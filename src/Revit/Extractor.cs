using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using BatchExtractor;
using RApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitBatchDataExtractor;

/// <summary>
/// Headless extraction core shared by the desktop command and the APS Automation entry point.
/// Uses only RevitAPI (no RevitAPIUI, dialogs or selection) so it can run in revitcoreconsole.
/// </summary>
public static class Extractor
{
    /// <summary>Runs the batch and returns the final status of each model, in config order.</summary>
    public static IReadOnlyList<string> Run(RApplication app, Config config, string directory)
    {
        var statuses = new List<string>();
        using (var store = new Store(directory))
        {
            File.WriteAllText(Path.Combine(directory, "errors.jsonl"), "");
            var cancelPath = Path.Combine(directory, "STOP.txt");
            void CheckCancel() { if (File.Exists(cancelPath)) throw new OperationCanceledException("STOP.txt requested cancellation."); }
            void Progress(string value) => File.AppendAllText(Path.Combine(directory, "progress.log"), $"{DateTime.UtcNow:O} {value}{Environment.NewLine}");
            bool canceled = false;
            for (int index = 0; index < config.Models.Count; index++)
            {
                var path = config.Models[index]; int modelId = index + 1, count = 0;
                store.StartModel(modelId, path);
                if (canceled || File.Exists(cancelPath)) { canceled = true; store.FinishModel(modelId, "skipped_cancelled", 0); statuses.Add("skipped_cancelled"); continue; }
                Document? doc = null; bool transaction = false, partial = false;
                string stage = "open", status = "failed";
                Progress($"[{modelId}/{config.Models.Count}] Opening {path}");
                try
                {
                    CheckCancel();
                    if (!File.Exists(path) || !Path.GetExtension(path).Equals(".rvt", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Input must be an existing local/network .rvt file: " + path);
                    if (app.Documents.Cast<Document>().Any(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Model is already open in this Revit session. Close it before running the batch.");
                    using var info = BasicFileInfo.Extract(path);
                    if (info.IsSavedInLaterVersion) throw new InvalidOperationException("Model uses a newer Revit version.");
                    using var options = new OpenOptions();
                    if (info.IsWorkshared)
                    {
                        options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                        using var worksets = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
                        options.SetOpenWorksetsConfiguration(worksets);
                    }
                    doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options);
                    stage = "extract";
                    store.BeginModel(); transaction = true;
                    var typeCache = new Dictionary<long, List<ParameterRow>>();
                    using var collector = new FilteredElementCollector(doc);
                    var selected = config.IncludeTypes ? collector.WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(), new ElementIsElementTypeFilter(true))) : collector.WhereElementIsNotElementType();
                    foreach (var element in selected)
                    {
                        CheckCancel();
                        try
                        {
                            var category = element.Category;
                            if (config.Categories.Count > 0 && (category is null || !config.Categories.Any(c =>
                                c.Equals(category.Name, StringComparison.OrdinalIgnoreCase) || c.Equals(((BuiltInCategory)category.Id.Value).ToString(), StringComparison.OrdinalIgnoreCase) || c == category.Id.Value.ToString(CultureInfo.InvariantCulture)))) continue;
                            var isType = element is ElementType;
                            var type = isType ? (ElementType)element : doc.GetElement(element.GetTypeId()) as ElementType;
                            void ParameterError(Exception ex) { partial = true; store.Error(modelId, element.Id.Value, "parameter", ex); }
                            var parameters = ReadParameters(element, isType ? "type" : "instance", ParameterError);
                            if (!isType && type is not null)
                            {
                                if (!typeCache.TryGetValue(type.Id.Value, out var typeParameters))
                                { typeParameters = ReadParameters(type, "type", ParameterError); typeCache[type.Id.Value] = typeParameters; }
                                parameters.AddRange(typeParameters);
                            }
                            if (!config.Matches(parameters)) continue;
                            var row = new ElementRow(element.Id.Value, element.UniqueId, isType ? "type" : "instance",
                                category?.Id.Value.ToString(CultureInfo.InvariantCulture) ?? "", category?.Name ?? "",
                                type?.FamilyName ?? "", type?.Name ?? "", type?.Id.Value ?? -1, element.Name);
                            store.AddElement(modelId, row, parameters.Where(p => config.Parameters.Count == 0 || config.Parameters.Any(s => Rule.Selects(s, p))));
                            count++;
                            if (count % 1000 == 0) Progress($"[{modelId}] Exported {count} elements");
                        }
                        catch (Microsoft.Data.Sqlite.SqliteException) { throw; }
                        catch (IOException) { throw; }
                        catch (Exception ex) { partial = true; store.Error(modelId, element.Id.Value, "element", ex); }
                    }
                    store.CommitModel(); transaction = false;
                    status = partial ? "partial" : "succeeded";
                }
                catch (Exception ex)
                {
                    if (transaction) { store.RollbackModel(); transaction = false; count = 0; }
                    canceled = ex is OperationCanceledException;
                    status = canceled ? "cancelled" : "failed";
                    store.Error(modelId, null, stage, ex);
                }
                finally
                {
                    if (doc is not null)
                    {
                        try { if (!doc.Close(false)) throw new InvalidOperationException("Revit refused to close the background document."); }
                        catch (Exception ex) { store.Error(modelId, null, "close", ex); status = "close_failed"; canceled = true; }
                    }
                    store.FinishModel(modelId, status, count);
                    statuses.Add(status);
                    Progress($"[{modelId}/{config.Models.Count}] {status}; elements={count}");
                }
            }
            store.ReconcileErrors();
            if (config.ExportCsv) store.ExportCsv(config.CsvSafeForExcel);
            Progress("Batch complete");
        }
        if (!config.ExportSqlite && !config.EnableDashboard)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(Path.Combine(directory, "extract.sqlite"));
        }
        return statuses;
    }

    private static List<ParameterRow> ReadParameters(Element element, string scope, Action<Exception> error)
    {
        var rows = new List<ParameterRow>();
        foreach (Parameter parameter in element.Parameters)
        {
            try
            {
                var key = parameter.IsShared ? "guid:" + parameter.GUID.ToString("D") :
                    parameter.Id.Value < 0 ? "builtin:" + ((BuiltInParameter)parameter.Id.Value).ToString() : "id:" + parameter.Id.Value.ToString(CultureInfo.InvariantCulture);
                var raw = !parameter.HasValue ? "" : parameter.StorageType switch
                {
                    StorageType.String => parameter.AsString() ?? "",
                    StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
                    StorageType.Double => parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture),
                    StorageType.ElementId => parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture), _ => ""
                };
                var unit = "";
                if (parameter.StorageType == StorageType.Double && UnitUtils.IsMeasurableSpec(parameter.Definition.GetDataType())) unit = parameter.GetUnitTypeId().TypeId;
                rows.Add(new ParameterRow(scope, key, parameter.Definition.Name, parameter.StorageType.ToString(), parameter.HasValue,
                    raw, parameter.HasValue ? parameter.AsValueString() ?? raw : "", unit));
            }
            catch (Exception ex) { error(new InvalidOperationException($"Parameter read failed on element {element.Id.Value}: {parameter.Id.Value}", ex)); }
        }
        return rows;
    }
}
