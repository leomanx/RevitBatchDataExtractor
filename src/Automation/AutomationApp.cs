using System.IO;
using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using BatchExtractor;
using DesignAutomationFramework;

namespace RevitBatchDataExtractor.Automation;

/// <summary>
/// APS Automation (Design Automation for Revit) entry point.
/// Work item contract (files in the work item's current directory):
///   input.rvt   - model to extract (downloaded by the Activity, e.g. from an ACC signed URL)
///   params.json - extraction settings in the same schema as config.example.json;
///                 "models" and "outputDirectory" are ignored and replaced here.
///   result/     - output folder (extract.sqlite, CSVs, errors.jsonl, progress.log, summary.json),
///                 zipped by the Activity as the "result" output.
/// The Activity must NOT pass /i, so the model is opened here with detach options.
/// </summary>
public sealed class AutomationApp : IExternalDBApplication
{
    public const string InputModel = "input.rvt";
    public const string ParamsFile = "params.json";
    public const string ResultFolder = "result";

    public ExternalDBApplicationResult OnStartup(ControlledApplication application)
    {
        DesignAutomationBridge.DesignAutomationReadyEvent += OnReady;
        return ExternalDBApplicationResult.Succeeded;
    }

    public ExternalDBApplicationResult OnShutdown(ControlledApplication application)
    {
        DesignAutomationBridge.DesignAutomationReadyEvent -= OnReady;
        return ExternalDBApplicationResult.Succeeded;
    }

    private static void OnReady(object? sender, DesignAutomationReadyEventArgs e)
    {
        e.Succeeded = Process(e.DesignAutomationData.RevitApp, Directory.GetCurrentDirectory());
    }

    /// <summary>Runs one work item. Returns true when the model was extracted (fully or partially).</summary>
    public static bool Process(Application app, string workDir)
    {
        var result = Path.Combine(workDir, ResultFolder);
        Directory.CreateDirectory(result);
        try
        {
            var config = LoadConfig(workDir);
            Console.WriteLine($"RBDE: categories={config.Categories.Count} parameters={config.Parameters.Count} filters={config.Filters.Count}");
            var statuses = Extractor.Run(app, config, result);
            var status = statuses.Count > 0 ? statuses[0] : "failed";
            WriteSummary(result, status, null);
            Console.WriteLine("RBDE: model status = " + status);
            return status is "succeeded" or "partial";
        }
        catch (Exception ex)
        {
            Console.WriteLine("RBDE: fatal " + ex);
            WriteSummary(result, "failed", ex);
            return false;
        }
    }

    public static Config LoadConfig(string workDir)
    {
        var paramsPath = Path.Combine(workDir, ParamsFile);
        var config = File.Exists(paramsPath) ? Config.Load(paramsPath) : new Config();
        config.Models = [Path.Combine(workDir, InputModel)];
        config.OutputDirectory = Path.Combine(workDir, ResultFolder);
        config.EnableDashboard = false; // no browser in the cloud; the dashboard reads the downloaded sqlite locally
        config.Validate();
        return config;
    }

    private static void WriteSummary(string result, string status, Exception? error)
    {
        var summary = new { status, utc = DateTime.UtcNow.ToString("O"), error = error?.Message, engine = "Autodesk.Revit+2026" };
        File.WriteAllText(Path.Combine(result, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }
}
