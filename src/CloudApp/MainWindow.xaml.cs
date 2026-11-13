using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace RBDE.CloudApp;

public sealed class ScheduledProfile
{
    public string HubId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string FolderName { get; set; } = "";
    public bool ExtractAllCategories { get; set; } = true;
    public List<string> Categories { get; set; } = [];
    public bool ExtractAllParameters { get; set; } = true;
    public List<string> PriorityColumns { get; set; } = [];
    public bool SkipUnchangedVersions { get; set; }
}

public sealed record ElementDiffRecord(
    int ModelId,
    string ChangeType, // "Added", "Removed", "Modified"
    long ElementId,
    string UniqueId,
    string Category,
    string Family,
    string TypeName,
    string ParameterName,
    string OldValue,
    string NewValue);

public partial class MainWindow : Window
{
    private readonly ApsClient aps = new();
    private readonly string baseDir;
    private readonly string dashScript;
    private readonly string outputRoot;
    private readonly string rulesPath;
    private List<ModelFileItem> currentModels = [];
    private readonly List<CategoryOptionItem> categoryOptions = [];
    private bool suppressPresetEvent;

    public MainWindow()
    {
        InitializeComponent();

        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(exeDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Open-Dashboard.ps1")))
        {
            dir = dir.Parent;
        }

        baseDir = dir?.FullName ?? @"E:\Work\BIM\scripts\Dynamo\RevitBatchDataExtractor";
        dashScript = Path.Combine(baseDir, "Open-Dashboard.ps1");
        outputRoot = Path.Combine(baseDir, "output");
        rulesPath = Path.Combine(baseDir, "rules.ids.json");

        InitializeSchemaControls();
        Loaded += MainWindow_Loaded;
    }

    private void InitializeSchemaControls()
    {
        categoryOptions.Clear();
        foreach (var (name, discipline) in AecSchemaPresets.StandardCategories)
        {
            categoryOptions.Add(new CategoryOptionItem
            {
                Name = name,
                Discipline = discipline,
                IsSelected = true
            });
        }

        ItemsCategories.ItemsSource = categoryOptions;
        TxtPriorityColumns.Text = string.Join(", ", AecSchemaPresets.DefaultPriorityColumns);
        UpdateCategorySummary();
    }

    private void UpdateCategorySummary()
    {
        if (TxtCategorySummary == null) return;
        var selectedCount = categoryOptions.Count(c => c.IsSelected);
        if (ChkExtractAllCategories?.IsChecked == true)
        {
            TxtCategorySummary.Text = $"Unrestricted mode: All categories in model will be extracted ({selectedCount}/{categoryOptions.Count} preset categories checked).";
        }
        else
        {
            TxtCategorySummary.Text = $"Filtered mode: {selectedCount} of {categoryOptions.Count} AEC categories selected for extraction.";
        }
    }

    private void CmbDisciplinePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPresetEvent || categoryOptions.Count == 0) return;
        if (CmbDisciplinePreset.SelectedItem is not ComboBoxItem item) return;

        var label = item.Content?.ToString() ?? "";
        if (label.StartsWith("Custom", StringComparison.OrdinalIgnoreCase)) return;

