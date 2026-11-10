using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BatchExtractor;
using RApplication = Autodesk.Revit.ApplicationServices.Application;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitBatchDataExtractor;

public sealed class Availability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

[Transaction(TransactionMode.Manual)]
public sealed class Command : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            using var picker = new OpenFileDialog { Title = "Select Batch Extractor configuration", Filter = "JSON configuration|*.json" };
            if (picker.ShowDialog() != DialogResult.OK) return Result.Cancelled;
            var config = Config.Load(picker.FileName);
            if (config.Models.Count == 0)
            {
                using var models = new OpenFileDialog { Title = "Select RVT models (multiple selection)", Filter = "Revit models|*.rvt", Multiselect = true };
                if (models.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                config.Models = models.FileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            var directory = Path.Combine(config.OutputDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "config.snapshot.json"), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            Extractor.Run(commandData.Application.Application, config, directory);
            var finished = new TaskDialog("Batch Data Extractor")
            {
                MainInstruction = "Batch finished",
                MainContent = "Review model status and errors before using the extracted data.\n\n" + directory,
                CommonButtons = TaskDialogCommonButtons.Close
            };
            var dashboard = Path.Combine(Path.GetDirectoryName(typeof(Command).Assembly.Location)!, "Dashboard", "RBDE.Dashboard.exe");
            if (config.EnableDashboard && File.Exists(dashboard))
                finished.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open Dashboard", "Review models, categories, parameters and error logs in your browser.");
            finished.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open output folder");
            var choice = finished.Show();
            try
            {
                if (choice == TaskDialogResult.CommandLink1)
                {
                    var start = new ProcessStartInfo(dashboard) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add(Path.Combine(directory, "extract.sqlite"));
                    Process.Start(start);
                }
                else if (choice == TaskDialogResult.CommandLink2) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception ex) { TaskDialog.Show("Dashboard", "Extraction finished, but opening the result failed: " + ex.Message); }
            return Result.Succeeded;
        }
        catch (Exception ex) { message = ex.ToString(); return Result.Failed; }
    }
}
