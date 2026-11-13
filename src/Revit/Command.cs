using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BatchExtractor;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using TaskDialogCommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons;
using TaskDialogCommandLinkId = Autodesk.Revit.UI.TaskDialogCommandLinkId;
using TaskDialogResult = Autodesk.Revit.UI.TaskDialogResult;

namespace RevitBatchDataExtractor;

public sealed class Availability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

[Transaction(TransactionMode.Manual)]
public sealed class Command : IExternalCommand
{
    private const string RepoRoot = @"E:\Work\BIM\scripts\Dynamo\RevitBatchDataExtractor";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var hubDialog = new TaskDialog("Revit Batch Data Extractor (RBDE)")
            {
                MainInstruction = "Select Extraction or QA/QC Workflow",
                MainContent = "Choose whether to extract from ACC Cloud, inspect/fix issues in the active model (Host + Revit Links), or run a local offline RVT batch.",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };

            hubDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                "1. Launch APS Cloud Batch Extractor (Recommended)",
                "Open the standalone WPF app (RBDE.CloudApp.exe) to select ACC Hub, Project, Folder, and extract in seconds.");

            hubDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                "2. Open BIM Fix & Link Navigator (Active Model + Links)",
                "Open the modeless issue navigator to Select, Zoom, 3D Section Box (Host & Linked Elements), and write authorized parameter fixes.");

            hubDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink3,
                "3. Run Local Offline RVT Batch Extractor (config.sample.json)",
                "Extract local .rvt files on disk using E:\\Work\\BIM\\scripts\\Dynamo\\RevitBatchDataExtractor\\config.sample.json.");

            var mode = hubDialog.Show();
            if (mode == TaskDialogResult.CommandLink1)
            {
                var cloudExe = Path.Combine(RepoRoot, "bin", "CloudApp", "RBDE.CloudApp.exe");
                if (!File.Exists(cloudExe))
                {
                    TaskDialog.Show("RBDE", "CloudApp executable not found at:\n" + cloudExe);
                    return Result.Cancelled;
                }
                Process.Start(new ProcessStartInfo(cloudExe) { UseShellExecute = true });
                return Result.Succeeded;
            }

            if (mode == TaskDialogResult.CommandLink2)
            {
                var fixCmd = new FixAssistantCommand();
                return fixCmd.Execute(commandData, ref message, elements);
            }

            if (mode != TaskDialogResult.CommandLink3)
                return Result.Cancelled;

            var defaultConfig = Path.Combine(RepoRoot, "config.sample.json");
            using var picker = new OpenFileDialog
            {
                Title = "Select Batch Extractor configuration (e.g. config.sample.json)",
                Filter = "JSON configuration|*.json",
                InitialDirectory = Directory.Exists(RepoRoot) ? RepoRoot : Environment.CurrentDirectory,
                FileName = File.Exists(defaultConfig) ? "config.sample.json" : ""
            };
            if (picker.ShowDialog() != DialogResult.OK) return Result.Cancelled;

            var config = Config.Load(picker.FileName);
            if (config.Models.Count == 0)
            {
                using var models = new OpenFileDialog
                {
                    Title = "Select RVT models (multiple selection)",
                    Filter = "Revit models|*.rvt",
                    Multiselect = true,
                    InitialDirectory = Directory.Exists(RepoRoot) ? RepoRoot : Environment.CurrentDirectory
                };
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
                else if (choice == TaskDialogResult.CommandLink2)
                {
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Dashboard", "Extraction finished, but opening the result failed: " + ex.Message);
            }
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            return Result.Failed;
        }
    }
}