        suppressPresetEvent = true;
        try
        {
            if (label.StartsWith("All Categories", StringComparison.OrdinalIgnoreCase))
            {
                ChkExtractAllCategories.IsChecked = true;
                foreach (var c in categoryOptions) c.IsSelected = true;
            }
            else if (label.StartsWith("Architecture", StringComparison.OrdinalIgnoreCase))
            {
                ChkExtractAllCategories.IsChecked = false;
                foreach (var c in categoryOptions)
                    c.IsSelected = c.Discipline is "Architecture" or "Interior" or "Site";
            }
            else if (label.StartsWith("Structure", StringComparison.OrdinalIgnoreCase))
            {
                ChkExtractAllCategories.IsChecked = false;
                var structNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Walls", "Floors", "Roofs", "Columns", "Ramps", "Stairs"
                };
                foreach (var c in categoryOptions)
                    c.IsSelected = c.Discipline == "Structure" || structNames.Contains(c.Name);
            }
            else if (label.StartsWith("MEP", StringComparison.OrdinalIgnoreCase))
            {
                ChkExtractAllCategories.IsChecked = false;
                foreach (var c in categoryOptions)
                    c.IsSelected = c.Discipline == "MEP";
            }
            else if (label.StartsWith("Rooms", StringComparison.OrdinalIgnoreCase))
            {
                ChkExtractAllCategories.IsChecked = false;
                var spatialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Rooms", "Areas", "Spaces", "Zones", "Doors", "Windows", "Walls", "Floors", "Ceilings", "Furniture"
                };
                foreach (var c in categoryOptions)
                    c.IsSelected = spatialNames.Contains(c.Name);
            }
            else if (label.StartsWith("QTO", StringComparison.OrdinalIgnoreCase))
            {
                ChkExtractAllCategories.IsChecked = false;
                var qtoExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Areas", "Spaces", "Zones", "Mass"
                };
                foreach (var c in categoryOptions)
                    c.IsSelected = !qtoExclude.Contains(c.Name);
            }

            ItemsCategories.Items.Refresh();
            UpdateCategorySummary();
        }
        finally
        {
            suppressPresetEvent = false;
        }
    }

    private void ChkExtractAllCategories_Changed(object sender, RoutedEventArgs e) => UpdateCategorySummary();

    private void CategoryItem_Changed(object sender, RoutedEventArgs e)
    {
        if (suppressPresetEvent) return;
        UpdateCategorySummary();
    }

    private void BtnCheckAllCats_Click(object sender, RoutedEventArgs e)
    {
        suppressPresetEvent = true;
        foreach (var c in categoryOptions) c.IsSelected = true;
        ItemsCategories.Items.Refresh();
        suppressPresetEvent = false;
        UpdateCategorySummary();
    }

    private void BtnUncheckAllCats_Click(object sender, RoutedEventArgs e)
    {
        suppressPresetEvent = true;
        ChkExtractAllCategories.IsChecked = false;
        foreach (var c in categoryOptions) c.IsSelected = false;
        ItemsCategories.Items.Refresh();
        suppressPresetEvent = false;
        UpdateCategorySummary();
    }

    private void BtnResetHeaders_Click(object sender, RoutedEventArgs e)
    {
        TxtPriorityColumns.Text = string.Join(", ", AecSchemaPresets.DefaultPriorityColumns);
    }

    private async void BtnDiscoverSchema_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProjects.SelectedItem is not ProjectItem proj)
        {
            MessageBox.Show(this, "Please select an ACC Project first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetModel = currentModels.FirstOrDefault(m => m.IsSelected) ?? currentModels.FirstOrDefault();
        if (targetModel == null)
        {
            MessageBox.Show(this, "No RVT model available in the current folder to scan.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnDiscoverSchema.IsEnabled = false;
        TxtProgressStatus.Text = $"Scanning live AEC schema from '{targetModel.Name}'...";
        Log($"[{DateTime.Now:HH:mm:ss}] Inspecting live categories and parameters in '{targetModel.Name}'...");

        try
        {
            var (discoveredCats, discoveredParams) = await aps.DiscoverModelSchemaAsync(proj.Id, targetModel);

            int newCatCount = 0;
            foreach (var cat in discoveredCats)
            {
                var existing = categoryOptions.FirstOrDefault(c => c.Name.Equals(cat, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    categoryOptions.Add(new CategoryOptionItem
                    {
                        Name = cat,
                        Discipline = "Discovered",
                        IsSelected = true
                    });
                    newCatCount++;
                }
                else
                {
                    existing.IsSelected = true;
                }
            }

            ItemsCategories.Items.Refresh();
            UpdateCategorySummary();

            Log($"[OK] Schema scan complete for '{targetModel.Name}':");
            Log($"     Categories present in model : {discoveredCats.Count} ({newCatCount} custom/additional added to checklist)");
            Log($"     Distinct Parameters present : {discoveredParams.Count}");
            Log($"     Top 20 Populated Parameters : {string.Join(", ", discoveredParams.Take(20))}");

            TxtProgressStatus.Text = $"Schema scan complete: {discoveredCats.Count} categories, {discoveredParams.Count} distinct parameters found.";
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Schema scan failed: {ex.Message}");
            TxtProgressStatus.Text = "Schema scan failed.";
        }
        finally
        {
            BtnDiscoverSchema.IsEnabled = true;
        }
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtLog.AppendText(message + Environment.NewLine);
            TxtLog.ScrollToEnd();
        });
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var cmdArgs = Environment.GetCommandLineArgs();

        var capIdx = Array.IndexOf(cmdArgs, "--capture-docs");
        if (capIdx >= 0 && capIdx + 1 < cmdArgs.Length)
        {
            await CaptureDocumentationScreenshotsAsync(cmdArgs[capIdx + 1]);
            Application.Current.Shutdown();
            return;
        }

        var batchIdx = Array.IndexOf(cmdArgs, "--headless-batch");
        if (batchIdx >= 0 && batchIdx + 1 < cmdArgs.Length)
        {
            await RunHeadlessScheduledBatchAsync(cmdArgs[batchIdx + 1]);
            Application.Current.Shutdown();
            return;
        }

        if (!aps.HasCredentials)
        {
            OpenSettingsModal("APS credentials not found. Please enter Client ID and Client Secret to connect.");
            return;
        }

        await InitializeHubConnectionAsync();
    }

    private async Task RunHeadlessScheduledBatchAsync(string profilePath)
    {
        if (!File.Exists(profilePath)) return;
        var profile = JsonSerializer.Deserialize<ScheduledProfile>(await File.ReadAllTextAsync(profilePath));
        if (profile == null) return;

        var models = await aps.GetRvtModelsAsync(profile.ProjectId, profile.FolderId);
        if (models.Count == 0) return;

        var categories = new HashSet<string>(profile.Categories, StringComparer.OrdinalIgnoreCase);
        var targetParams = BuildTargetParameterSet(profile.PriorityColumns);

        await ExecuteBatchExtractionAsync(
            profile.ProjectId,
            profile.ProjectName,
            models,
            profile.ExtractAllCategories,
            categories,
            profile.ExtractAllParameters,
            targetParams,
            profile.PriorityColumns,
            skipUnchanged: profile.SkipUnchangedVersions,
            isHeadless: true);
    }

    private async Task CaptureDocumentationScreenshotsAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);

        var demoHub = new HubItem("b.demo-hub", "ACC Enterprise Hub");
        var demoProj = new ProjectItem("b.demo-proj", "2026 Mixed-Use Tower Development");
        var demoFolder = new FolderItem("urn:demo:wip", "Project Files / 01_WIP_Models");

        CmbHubs.SelectionChanged -= CmbHubs_SelectionChanged;
        CmbProjects.SelectionChanged -= CmbProjects_SelectionChanged;
        CmbFolders.SelectionChanged -= CmbFolders_SelectionChanged;

        CmbHubs.ItemsSource = new List<HubItem> { demoHub };
        CmbHubs.SelectedItem = demoHub;
        CmbProjects.ItemsSource = new List<ProjectItem> { demoProj };
        CmbProjects.SelectedItem = demoProj;
        CmbFolders.ItemsSource = new List<FolderItem> { demoFolder };
        CmbFolders.SelectedItem = demoFolder;

        TxtHubName.Text = "Hub: ACC Enterprise Hub";
        TxtStatusBadge.Text = "Connected (DPAPI Secured)";

        currentModels =
        [
            new ModelFileItem { Id = "1", Name = "ARC_Main_Tower_R26.rvt (v12)", IsSelected = true, Status = "Done (17,425 el / 84,620 pr)" },
            new ModelFileItem { Id = "2", Name = "STR_Superstructure_R26.rvt (v8)", IsSelected = true, Status = "Done (8,912 el / 41,205 pr)" },
            new ModelFileItem { Id = "3", Name = "MEP_HVAC_Piping_R26.rvt (v15)", IsSelected = true, Status = "Done (14,310 el / 68,110 pr)" },
            new ModelFileItem { Id = "4", Name = "INT_Podium_Fitout_R26.rvt (v6)", IsSelected = true, Status = "Done (6,180 el / 29,400 pr)" },
            new ModelFileItem { Id = "5", Name = "SITE_Landscape_Civil_R26.rvt (v3)", IsSelected = false, Status = "Ready" }
        ];
        LstModels.ItemsSource = currentModels;
        UpdateSelectedCount();

        ProgBatch.Maximum = 4;
        ProgBatch.Value = 4;
        TxtProgressStatus.Text = "Extraction, Element Diff & Rule Lifecycle completed for 4 model(s) in 34.2s.";

        TxtLog.Text = string.Join(Environment.NewLine,
            "[10:15:02] Authenticating with Autodesk Platform Services (DPAPI Encrypted Secret)...",
            "[OK] Connected to Hub: ACC Enterprise Hub",
            "============================================================",
            ">>> [1/4] ARC_Main_Tower_R26.rvt (v12) -> View '{3D}' -> 17,425 elements, 84,620 parameters (8.9s)",
            ">>> [2/4] STR_Superstructure_R26.rvt (v8) -> View '{3D}' -> 8,912 elements, 41,205 parameters (7.1s)",
            ">>> [3/4] MEP_HVAC_Piping_R26.rvt (v15) -> View '{3D}' -> 14,310 elements, 68,110 parameters (10.4s)",
            ">>> [4/4] INT_Podium_Fitout_R26.rvt (v6) -> View '{3D}' -> 6,180 elements, 29,400 parameters (6.2s)",
            "[QA/QC] Evaluated 7 IDS-inspired specifications + Element Diff + Issue Lifecycle.",
            "[SUCCESS] Persisted extract.sqlite, warehouse.sqlite, elements_wide.csv, and fix_candidates.json.");

        await Task.Delay(350);
        UpdateLayout();
        TxtLog.ScrollToEnd();
        await Task.Delay(100);
        SaveWindowPng(Path.Combine(outDir, "cloud-extractor-main.png"));

        TxtClientId.Text = "YOUR_APS_CLIENT_ID_HERE";
        PwdClientSecret.Password = "********************************";
        TxtModalStatus.Text = "Secret is encrypted at rest using Windows DPAPI (CurrentUser scope). Plaintext environment variables are scrubbed automatically.";
        ModalSettings.Visibility = Visibility.Visible;

        await Task.Delay(250);
        UpdateLayout();
        SaveWindowPng(Path.Combine(outDir, "cloud-extractor-settings.png"));
    }

    private void SaveWindowPng(string filePath)
    {
        if (Content is not FrameworkElement root) return;
        int w = (int)Math.Max(1200, root.ActualWidth);
        int h = (int)Math.Max(800, root.ActualHeight);

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private async Task InitializeHubConnectionAsync()
    {
        TxtStatusBadge.Text = "Connecting...";
        Log($"[{DateTime.Now:HH:mm:ss}] Authenticating with Autodesk Platform Services (DPAPI Protected Store)...");

        try
        {
            var hubs = await aps.GetHubsAsync();
            CmbHubs.ItemsSource = hubs;

            var defaultHub = hubs.FirstOrDefault();
            if (defaultHub != null)
            {
                CmbHubs.SelectedItem = defaultHub;
                TxtHubName.Text = $"Hub: {defaultHub.Name}";
                TxtStatusBadge.Text = "Connected (DPAPI)";
                Log($"[OK] Connected to Hub: {defaultHub.Name} ({hubs.Count} hub(s) accessible)");
            }
            else
            {
                TxtHubName.Text = "No Hubs found";
                TxtStatusBadge.Text = "Warning";
                Log("[WARN] No accessible hubs found for this Client ID. Ensure Custom Integration is active in Account Admin.");
            }
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Connection failed: {ex.Message}");
            TxtStatusBadge.Text = "Error";
            OpenSettingsModal("Failed to connect with current credentials. Please check Client ID and Secret:\n" + ex.Message);
        }
    }

    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsModal();

    private void OpenSettingsModal(string? statusMessage = null)
    {
        TxtClientId.Text = aps.ClientId ?? "";
        PwdClientSecret.Password = "";
        TxtModalStatus.Text = statusMessage ?? (aps.HasCredentials
            ? "Encrypted Client Secret is active in Windows DPAPI. Leave Secret blank to keep current, or type a new Secret to rotate."
            : "Enter your APS Client ID and Client Secret. Secret will be encrypted via Windows DPAPI.");
        ModalSettings.Visibility = Visibility.Visible;
    }

    private void BtnCloseModal_Click(object sender, RoutedEventArgs e)
    {
        PwdClientSecret.Password = "";
        ModalSettings.Visibility = Visibility.Collapsed;
    }

    private async void BtnSaveSync_Click(object sender, RoutedEventArgs e)
    {
        var id = TxtClientId.Text.Trim();
        var secret = PwdClientSecret.Password.Trim();

        if (string.IsNullOrEmpty(id) || (string.IsNullOrEmpty(secret) && !aps.HasCredentials))
        {
            TxtModalStatus.Text = "Both Client ID and Client Secret are required.";
            return;
        }

        BtnSaveSync.IsEnabled = false;
        TxtModalStatus.Text = "Encrypting secret with Windows DPAPI and testing connection to APS...";

        try
        {
            aps.SaveCredentials(id, string.IsNullOrEmpty(secret) ? null : secret);
            PwdClientSecret.Password = "";
            await aps.EnsureTokenAsync();
            TxtModalStatus.Text = "Authentication succeeded! Syncing Hubs and Projects...";

            await InitializeHubConnectionAsync();
            TxtModalStatus.Text = "Connected and encrypted successfully!";
            await Task.Delay(700);
            ModalSettings.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TxtModalStatus.Text = "Connection test failed: " + ex.Message;
        }
        finally
        {
            BtnSaveSync.IsEnabled = true;
        }
    }

    private async void CmbHubs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbHubs.SelectedItem is not HubItem hub) return;
        TxtHubName.Text = $"Hub: {hub.Name}";
        Log($"Active Hub: {hub.Name}");
        TxtProgressStatus.Text = $"Loading projects in {hub.Name}...";

        try
        {
            var projects = await aps.GetProjectsAsync(hub.Id);
            CmbProjects.ItemsSource = projects;

            var defaultProj = projects.FirstOrDefault();
            if (defaultProj != null)
            {
                CmbProjects.SelectedItem = defaultProj;
            }
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Could not load projects: {ex.Message}");
        }
    }

    private async void CmbProjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbHubs.SelectedItem is not HubItem hub) return;
        if (CmbProjects.SelectedItem is not ProjectItem proj) return;

        Log($"Selected Project: {proj.Name}");
        TxtProgressStatus.Text = $"Loading folders in {proj.Name}...";

        try
        {
            var folders = await aps.GetFoldersAsync(hub.Id, proj.Id);
            CmbFolders.ItemsSource = folders;

            var defaultFolder = folders.FirstOrDefault(f => f.Name.Contains("WIP", StringComparison.OrdinalIgnoreCase)) ?? folders.FirstOrDefault();
            if (defaultFolder != null)
            {
                CmbFolders.SelectedItem = defaultFolder;
            }
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Could not load folders: {ex.Message}");
        }
    }

    private async void CmbFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbProjects.SelectedItem is not ProjectItem proj) return;
        if (CmbFolders.SelectedItem is not FolderItem folder) return;

        Log($"Selected Folder: {folder.Name}");
        TxtProgressStatus.Text = $"Scanning for RVT models in {folder.Name}...";

        try
        {
            currentModels = await aps.GetRvtModelsAsync(proj.Id, folder.Id);
            LstModels.ItemsSource = currentModels;
            UpdateSelectedCount();
            TxtProgressStatus.Text = $"Found {currentModels.Count} RVT model(s) ready for extraction.";
            Log($"[OK] Found {currentModels.Count} RVT model(s) in folder '{folder.Name}'");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Could not load models: {ex.Message}");
        }
    }

    private void UpdateSelectedCount()
    {
        var selected = currentModels.Count(m => m.IsSelected);
        TxtSelectedCount.Text = $"Selected: {selected} / {currentModels.Count} models";
    }

    private void ModelCheck_Changed(object sender, RoutedEventArgs e) => UpdateSelectedCount();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in currentModels) m.IsSelected = true;
        LstModels.Items.Refresh();
        UpdateSelectedCount();
    }

    private void BtnClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in currentModels) m.IsSelected = false;
        LstModels.Items.Refresh();
        UpdateSelectedCount();
    }

    private List<string> ParsePriorityColumns()
    {
        var raw = TxtPriorityColumns.Text ?? "";
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                list.Add(trimmed);
            }
        }
        return list;
    }

    private HashSet<string> BuildTargetParameterSet(List<string> priorityColumns)
    {
        var set = new HashSet<string>(priorityColumns, StringComparer.OrdinalIgnoreCase);

        if (ChkGrpIdentity.IsChecked == true)
            foreach (var p in AecSchemaPresets.ParameterGroups["Identity"]) set.Add(p);
        if (ChkGrpSpatial.IsChecked == true)
            foreach (var p in AecSchemaPresets.ParameterGroups["Spatial"]) set.Add(p);
        if (ChkGrpQuantities.IsChecked == true)
            foreach (var p in AecSchemaPresets.ParameterGroups["Quantities"]) set.Add(p);
        if (ChkGrpMaterials.IsChecked == true)
            foreach (var p in AecSchemaPresets.ParameterGroups["Materials"]) set.Add(p);
        if (ChkGrpMep.IsChecked == true)
            foreach (var p in AecSchemaPresets.ParameterGroups["MEP"]) set.Add(p);
        if (ChkGrpLifecycle.IsChecked == true)
            foreach (var p in AecSchemaPresets.ParameterGroups["Lifecycle"]) set.Add(p);

        return set;
    }

    private void BtnScheduleTask_Click(object sender, RoutedEventArgs e)
    {
        if (CmbHubs.SelectedItem is not HubItem hub ||
            CmbProjects.SelectedItem is not ProjectItem proj ||
            CmbFolders.SelectedItem is not FolderItem folder)
        {
            MessageBox.Show(this, "Please select a Hub, Project, and Folder before scheduling automatic extraction.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMK_BIM", "RBDE");
        Directory.CreateDirectory(profileDir);
        var profilePath = Path.Combine(profileDir, "scheduled-profile.json");

        var profile = new ScheduledProfile
        {
            HubId = hub.Id,
            ProjectId = proj.Id,
            ProjectName = proj.Name,
            FolderId = folder.Id,
            FolderName = folder.Name,
            ExtractAllCategories = ChkExtractAllCategories.IsChecked == true,
            Categories = categoryOptions.Where(c => c.IsSelected).Select(c => c.Name).ToList(),
            ExtractAllParameters = ChkExtractAllParams.IsChecked == true,
            PriorityColumns = ParsePriorityColumns(),
            SkipUnchangedVersions = ChkSkipUnchanged.IsChecked == true
        };

        File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        var exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(baseDir, "bin", "CloudApp", "RBDE.CloudApp.exe");

        var taskName = "MMK_BIM_Weekly_Cloud_Extract";
        var trArg = $"\\\"{exePath}\\\" --headless-batch \\\"{profilePath}\\\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{taskName}\" /TR \"{trArg}\" /SC WEEKLY /D MON /ST 07:00 /F",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(8000);

            if (proc.ExitCode == 0)
            {
                Log($"[SCHEDULED] Registered Windows Scheduled Task '{taskName}' (Every Monday at 07:00 AM) for '{proj.Name} / {folder.Name}'.");
                MessageBox.Show(this,
                    $"Scheduled Task '{taskName}' registered successfully!\n\n" +
                    $"• Target: {proj.Name} ({folder.Name})\n" +
                    $"• Schedule: Every Monday at 07:00 AM\n" +
                    $"• Profile: {profilePath}\n\n" +
                    "You can also run extractions manually at any time from this app or the Revit ribbon button.",
                    "Scheduled Extraction Active", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var err = proc.StandardError.ReadToEnd();
                Log($"[WARN] Could not register Windows Task Scheduler entry automatically: {err}. Saved profile to {profilePath}.");
                MessageBox.Show(this, $"Saved batch profile to:\n{profilePath}\n\nTask Scheduler note: {err}", "Profile Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Saved profile to {profilePath}.\nCould not invoke schtasks: {ex.Message}", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnStartExtract_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProjects.SelectedItem is not ProjectItem proj)
        {
            MessageBox.Show(this, "Please select an ACC Project first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedModels = currentModels.Where(m => m.IsSelected).ToList();
        if (selectedModels.Count == 0)
        {
            MessageBox.Show(this, "Please select at least one RVT model to extract.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extractAllCategories = ChkExtractAllCategories.IsChecked == true;
        var categories = new HashSet<string>(
            categoryOptions.Where(c => c.IsSelected).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        if (!extractAllCategories && categories.Count == 0)
        {
            MessageBox.Show(this, "Please select at least one category or enable 'Extract All Categories Found in Model'.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var priorityColumns = ParsePriorityColumns();
        var extractAllParams = ChkExtractAllParams.IsChecked == true;
        var targetParams = BuildTargetParameterSet(priorityColumns);
        var skipUnchanged = ChkSkipUnchanged.IsChecked == true;

        await ExecuteBatchExtractionAsync(
            proj.Id,
            proj.Name,
            selectedModels,
            extractAllCategories,
            categories,
            extractAllParams,
            targetParams,
            priorityColumns,
            skipUnchanged,
            isHeadless: false);
    }

    private bool IsModelVersionAlreadyInWarehouse(string projectId, ModelFileItem model)
    {
        var warehousePath = Path.Combine(outputRoot, "warehouse.sqlite");
        if (!File.Exists(warehousePath)) return false;

        try
        {
            using var conn = new SqliteConnection("Data Source=" + warehousePath);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 1 FROM model_versions
                WHERE project_id = $pid AND lineage_id = $lid AND version_id = $vid
                LIMIT 1";
            cmd.Parameters.AddWithValue("$pid", projectId);
            cmd.Parameters.AddWithValue("$lid", model.Id);
            cmd.Parameters.AddWithValue("$vid", model.VersionId);
            return cmd.ExecuteScalar() != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task ExecuteBatchExtractionAsync(
        string projectId,
        string projectName,
        List<ModelFileItem> selectedModels,
        bool extractAllCategories,
        HashSet<string> categories,
        bool extractAllParams,
        HashSet<string> targetParams,
        List<string> priorityColumns,
        bool skipUnchanged,
        bool isHeadless)
    {
        if (!isHeadless)
        {
            BtnStartExtract.IsEnabled = false;
            BtnDashboard.IsEnabled = false;
            ProgBatch.Maximum = selectedModels.Count;
            ProgBatch.Value = 0;
        }

        Log("\n============================================================");
        Log($"[{DateTime.Now:HH:mm:ss}] Initiating Batch Extraction ({selectedModels.Count} model(s))...");
        Log($"Project           : {projectName}");
        Log($"Category Scope    : {(extractAllCategories ? "ALL Model Categories" : $"{categories.Count} Selected Categories")}");
        Log($"Parameter Scope   : {(extractAllParams ? "ALL Model Parameters (Full Dump)" : $"{targetParams.Count} Targeted Parameters")}");
        Log("============================================================");

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(outputRoot, $"batch-{timestamp}");
        Directory.CreateDirectory(runDir);

        var sw = Stopwatch.StartNew();
        var allElements = new List<(int modelId, ExtractedElement elem)>();
        var allParameters = new List<(int modelId, ExtractedParam param)>();
        var modelRows = new List<(int modelId, ModelFileItem item, string status, int count)>();
        var errorLogs = new List<(int modelId, string stage, string type, string message)>();
        RuleEvaluationSummary? evalSummary = null;
        int fixedIssuesCount = 0;
        int newIssuesCount = 0;
        int regressedIssuesCount = 0;
        int addedElemsCount = 0;
        int removedElemsCount = 0;
        int modifiedElemsCount = 0;

        await Task.Run(async () =>
        {
            for (int i = 0; i < selectedModels.Count; i++)
            {
                var model = selectedModels[i];
                var modelId = i + 1;

                if (skipUnchanged && IsModelVersionAlreadyInWarehouse(projectId, model))
                {
                    Log($"\n>>> [{modelId}/{selectedModels.Count}] {model.Name} -> Skipped (Version already extracted in warehouse)");
                    modelRows.Add((modelId, model, "skipped_unchanged", 0));
                    if (!isHeadless)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ProgBatch.Value = i + 1;
                            model.Status = "Skipped (Unchanged)";
                            LstModels.Items.Refresh();
                        });
                    }
                    continue;
                }

                if (!isHeadless)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgBatch.Value = i;
                        TxtProgressStatus.Text = $"[{modelId}/{selectedModels.Count}] Processing: {model.Name}...";
                        model.Status = "Extracting...";
                        LstModels.Items.Refresh();
                    });
                }

                Log($"\n>>> [{modelId}/{selectedModels.Count}] {model.Name}");
                try
                {
                    var (elements, paramValues) = await aps.ExtractModelAsync(
                        projectId,
                        model,
                        extractAllCategories,
                        categories,
                        extractAllParams,
                        targetParams,
                        Log);

                    foreach (var el in elements) allElements.Add((modelId, el));
                    foreach (var pr in paramValues) allParameters.Add((modelId, pr));

                    modelRows.Add((modelId, model, "succeeded", elements.Count));
                    if (!isHeadless)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            model.Status = $"Done ({elements.Count:N0} el / {paramValues.Count:N0} pr)";
                            LstModels.Items.Refresh();
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] {model.Name} failed (continuing remaining batch): {ex.Message}");
                    modelRows.Add((modelId, model, "failed", 0));
                    errorLogs.Add((modelId, "ModelDerivativeExtract", ex.GetType().Name, ex.Message));
                    if (!isHeadless)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            model.Status = "Failed";
                            LstModels.Items.Refresh();
                        });
                    }
                }
            }

            // Evaluate IDS-inspired Rules with Data Confidence
            var specs = AecRuleEngine.LoadSpecifications(rulesPath);
            evalSummary = AecRuleEngine.Evaluate(
                specs,
                allElements,
                allParameters,
                extractAllCategories,
                categories,
                extractAllParams,
                targetParams);

            Log($"\n[DATA CONFIDENCE] Applicable Checks: {evalSummary.ApplicableChecks:N0} | Evaluated: {evalSummary.EvaluatedChecks:N0} | Not Evaluated: {evalSummary.NotEvaluatedChecks:N0}");
            Log($"[RULE ENGINE]     Passed: {evalSummary.PassedChecks:N0} | Failed: {evalSummary.FailedChecks:N0} | Invalid Numeric: {evalSummary.InvalidValueChecks:N0}");

            Log("Computing Element-Level Diffs (Added / Removed / Modified) & Issue Lifecycle (New / Active / Fixed / Regressed)...");
            (fixedIssuesCount, newIssuesCount, regressedIssuesCount, addedElemsCount, removedElemsCount, modifiedElemsCount) =
                SaveToDatabaseAndCsv(
                    runDir,
                    outputRoot,
                    timestamp,
                    projectId,
                    projectName,
                    modelRows,
                    allElements,
                    allParameters,
                    evalSummary,
                    errorLogs,
                    priorityColumns);
        });

        sw.Stop();
        if (!isHeadless)
        {
            ProgBatch.Value = selectedModels.Count;
            TxtProgressStatus.Text = $"Completed in {sw.Elapsed.TotalSeconds:F1}s | Fixed: {fixedIssuesCount}, New: {newIssuesCount}, Regressed: {regressedIssuesCount}";
            BtnStartExtract.IsEnabled = true;
            BtnDashboard.IsEnabled = true;
        }

        Log("\n============================================================");
        Log($"[SUCCESS] Batch Extraction & Lifecycle Audit Completed!");
        Log($"Total Elements   : {allElements.Count:N0} (+{addedElemsCount} Added, -{removedElemsCount} Removed, ~{modifiedElemsCount} Modified)");
        Log($"Issue Lifecycle  : Fixed: {fixedIssuesCount} | New: {newIssuesCount} | Regressed: {regressedIssuesCount}");
        Log($"Elapsed Time     : {sw.Elapsed.TotalSeconds:F1} seconds");
        Log($"Output Directory : {runDir}");
        Log("============================================================");

        if (!isHeadless)
        {
            var res = MessageBox.Show(this,
                $"Batch extraction & Decision Audit completed in {sw.Elapsed.TotalSeconds:F1}s!\n\n" +
                $"• Elements Extracted: {allElements.Count:N0}\n" +
                $"• Element Diff vs Last Run: +{addedElemsCount} Added, -{removedElemsCount} Removed, ~{modifiedElemsCount} Modified\n" +
                $"• Issue Lifecycle: {fixedIssuesCount} Fixed, {newIssuesCount} New, {regressedIssuesCount} Regressed\n\n" +
                "Would you like to open the Decision & Review Dashboard now?",
                "Extraction Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (res == MessageBoxResult.Yes)
            {
                OpenDashboard(runDir);
            }
        }
    }

    private static string EscapeCsv(string? val)
    {
        if (string.IsNullOrEmpty(val)) return "\"\"";
        var s = val;
        if (s.AsSpan().TrimStart().Length > 0 && "=+@-".Contains(s.AsSpan().TrimStart()[0]))
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static (int fixedIssues, int newIssues, int regressedIssues, int addedElems, int removedElems, int modifiedElems) SaveToDatabaseAndCsv(
        string runDir,
        string outputRoot,
        string runId,
        string projectId,
        string projectName,
        List<(int modelId, ModelFileItem item, string status, int count)> models,
        List<(int modelId, ExtractedElement elem)> elements,
        List<(int modelId, ExtractedParam param)> parameters,
        RuleEvaluationSummary evalSummary,
        List<(int modelId, string stage, string type, string message)> errorLogs,
        List<string> priorityColumns)
    {
        Directory.CreateDirectory(outputRoot);
        var warehousePath = Path.Combine(outputRoot, "warehouse.sqlite");
        var modelDeltas = new Dictionary<int, (int prevVersion, int prevElements, int deltaElements, int added, int removed, int modified)>();
        var elementDiffs = new List<ElementDiffRecord>();
        var lifecycleIssues = new List<IdsIssueRecord>();

        int totalFixedIssues = 0;
        int totalNewIssues = 0;
        int totalRegressedIssues = 0;
        int totalActiveIssues = 0;

        // Build per-element priority parameter map for current run
        var elemParamMap = new Dictionary<(int mId, long elId), Dictionary<string, string>>();
        var paramFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (mId, pr) in parameters)
        {
            var key = (mId, pr.ElementId);
            if (!elemParamMap.TryGetValue(key, out var pDict))
            {
                pDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                elemParamMap[key] = pDict;
            }
            if (!pDict.ContainsKey(pr.Name))
            {
                pDict[pr.Name] = pr.DisplayValue;
                paramFrequency[pr.Name] = paramFrequency.GetValueOrDefault(pr.Name) + 1;
            }
        }

        var diffTrackCols = priorityColumns.Take(12).ToList();

        // 1. Update Persistent SQLite History Warehouse (output/warehouse.sqlite): Element Diff + Issue Lifecycle
        using (var wConn = new SqliteConnection("Data Source=" + warehousePath))
        {
            wConn.Open();
            using (var wInit = wConn.CreateCommand())
            {
                wInit.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS runs (
                        run_id TEXT PRIMARY KEY,
                        project_id TEXT,
                        project_name TEXT,
                        started_utc TEXT,
                        model_count INTEGER,
                        element_count INTEGER,
                        parameter_count INTEGER,
                        issue_count INTEGER,
                        fixed_count INTEGER DEFAULT 0,
                        new_issue_count INTEGER DEFAULT 0,
                        regressed_count INTEGER DEFAULT 0
                    );
                    CREATE TABLE IF NOT EXISTS model_versions (
                        run_id TEXT,
                        project_id TEXT,
                        lineage_id TEXT,
                        model_name TEXT,
                        version_number INTEGER,
                        version_id TEXT,
                        view_name TEXT,
                        element_count INTEGER,
                        issue_count INTEGER,
                        recorded_utc TEXT,
                        PRIMARY KEY (run_id, project_id, lineage_id)
                    );
                    CREATE TABLE IF NOT EXISTS element_snapshots (
                        project_id TEXT,
                        lineage_id TEXT,
                        unique_id TEXT,
                        element_id INTEGER,
                        category TEXT,
                        family TEXT,
                        type_name TEXT,
                        params_json TEXT,
                        run_id TEXT,
                        PRIMARY KEY (project_id, lineage_id, unique_id)
                    );
                    CREATE TABLE IF NOT EXISTS issue_registry (
                        project_id TEXT,
                        lineage_id TEXT,
                        unique_id TEXT,
                        rule_id TEXT,
                        parameter_name TEXT,
                        discipline TEXT,
                        severity TEXT,
                        status TEXT,
                        first_seen_run TEXT,
                        last_seen_run TEXT,
                        closed_run TEXT,
                        reopened_count INTEGER DEFAULT 0,
                        PRIMARY KEY (project_id, lineage_id, unique_id, rule_id, parameter_name)
                    );
                ";
                wInit.ExecuteNonQuery();
            }

            // Ensure new columns exist on older warehouse tables if migrating
            foreach (var alterSql in new[]
            {
                "ALTER TABLE runs ADD COLUMN project_id TEXT DEFAULT ''",
                "ALTER TABLE runs ADD COLUMN fixed_count INTEGER DEFAULT 0",
                "ALTER TABLE runs ADD COLUMN new_issue_count INTEGER DEFAULT 0",
                "ALTER TABLE runs ADD COLUMN regressed_count INTEGER DEFAULT 0",
                "ALTER TABLE model_versions ADD COLUMN project_id TEXT DEFAULT ''"
            })
            {
                try { using var ac = wConn.CreateCommand(); ac.CommandText = alterSql; ac.ExecuteNonQuery(); } catch { }
            }

            using var wTrans = wConn.BeginTransaction();

            foreach (var m in models)
            {
                if (m.status != "succeeded")
                {
                    modelDeltas[m.modelId] = (m.item.VersionNumber, m.count, 0, 0, 0, 0);
                    continue;
                }

                // Strictly match by (project_id, lineage_id), never by file name alone!
                int prevVer = m.item.VersionNumber;
                int prevCount = m.count;
                bool hasPriorModelRun = false;

                using (var qPrev = wConn.CreateCommand())
                {
                    qPrev.CommandText = @"
                        SELECT version_number, element_count
                        FROM model_versions
                        WHERE project_id = $pid AND lineage_id = $lid
                        ORDER BY recorded_utc DESC LIMIT 1";
                    qPrev.Parameters.AddWithValue("$pid", projectId);
                    qPrev.Parameters.AddWithValue("$lid", m.item.Id);
                    using var r = qPrev.ExecuteReader();
                    if (r.Read())
                    {
                        hasPriorModelRun = true;
                        prevVer = r.GetInt32(0);
                        prevCount = r.GetInt32(1);
                    }
                }

                // Load previous element snapshots for (project_id, lineage_id)
                var prevSnapshots = new Dictionary<string, (long elId, string cat, string fam, string typ, string json)>(StringComparer.Ordinal);
                using (var qSnap = wConn.CreateCommand())
                {
                    qSnap.CommandText = "SELECT unique_id, element_id, category, family, type_name, params_json FROM element_snapshots WHERE project_id=$pid AND lineage_id=$lid";
                    qSnap.Parameters.AddWithValue("$pid", projectId);
                    qSnap.Parameters.AddWithValue("$lid", m.item.Id);
                    using var sr = qSnap.ExecuteReader();
                    while (sr.Read())
                    {
                        var uid = sr.GetString(0);
                        prevSnapshots[uid] = (sr.GetInt64(1), sr.GetString(2), sr.GetString(3), sr.GetString(4), sr.GetString(5));
                    }
                }

                int mAdded = 0, mRemoved = 0, mModified = 0;
                var currentUids = new HashSet<string>(StringComparer.Ordinal);
                var modelInstances = elements.Where(x => x.modelId == m.modelId && x.elem.Kind == "instance" && !string.IsNullOrEmpty(x.elem.UniqueId)).ToList();

                foreach (var (_, el) in modelInstances)
                {
                    currentUids.Add(el.UniqueId);
                    elemParamMap.TryGetValue((m.modelId, el.ElementId), out var pDict);
                    var subset = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (pDict != null)
                    {
                        foreach (var col in diffTrackCols)
                        {
                            if (pDict.TryGetValue(col, out var val) && !string.IsNullOrEmpty(val))
                                subset[col] = val;
                        }
                    }
                    var currentJson = JsonSerializer.Serialize(subset);

                    if (hasPriorModelRun && prevSnapshots.Count > 0)
                    {
                        if (!prevSnapshots.TryGetValue(el.UniqueId, out var prevSnap))
                        {
                            mAdded++;
                            elementDiffs.Add(new ElementDiffRecord(
                                m.modelId, "Added", el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                "(Element Added)", "-", $"{el.Family} : {el.TypeName}"));
                        }
                        else if (prevSnap.typ != el.TypeName || prevSnap.json != currentJson)
                        {
                            mModified++;
                            if (prevSnap.typ != el.TypeName)
                            {
                                elementDiffs.Add(new ElementDiffRecord(
                                    m.modelId, "Modified", el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                    "Type Name", prevSnap.typ, el.TypeName));
                            }

                            var oldDict = JsonSerializer.Deserialize<Dictionary<string, string>>(prevSnap.json) ?? [];
                            foreach (var col in diffTrackCols)
                            {
                                var oldV = oldDict.GetValueOrDefault(col, "");
                                var newV = subset.GetValueOrDefault(col, "");
                                if (!string.Equals(oldV, newV, StringComparison.Ordinal))
                                {
                                    elementDiffs.Add(new ElementDiffRecord(
                                        m.modelId, "Modified", el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                        col, string.IsNullOrEmpty(oldV) ? "(empty)" : oldV, string.IsNullOrEmpty(newV) ? "(empty)" : newV));
                                }
                            }
                        }
                    }

                    // Upsert snapshot
                    using var upSnap = wConn.CreateCommand();
                    upSnap.CommandText = "INSERT OR REPLACE INTO element_snapshots VALUES ($pid, $lid, $uid, $elid, $cat, $fam, $typ, $json, $rid)";
                    upSnap.Parameters.AddWithValue("$pid", projectId);
                    upSnap.Parameters.AddWithValue("$lid", m.item.Id);
                    upSnap.Parameters.AddWithValue("$uid", el.UniqueId);
                    upSnap.Parameters.AddWithValue("$elid", el.ElementId);
                    upSnap.Parameters.AddWithValue("$cat", el.Category);
                    upSnap.Parameters.AddWithValue("$fam", el.Family);
                    upSnap.Parameters.AddWithValue("$typ", el.TypeName);
                    upSnap.Parameters.AddWithValue("$json", currentJson);
                    upSnap.Parameters.AddWithValue("$rid", runId);
                    upSnap.ExecuteNonQuery();
                }

                if (hasPriorModelRun && prevSnapshots.Count > 0)
                {
                    foreach (var (oldUid, oldSnap) in prevSnapshots)
                    {
                        if (!currentUids.Contains(oldUid))
                        {
                            mRemoved++;
                            elementDiffs.Add(new ElementDiffRecord(
                                m.modelId, "Removed", oldSnap.elId, oldUid, oldSnap.cat, oldSnap.fam, oldSnap.typ,
                                "(Element Removed)", $"{oldSnap.fam} : {oldSnap.typ}", "-"));

                            using var delSnap = wConn.CreateCommand();
                            delSnap.CommandText = "DELETE FROM element_snapshots WHERE project_id=$pid AND lineage_id=$lid AND unique_id=$uid";
                            delSnap.Parameters.AddWithValue("$pid", projectId);
                            delSnap.Parameters.AddWithValue("$lid", m.item.Id);
                            delSnap.Parameters.AddWithValue("$uid", oldUid);
                            delSnap.ExecuteNonQuery();
                        }
                    }
                }

                modelDeltas[m.modelId] = (prevVer, prevCount, m.count - prevCount, mAdded, mRemoved, mModified);

                // Issue Lifecycle Reconciliation for (project_id, lineage_id)
                var priorIssues = new Dictionary<(string uid, string ruleId, string param), (string status, string firstRun, int reopened)>();
                using (var qIss = wConn.CreateCommand())
                {
                    qIss.CommandText = "SELECT unique_id, rule_id, parameter_name, status, first_seen_run, reopened_count FROM issue_registry WHERE project_id=$pid AND lineage_id=$lid";
                    qIss.Parameters.AddWithValue("$pid", projectId);
                    qIss.Parameters.AddWithValue("$lid", m.item.Id);
                    using var ir = qIss.ExecuteReader();
                    while (ir.Read())
                    {
                        priorIssues[(ir.GetString(0), ir.GetString(1), ir.GetString(2))] =
                            (ir.GetString(3), ir.GetString(4), ir.GetInt32(5));
                    }
                }

                var seenIssueKeys = new HashSet<(string uid, string ruleId, string param)>();
                foreach (var iss in evalSummary.Issues.Where(x => x.ModelId == m.modelId))
                {
                    var uidKey = string.IsNullOrEmpty(iss.UniqueId) ? iss.ElementId.ToString() : iss.UniqueId;
                    var iKey = (uidKey, iss.RuleId, iss.ParameterName);
                    seenIssueKeys.Add(iKey);

                    string state = "New";
                    string firstRun = runId;
                    int reopened = 0;

                    if (priorIssues.TryGetValue(iKey, out var prevIss))
                    {
                        firstRun = prevIss.firstRun;
                        reopened = prevIss.reopened;
                        if (prevIss.status == "Closed")
                        {
                            state = "Regressed";
                            reopened++;
                            totalRegressedIssues++;
                        }
                        else
                        {
                            state = "Active";
                            totalActiveIssues++;
                        }
                    }
                    else
                    {
                        totalNewIssues++;
                    }

                    lifecycleIssues.Add(iss with { LifecycleState = state });

                    using var upIss = wConn.CreateCommand();
                    upIss.CommandText = @"
                        INSERT OR REPLACE INTO issue_registry
                        VALUES ($pid, $lid, $uid, $rid, $pname, $disc, $sev, 'Open', $first, $last, '', $reopen)";
                    upIss.Parameters.AddWithValue("$pid", projectId);
                    upIss.Parameters.AddWithValue("$lid", m.item.Id);
                    upIss.Parameters.AddWithValue("$uid", uidKey);
                    upIss.Parameters.AddWithValue("$rid", iss.RuleId);
                    upIss.Parameters.AddWithValue("$pname", iss.ParameterName);
                    upIss.Parameters.AddWithValue("$disc", iss.Discipline);
                    upIss.Parameters.AddWithValue("$sev", iss.Severity);
                    upIss.Parameters.AddWithValue("$first", firstRun);
                    upIss.Parameters.AddWithValue("$last", runId);
                    upIss.Parameters.AddWithValue("$reopen", reopened);
                    upIss.ExecuteNonQuery();
                }

                // Mark previously Open issues that are no longer failing in this model run as Closed (Fixed!)
                foreach (var (iKey, prevIss) in priorIssues)
                {
                    if (prevIss.status == "Open" && !seenIssueKeys.Contains(iKey))
                    {
                        totalFixedIssues++;
                        using var closeIss = wConn.CreateCommand();
                        closeIss.CommandText = @"
                            UPDATE issue_registry
                            SET status = 'Closed', closed_run = $crun
                            WHERE project_id=$pid AND lineage_id=$lid AND unique_id=$uid AND rule_id=$rid AND parameter_name=$pname";
                        closeIss.Parameters.AddWithValue("$crun", runId);
                        closeIss.Parameters.AddWithValue("$pid", projectId);
                        closeIss.Parameters.AddWithValue("$lid", m.item.Id);
                        closeIss.Parameters.AddWithValue("$uid", iKey.uid);
                        closeIss.Parameters.AddWithValue("$rid", iKey.ruleId);
                        closeIss.Parameters.AddWithValue("$pname", iKey.param);
                        closeIss.ExecuteNonQuery();
                    }
                }
            }

            using (var rCmd = wConn.CreateCommand())
            {
                rCmd.CommandText = "INSERT OR REPLACE INTO runs (run_id, project_id, project_name, started_utc, model_count, element_count, parameter_count, issue_count, fixed_count, new_issue_count, regressed_count) VALUES ($rid, $pid, $proj, datetime('now'), $mc, $ec, $pc, $ic, $fc, $nc, $rc)";
                rCmd.Parameters.AddWithValue("$rid", runId);
                rCmd.Parameters.AddWithValue("$pid", projectId);
                rCmd.Parameters.AddWithValue("$proj", projectName);
                rCmd.Parameters.AddWithValue("$mc", models.Count);
                rCmd.Parameters.AddWithValue("$ec", elements.Count);
                rCmd.Parameters.AddWithValue("$pc", parameters.Count);
                rCmd.Parameters.AddWithValue("$ic", lifecycleIssues.Count);
                rCmd.Parameters.AddWithValue("$fc", totalFixedIssues);
                rCmd.Parameters.AddWithValue("$nc", totalNewIssues);
                rCmd.Parameters.AddWithValue("$rc", totalRegressedIssues);
                rCmd.ExecuteNonQuery();
            }

            foreach (var m in models)
            {
                var mIssues = lifecycleIssues.Count(x => x.ModelId == m.modelId);
                using var mvCmd = wConn.CreateCommand();
                mvCmd.CommandText = "INSERT OR REPLACE INTO model_versions (run_id, project_id, lineage_id, model_name, version_number, version_id, view_name, element_count, issue_count, recorded_utc) VALUES ($rid, $pid, $lid, $name, $vnum, $vid, $vname, $ec, $ic, datetime('now'))";
                mvCmd.Parameters.AddWithValue("$rid", runId);
                mvCmd.Parameters.AddWithValue("$pid", projectId);
                mvCmd.Parameters.AddWithValue("$lid", m.item.Id);
                mvCmd.Parameters.AddWithValue("$name", m.item.Name);
                mvCmd.Parameters.AddWithValue("$vnum", m.item.VersionNumber);
                mvCmd.Parameters.AddWithValue("$vid", m.item.VersionId);
                mvCmd.Parameters.AddWithValue("$vname", m.item.ViewName);
                mvCmd.Parameters.AddWithValue("$ec", m.count);
                mvCmd.Parameters.AddWithValue("$ic", mIssues);
                mvCmd.ExecuteNonQuery();
            }

            wTrans.Commit();
        }

        int totalAddedElems = modelDeltas.Values.Sum(x => x.added);
        int totalRemovedElems = modelDeltas.Values.Sum(x => x.removed);
        int totalModifiedElems = modelDeltas.Values.Sum(x => x.modified);

        // 2. Write Run-Specific SQLite Database (extract.sqlite) with Data Confidence, Element Diffs, & Issue Lifecycle
        var dbPath = Path.Combine(runDir, "extract.sqlite");
        using var conn = new SqliteConnection("Data Source=" + dbPath);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA foreign_keys=OFF;
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE models (
                    model_id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL,
                    status TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    finished_utc TEXT,
                    element_count INTEGER DEFAULT 0,
                    version_number INTEGER DEFAULT 1,
                    view_name TEXT DEFAULT '{3D}',
                    delta_elements INTEGER DEFAULT 0,
                    added_elements INTEGER DEFAULT 0,
                    removed_elements INTEGER DEFAULT 0,
                    modified_elements INTEGER DEFAULT 0,
                    derivative_urn TEXT DEFAULT ''
                );
                CREATE TABLE elements (
                    model_id INTEGER NOT NULL,
                    element_id INTEGER NOT NULL,
                    unique_id TEXT,
                    kind TEXT,
                    category_id TEXT,
                    category TEXT,
                    family TEXT,
                    type_name TEXT,
                    type_id INTEGER,
                    name TEXT,
                    object_id INTEGER DEFAULT 0,
                    PRIMARY KEY(model_id, element_id)
                );
                CREATE TABLE parameters (
                    model_id INTEGER NOT NULL,
                    element_id INTEGER NOT NULL,
                    scope TEXT,
                    parameter_key TEXT,
                    name TEXT,
                    storage_type TEXT,
                    has_value INTEGER,
                    raw_value TEXT,
                    display_value TEXT,
                    unit_type_id TEXT
                );
                CREATE TABLE issues (
                    issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    model_id INTEGER NOT NULL,
                    element_id INTEGER NOT NULL,
                    unique_id TEXT,
                    category TEXT,
                    family TEXT,
                    type_name TEXT,
                    rule_id TEXT,
                    requirement_id TEXT,
                    discipline TEXT,
                    severity TEXT,
                    parameter_name TEXT,
                    actual_value TEXT,
                    expected_rule TEXT,
                    evaluation_status TEXT,
                    lifecycle_state TEXT,
                    message TEXT
                );
                CREATE TABLE element_diffs (
                    diff_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    model_id INTEGER NOT NULL,
                    change_type TEXT NOT NULL,
                    element_id INTEGER NOT NULL,
                    unique_id TEXT,
                    category TEXT,
                    family TEXT,
                    type_name TEXT,
                    parameter_name TEXT,
                    old_value TEXT,
                    new_value TEXT
                );
                CREATE TABLE confidence_summary (
                    applicable_checks INTEGER,
                    evaluated_checks INTEGER,
                    passed_checks INTEGER,
                    failed_checks INTEGER,
                    invalid_value_checks INTEGER,
                    not_evaluated_checks INTEGER,
                    fixed_issues INTEGER,
                    new_issues INTEGER,
                    active_issues INTEGER,
                    regressed_issues INTEGER,
                    added_elements INTEGER,
                    removed_elements INTEGER,
                    modified_elements INTEGER
                );
                CREATE TABLE priority_columns (col_order INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE errors (utc TEXT, model_id INTEGER, element_id INTEGER, stage TEXT, exception_type TEXT, message TEXT);
            ";
            cmd.ExecuteNonQuery();
        }

        using (var trans = conn.BeginTransaction())
        {
            using (var csCmd = conn.CreateCommand())
            {
                csCmd.CommandText = "INSERT INTO confidence_summary VALUES ($app, $eval, $pass, $fail, $inv, $neval, $fix, $new, $act, $reg, $add, $rem, $mod)";
                csCmd.Parameters.AddWithValue("$app", evalSummary.ApplicableChecks);
                csCmd.Parameters.AddWithValue("$eval", evalSummary.EvaluatedChecks);
                csCmd.Parameters.AddWithValue("$pass", evalSummary.PassedChecks);
                csCmd.Parameters.AddWithValue("$fail", evalSummary.FailedChecks);
                csCmd.Parameters.AddWithValue("$inv", evalSummary.InvalidValueChecks);
                csCmd.Parameters.AddWithValue("$neval", evalSummary.NotEvaluatedChecks);
                csCmd.Parameters.AddWithValue("$fix", totalFixedIssues);
                csCmd.Parameters.AddWithValue("$new", totalNewIssues);
                csCmd.Parameters.AddWithValue("$act", totalActiveIssues);
                csCmd.Parameters.AddWithValue("$reg", totalRegressedIssues);
                csCmd.Parameters.AddWithValue("$add", totalAddedElems);
                csCmd.Parameters.AddWithValue("$rem", totalRemovedElems);
                csCmd.Parameters.AddWithValue("$mod", totalModifiedElems);
                csCmd.ExecuteNonQuery();
            }

            using (var colCmd = conn.CreateCommand())
            {
                colCmd.CommandText = "INSERT INTO priority_columns VALUES (@ord, @name)";
                var pOrd = colCmd.Parameters.Add("@ord", SqliteType.Integer);
                var pName = colCmd.Parameters.Add("@name", SqliteType.Text);
                for (int i = 0; i < priorityColumns.Count; i++)
                {
                    pOrd.Value = i + 1;
                    pName.Value = priorityColumns[i];
                    colCmd.ExecuteNonQuery();
                }
            }

            using (var mCmd = conn.CreateCommand())
            {
                mCmd.CommandText = "INSERT INTO models VALUES (@id, @path, @status, DateTime('now'), DateTime('now'), @count, @vnum, @vname, @delta, @add, @rem, @mod, @urn)";
                var pId = mCmd.Parameters.Add("@id", SqliteType.Integer);
                var pPath = mCmd.Parameters.Add("@path", SqliteType.Text);
                var pStatus = mCmd.Parameters.Add("@status", SqliteType.Text);
                var pCount = mCmd.Parameters.Add("@count", SqliteType.Integer);
                var pVnum = mCmd.Parameters.Add("@vnum", SqliteType.Integer);
                var pVname = mCmd.Parameters.Add("@vname", SqliteType.Text);
                var pDelta = mCmd.Parameters.Add("@delta", SqliteType.Integer);
                var pAdd = mCmd.Parameters.Add("@add", SqliteType.Integer);
                var pRem = mCmd.Parameters.Add("@rem", SqliteType.Integer);
                var pMod = mCmd.Parameters.Add("@mod", SqliteType.Integer);
                var pUrn = mCmd.Parameters.Add("@urn", SqliteType.Text);

                foreach (var m in models)
                {
                    var d = modelDeltas.GetValueOrDefault(m.modelId, (1, m.count, 0, 0, 0, 0));
                    pId.Value = m.modelId;
                    pPath.Value = m.item.Name;
                    pStatus.Value = m.status;
                    pCount.Value = m.count;
                    pVnum.Value = m.item.VersionNumber;
                    pVname.Value = string.IsNullOrEmpty(m.item.ViewName) ? "{3D}" : m.item.ViewName;
                    pDelta.Value = d.Item3;
                    pAdd.Value = d.Item4;
                    pRem.Value = d.Item5;
                    pMod.Value = d.Item6;
                    pUrn.Value = m.item.DerivativeUrn ?? "";
                    mCmd.ExecuteNonQuery();
                }
            }

            using (var eCmd = conn.CreateCommand())
            {
                eCmd.CommandText = "INSERT OR IGNORE INTO elements VALUES (@m, @id, @uid, @kind, @catId, @cat, @fam, @type, @tid, @name, @objId)";
                var pM = eCmd.Parameters.Add("@m", SqliteType.Integer);
                var pId = eCmd.Parameters.Add("@id", SqliteType.Integer);
                var pUid = eCmd.Parameters.Add("@uid", SqliteType.Text);
                var pKind = eCmd.Parameters.Add("@kind", SqliteType.Text);
                var pCatId = eCmd.Parameters.Add("@catId", SqliteType.Text);
                var pCat = eCmd.Parameters.Add("@cat", SqliteType.Text);
                var pFam = eCmd.Parameters.Add("@fam", SqliteType.Text);
                var pType = eCmd.Parameters.Add("@type", SqliteType.Text);
                var pTid = eCmd.Parameters.Add("@tid", SqliteType.Integer);
                var pName = eCmd.Parameters.Add("@name", SqliteType.Text);
                var pObjId = eCmd.Parameters.Add("@objId", SqliteType.Integer);

                foreach (var (mId, el) in elements)
                {
                    pM.Value = mId;
                    pId.Value = el.ElementId;
                    pUid.Value = el.UniqueId;
                    pKind.Value = el.Kind;
                    pCatId.Value = el.Category;
                    pCat.Value = el.Category;
                    pFam.Value = el.Family;
                    pType.Value = el.TypeName;
                    pTid.Value = el.TypeId;
                    pName.Value = el.Name;
                    pObjId.Value = el.ObjectId;
                    eCmd.ExecuteNonQuery();
                }
            }

            using (var pCmd = conn.CreateCommand())
            {
                pCmd.CommandText = "INSERT INTO parameters VALUES (@m, @id, @scope, @key, @name, @stype, 1, @raw, @disp, @unit)";
                var pmM = pCmd.Parameters.Add("@m", SqliteType.Integer);
                var pmId = pCmd.Parameters.Add("@id", SqliteType.Integer);
                var pmScope = pCmd.Parameters.Add("@scope", SqliteType.Text);
                var pmKey = pCmd.Parameters.Add("@key", SqliteType.Text);
                var pmName = pCmd.Parameters.Add("@name", SqliteType.Text);
                var pmStype = pCmd.Parameters.Add("@stype", SqliteType.Text);
                var pmRaw = pCmd.Parameters.Add("@raw", SqliteType.Text);
                var pmDisp = pCmd.Parameters.Add("@disp", SqliteType.Text);
                var pmUnit = pCmd.Parameters.Add("@unit", SqliteType.Text);

                foreach (var (mId, pr) in parameters)
                {
                    pmM.Value = mId;
                    pmId.Value = pr.ElementId;
                    pmScope.Value = pr.Scope;
                    pmKey.Value = $"{pr.GroupName}:{pr.Name}";
                    pmName.Value = pr.Name;
                    pmStype.Value = pr.StorageType;
                    pmRaw.Value = pr.RawValue;
                    pmDisp.Value = pr.DisplayValue;
                    pmUnit.Value = pr.Unit;
                    pCmd.ExecuteNonQuery();
                }
            }

            using (var iCmd = conn.CreateCommand())
            {
                iCmd.CommandText = @"
                    INSERT INTO issues (model_id, element_id, unique_id, category, family, type_name, rule_id, requirement_id, discipline, severity, parameter_name, actual_value, expected_rule, evaluation_status, lifecycle_state, message)
                    VALUES (@m, @id, @uid, @cat, @fam, @typ, @rid, @req, @disc, @sev, @pname, @aval, @exp, @eval, @life, @msg)";
                var piM = iCmd.Parameters.Add("@m", SqliteType.Integer);
                var piId = iCmd.Parameters.Add("@id", SqliteType.Integer);
                var piUid = iCmd.Parameters.Add("@uid", SqliteType.Text);
                var piCat = iCmd.Parameters.Add("@cat", SqliteType.Text);
                var piFam = iCmd.Parameters.Add("@fam", SqliteType.Text);
                var piTyp = iCmd.Parameters.Add("@typ", SqliteType.Text);
                var piRid = iCmd.Parameters.Add("@rid", SqliteType.Text);
                var piReq = iCmd.Parameters.Add("@req", SqliteType.Text);
                var piDisc = iCmd.Parameters.Add("@disc", SqliteType.Text);
                var piSev = iCmd.Parameters.Add("@sev", SqliteType.Text);
                var piPname = iCmd.Parameters.Add("@pname", SqliteType.Text);
                var piAval = iCmd.Parameters.Add("@aval", SqliteType.Text);
                var piExp = iCmd.Parameters.Add("@exp", SqliteType.Text);
                var piEval = iCmd.Parameters.Add("@eval", SqliteType.Text);
                var piLife = iCmd.Parameters.Add("@life", SqliteType.Text);
                var piMsg = iCmd.Parameters.Add("@msg", SqliteType.Text);

                foreach (var iss in lifecycleIssues)
                {
                    piM.Value = iss.ModelId;
                    piId.Value = iss.ElementId;
                    piUid.Value = iss.UniqueId;
                    piCat.Value = iss.Category;
                    piFam.Value = iss.Family;
                    piTyp.Value = iss.TypeName;
                    piRid.Value = iss.RuleId;
                    piReq.Value = iss.RequirementId;
                    piDisc.Value = iss.Discipline;
                    piSev.Value = iss.Severity;
                    piPname.Value = iss.ParameterName;
                    piAval.Value = iss.ActualValue;
                    piExp.Value = iss.ExpectedRule;
                    piEval.Value = iss.EvaluationStatus;
                    piLife.Value = iss.LifecycleState;
                    piMsg.Value = iss.Message;
                    iCmd.ExecuteNonQuery();
                }
            }

            using (var dCmd = conn.CreateCommand())
            {
                dCmd.CommandText = @"
                    INSERT INTO element_diffs (model_id, change_type, element_id, unique_id, category, family, type_name, parameter_name, old_value, new_value)
                    VALUES (@m, @ct, @id, @uid, @cat, @fam, @typ, @pname, @oldv, @newv)";
                var pdM = dCmd.Parameters.Add("@m", SqliteType.Integer);
                var pdCt = dCmd.Parameters.Add("@ct", SqliteType.Text);
                var pdId = dCmd.Parameters.Add("@id", SqliteType.Integer);
                var pdUid = dCmd.Parameters.Add("@uid", SqliteType.Text);
                var pdCat = dCmd.Parameters.Add("@cat", SqliteType.Text);
                var pdFam = dCmd.Parameters.Add("@fam", SqliteType.Text);
                var pdTyp = dCmd.Parameters.Add("@typ", SqliteType.Text);
                var pdPname = dCmd.Parameters.Add("@pname", SqliteType.Text);
                var pdOld = dCmd.Parameters.Add("@oldv", SqliteType.Text);
                var pdNew = dCmd.Parameters.Add("@newv", SqliteType.Text);

                foreach (var df in elementDiffs)
                {
                    pdM.Value = df.ModelId;
                    pdCt.Value = df.ChangeType;
                    pdId.Value = df.ElementId;
                    pdUid.Value = df.UniqueId;
                    pdCat.Value = df.Category;
                    pdFam.Value = df.Family;
                    pdTyp.Value = df.TypeName;
                    pdPname.Value = df.ParameterName;
                    pdOld.Value = df.OldValue;
                    pdNew.Value = df.NewValue;
                    dCmd.ExecuteNonQuery();
                }
            }

            using (var errCmd = conn.CreateCommand())
            {
                errCmd.CommandText = "INSERT INTO errors VALUES (DateTime('now'), @m, 0, @stg, @type, @msg)";
                var peM = errCmd.Parameters.Add("@m", SqliteType.Integer);
                var peStg = errCmd.Parameters.Add("@stg", SqliteType.Text);
                var peType = errCmd.Parameters.Add("@type", SqliteType.Text);
                var peMsg = errCmd.Parameters.Add("@msg", SqliteType.Text);

                foreach (var er in errorLogs)
                {
                    peM.Value = er.modelId;
                    peStg.Value = er.stage;
                    peType.Value = er.type;
                    peMsg.Value = er.message;
                    errCmd.ExecuteNonQuery();
                }
            }

            trans.Commit();
        }

        using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = @"
                CREATE INDEX ix_parameter_name ON parameters(name);
                CREATE INDEX ix_parameter_element ON parameters(model_id, element_id);
                CREATE INDEX ix_element_cat ON elements(category);
                CREATE INDEX ix_issues_disc ON issues(discipline, severity, lifecycle_state);
                CREATE INDEX ix_issues_rule ON issues(rule_id);
                CREATE INDEX ix_diffs_type ON element_diffs(change_type, model_id);
            ";
            idxCmd.ExecuteNonQuery();
        }

        // Write CSVs & Fix Assistant JSON
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var modelNameMap = models.ToDictionary(m => m.modelId, m => m.item.Name);

        var modelsCsv = Path.Combine(runDir, "models.csv");
        using (var swM = new StreamWriter(modelsCsv, false, utf8Bom))
        {
            swM.WriteLine("model_id,path,version_number,view_name,status,element_count,delta_elements,added_elements,removed_elements,modified_elements");
            foreach (var m in models)
            {
                var d = modelDeltas.GetValueOrDefault(m.modelId, (1, m.count, 0, 0, 0, 0));
                swM.WriteLine($"{m.modelId},{EscapeCsv(m.item.Name)},{m.item.VersionNumber},{EscapeCsv(m.item.ViewName)},{m.status},{m.count},{d.Item3},{d.Item4},{d.Item5},{d.Item6}");
            }
        }

        var elementsCsv = Path.Combine(runDir, "elements.csv");
        using (var swE = new StreamWriter(elementsCsv, false, utf8Bom))
        {
            swE.WriteLine("model_id,model_name,element_id,object_id,unique_id,kind,category,family,type_name,type_id,name");
            foreach (var (mId, el) in elements)
            {
                var mName = modelNameMap.GetValueOrDefault(mId, "");
                swE.WriteLine($"{mId},{EscapeCsv(mName)},{el.ElementId},{el.ObjectId},{EscapeCsv(el.UniqueId)},{el.Kind},{EscapeCsv(el.Category)},{EscapeCsv(el.Family)},{EscapeCsv(el.TypeName)},{el.TypeId},{EscapeCsv(el.Name)}");
            }
        }

        var paramsCsv = Path.Combine(runDir, "parameters.csv");
        using (var swP = new StreamWriter(paramsCsv, false, utf8Bom))
        {
            swP.WriteLine("model_id,element_id,scope,property_group,name,storage_type,raw_value,display_value,unit");
            foreach (var (mId, pr) in parameters)
            {
                swP.WriteLine($"{mId},{pr.ElementId},{pr.Scope},{EscapeCsv(pr.GroupName)},{EscapeCsv(pr.Name)},{pr.StorageType},{EscapeCsv(pr.RawValue)},{EscapeCsv(pr.DisplayValue)},{EscapeCsv(pr.Unit)}");
            }
        }

        var issuesCsv = Path.Combine(runDir, "issues.csv");
        using (var swI = new StreamWriter(issuesCsv, false, utf8Bom))
        {
            swI.WriteLine("model_id,model_name,element_id,unique_id,discipline,severity,lifecycle_state,evaluation_status,rule_id,requirement_id,category,family,type_name,parameter_name,actual_value,expected_rule,message");
            foreach (var iss in lifecycleIssues)
            {
                var mName = modelNameMap.GetValueOrDefault(iss.ModelId, "");
                swI.WriteLine($"{iss.ModelId},{EscapeCsv(mName)},{iss.ElementId},{EscapeCsv(iss.UniqueId)},{EscapeCsv(iss.Discipline)},{EscapeCsv(iss.Severity)},{EscapeCsv(iss.LifecycleState)},{EscapeCsv(iss.EvaluationStatus)},{EscapeCsv(iss.RuleId)},{EscapeCsv(iss.RequirementId)},{EscapeCsv(iss.Category)},{EscapeCsv(iss.Family)},{EscapeCsv(iss.TypeName)},{EscapeCsv(iss.ParameterName)},{EscapeCsv(iss.ActualValue)},{EscapeCsv(iss.ExpectedRule)},{EscapeCsv(iss.Message)}");
            }
        }

        var fixJsonPath = Path.Combine(runDir, "fix_candidates.json");
        var fixPayload = JsonSerializer.Serialize(new
        {
            runId,
            projectId,
            projectName,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            totalIssues = lifecycleIssues.Count,
            issues = lifecycleIssues.Select(iss => new
            {
                iss.ModelId,
                ModelName = modelNameMap.GetValueOrDefault(iss.ModelId, ""),
                iss.ElementId,
                iss.UniqueId,
                iss.Category,
                iss.Family,
                iss.TypeName,
                iss.RuleId,
                iss.RequirementId,
                iss.Discipline,
                iss.Severity,
                iss.ParameterName,
                iss.ActualValue,
                iss.ExpectedRule,
                iss.EvaluationStatus,
                iss.LifecycleState,
                iss.Message
            }).Take(2500)
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fixJsonPath, fixPayload, Encoding.UTF8);

        var wideColumns = new List<string>();
        var wideSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in priorityColumns)
        {
            if (wideSet.Add(col)) wideColumns.Add(col);
        }
        foreach (var kv in paramFrequency.OrderByDescending(k => k.Value))
        {
            if (wideColumns.Count >= 60) break;
            if (wideSet.Add(kv.Key)) wideColumns.Add(kv.Key);
        }

        var wideCsv = Path.Combine(runDir, "elements_wide.csv");
        using (var swW = new StreamWriter(wideCsv, false, utf8Bom))
        {
            var coreHeaders = new[] { "ModelId", "ModelName", "ElementId", "ObjectId", "UniqueId", "Kind", "Category", "Family", "TypeName", "Name" };
            var allHeaders = coreHeaders.Concat(wideColumns).Select(EscapeCsv);
            swW.WriteLine(string.Join(",", allHeaders));

            var rowSb = new StringBuilder();
            foreach (var (mId, el) in elements)
            {
                rowSb.Clear();
                var mName = modelNameMap.GetValueOrDefault(mId, "");
                rowSb.Append(mId).Append(',')
                     .Append(EscapeCsv(mName)).Append(',')
                     .Append(el.ElementId).Append(',')
                     .Append(el.ObjectId).Append(',')
                     .Append(EscapeCsv(el.UniqueId)).Append(',')
                     .Append(EscapeCsv(el.Kind)).Append(',')
                     .Append(EscapeCsv(el.Category)).Append(',')
                     .Append(EscapeCsv(el.Family)).Append(',')
                     .Append(EscapeCsv(el.TypeName)).Append(',')
                     .Append(EscapeCsv(el.Name));

                elemParamMap.TryGetValue((mId, el.ElementId), out var pDict);
                foreach (var col in wideColumns)
                {
                    var val = pDict != null && pDict.TryGetValue(col, out var v) ? v : "";
                    rowSb.Append(',').Append(EscapeCsv(val));
                }
                swW.WriteLine(rowSb.ToString());
            }
        }

        return (totalFixedIssues, totalNewIssues, totalRegressedIssues, totalAddedElems, totalRemovedElems, totalModifiedElems);
    }

    private string? GetLatestRunDirectory()
    {
        if (!Directory.Exists(outputRoot)) return null;
        return Directory.GetDirectories(outputRoot, "batch-*")
            .Concat(Directory.GetDirectories(outputRoot, "derivative-*"))
            .OrderByDescending(Directory.GetCreationTimeUtc)
            .FirstOrDefault();
    }

    private void BtnDashboard_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetLatestRunDirectory();
        if (dir == null)
        {
            MessageBox.Show(this, "No extraction data found. Please run an extraction first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OpenDashboard(dir);
    }

    private void BtnOpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetLatestRunDirectory() ?? outputRoot;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{dir}\"",
            UseShellExecute = true
        });
    }

    private void OpenDashboard(string directory)
    {
        Log($"\nOpening Review Dashboard for: {directory}...");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -File \"{dashScript}\" -RunDirectory \"{directory}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open Dashboard: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
