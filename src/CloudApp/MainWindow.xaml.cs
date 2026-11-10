using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace RBDE.CloudApp;

public partial class MainWindow : Window
{
    private readonly ApsClient aps = new();
    private readonly string baseDir;
    private readonly string dashScript;
    private readonly string outputRoot;
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

    private void ChkExtractAllCategories_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCategorySummary();
    }

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

        if (!aps.HasCredentials)
        {
            OpenSettingsModal("APS credentials not found. Please enter Client ID and Client Secret to connect.");
            return;
        }

        await InitializeHubConnectionAsync();
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
        TxtStatusBadge.Text = "Connected (2LO)";

        currentModels =
        [
            new ModelFileItem { Id = "1", Name = "ARC_Main_Tower_R26.rvt", IsSelected = true, Status = "Done (17,425 el / 84,620 pr)" },
            new ModelFileItem { Id = "2", Name = "STR_Superstructure_R26.rvt", IsSelected = true, Status = "Done (8,912 el / 41,205 pr)" },
            new ModelFileItem { Id = "3", Name = "MEP_HVAC_Piping_R26.rvt", IsSelected = true, Status = "Done (14,310 el / 68,110 pr)" },
            new ModelFileItem { Id = "4", Name = "INT_Podium_Fitout_R26.rvt", IsSelected = true, Status = "Done (6,180 el / 29,400 pr)" },
            new ModelFileItem { Id = "5", Name = "SITE_Landscape_Civil_R26.rvt", IsSelected = false, Status = "Ready" }
        ];
        LstModels.ItemsSource = currentModels;
        UpdateSelectedCount();

        ProgBatch.Maximum = 4;
        ProgBatch.Value = 4;
        TxtProgressStatus.Text = "Extraction completed for 4 model(s) in 34.2s. Output saved to SQLite & elements_wide.csv.";

        TxtLog.Text = string.Join(Environment.NewLine,
            "[10:15:02] Authenticating with Autodesk Platform Services (2-Legged OAuth)...",
            "[OK] Connected to Hub: ACC Enterprise Hub",
            "[OK] Selected Project: 2026 Mixed-Use Tower Development | Folder: Project Files / 01_WIP_Models",
            "============================================================",
            "[10:15:08] Initiating Batch Extraction (4 models)...",
            "Category Scope   : ALL Model Categories (Architecture, Structure, MEP, Interior)",
            "Priority Columns : Level, Base Constraint, Type Mark, Mark, Comments, Type Comments, Area, Volume, Length, Width, Height, Material, Fire Rating, Workset, Phase Created",
            "============================================================",
            ">>> [1/4] ARC_Main_Tower_R26.rvt -> View '{3D}' -> 17,425 elements, 84,620 parameter records (8.9s)",
            ">>> [2/4] STR_Superstructure_R26.rvt -> View '{3D}' -> 8,912 elements, 41,205 parameter records (7.1s)",
            ">>> [3/4] MEP_HVAC_Piping_R26.rvt -> View '{3D}' -> 14,310 elements, 68,110 parameter records (10.4s)",
            ">>> [4/4] INT_Podium_Fitout_R26.rvt -> View '{3D}' -> 6,180 elements, 29,400 parameter records (6.2s)",
            "[SUCCESS] Persisted extract.sqlite, elements_wide.csv, elements.csv, parameters.csv in 34.2s.");

        await Task.Delay(350);
        UpdateLayout();
        TxtLog.ScrollToEnd();
        await Task.Delay(100);
        SaveWindowPng(Path.Combine(outDir, "cloud-extractor-main.png"));

        // Now open the Settings Popup Modal with sanitized placeholder values
        TxtClientId.Text = "YOUR_APS_CLIENT_ID_HERE";
        TxtClientSecret.Text = "************************************************";
        TxtModalStatus.Text = "Enter your Server-to-Server Client ID and Secret to sync directly with your ACC Hub.";
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
        Log($"[{DateTime.Now:HH:mm:ss}] Authenticating with Autodesk Platform Services...");

        try
        {
            var hubs = await aps.GetHubsAsync();
            CmbHubs.ItemsSource = hubs;

            var defaultHub = hubs.FirstOrDefault();
            if (defaultHub != null)
            {
                CmbHubs.SelectedItem = defaultHub;
                TxtHubName.Text = $"Hub: {defaultHub.Name}";
                TxtStatusBadge.Text = "Connected";
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

    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsModal();
    }

    private void OpenSettingsModal(string? statusMessage = null)
    {
        TxtClientId.Text = aps.ClientId ?? "";
        TxtClientSecret.Text = aps.ClientSecret ?? "";
        TxtModalStatus.Text = statusMessage ?? "";
        ModalSettings.Visibility = Visibility.Visible;
    }

    private void BtnCloseModal_Click(object sender, RoutedEventArgs e)
    {
        ModalSettings.Visibility = Visibility.Collapsed;
    }

    private async void BtnSaveSync_Click(object sender, RoutedEventArgs e)
    {
        var id = TxtClientId.Text.Trim();
        var secret = TxtClientSecret.Text.Trim();

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
        {
            TxtModalStatus.Text = "Both Client ID and Client Secret are required.";
            return;
        }

        BtnSaveSync.IsEnabled = false;
        TxtModalStatus.Text = "Saving credentials and testing connection to Autodesk Platform Services...";

        try
        {
            aps.SaveCredentials(id, secret);
            await aps.EnsureTokenAsync();
            TxtModalStatus.Text = "Authentication succeeded! Syncing Hubs and Projects...";

            await InitializeHubConnectionAsync();
            TxtModalStatus.Text = "Connected successfully! Hubs and projects synchronized.";
            await Task.Delay(800);
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

    private void ModelCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSelectedCount();
    }

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

        BtnStartExtract.IsEnabled = false;
        BtnDashboard.IsEnabled = false;
        ProgBatch.Maximum = selectedModels.Count;
        ProgBatch.Value = 0;

        Log("\n============================================================");
        Log($"[{DateTime.Now:HH:mm:ss}] Initiating Batch Extraction ({selectedModels.Count} model(s))...");
        Log($"Project           : {proj.Name}");
        Log($"Category Scope    : {(extractAllCategories ? "ALL Model Categories" : $"{categories.Count} Selected Categories")}");
        Log($"Parameter Scope   : {(extractAllParams ? "ALL Model Parameters (Full Dump)" : $"{targetParams.Count} Targeted Parameters")}");
        Log($"Priority Columns  : {string.Join(", ", priorityColumns)}");
        Log("============================================================");

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(outputRoot, $"batch-{timestamp}");
        Directory.CreateDirectory(runDir);

        var sw = Stopwatch.StartNew();
        var allElements = new List<(int modelId, ExtractedElement elem)>();
        var allParameters = new List<(int modelId, ExtractedParam param)>();
        var modelRows = new List<(int modelId, string name, string status, int count)>();

        await Task.Run(async () =>
        {
            for (int i = 0; i < selectedModels.Count; i++)
            {
                var model = selectedModels[i];
                var modelId = i + 1;
                Dispatcher.Invoke(() =>
                {
                    ProgBatch.Value = i;
                    TxtProgressStatus.Text = $"[{modelId}/{selectedModels.Count}] Processing: {model.Name}...";
                    model.Status = "Extracting...";
                    LstModels.Items.Refresh();
                });

                Log($"\n>>> [{modelId}/{selectedModels.Count}] {model.Name}");
                try
                {
                    var (elements, paramValues) = await aps.ExtractModelAsync(
                        proj.Id,
                        model,
                        extractAllCategories,
                        categories,
                        extractAllParams,
                        targetParams,
                        Log);

                    foreach (var el in elements) allElements.Add((modelId, el));
                    foreach (var pr in paramValues) allParameters.Add((modelId, pr));

                    modelRows.Add((modelId, model.Name, "succeeded", elements.Count));
                    Dispatcher.Invoke(() =>
                    {
                        model.Status = $"Done ({elements.Count:N0} el / {paramValues.Count:N0} pr)";
                        LstModels.Items.Refresh();
                    });
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] {model.Name} failed: {ex.Message}");
                    modelRows.Add((modelId, model.Name, "failed", 0));
                    Dispatcher.Invoke(() =>
                    {
                        model.Status = "Failed";
                        LstModels.Items.Refresh();
                    });
                }
            }

            Log("\nPersisting SQLite database, relational CSVs, and Wide pivoted BI table (elements_wide.csv)...");
            SaveToDatabaseAndCsv(runDir, modelRows, allElements, allParameters, priorityColumns);
        });

        sw.Stop();
        ProgBatch.Value = selectedModels.Count;
        TxtProgressStatus.Text = $"Extraction completed for {selectedModels.Count} model(s) in {sw.Elapsed.TotalSeconds:F1}s!";

        Log("\n============================================================");
        Log($"[SUCCESS] Extraction completed for {selectedModels.Count} model(s)!");
        Log($"Total Elements   : {allElements.Count:N0}");
        Log($"Total Parameters : {allParameters.Count:N0}");
        Log($"Elapsed Time     : {sw.Elapsed.TotalSeconds:F1} seconds");
        Log($"Output Directory : {runDir}");
        Log($"Exported Files   : extract.sqlite, elements_wide.csv, elements.csv, parameters.csv, models.csv");
        Log("============================================================");

        BtnStartExtract.IsEnabled = true;
        BtnDashboard.IsEnabled = true;

        var res = MessageBox.Show(this,
            $"Batch extraction completed for {selectedModels.Count} model(s)!\n" +
            $"Total: {allElements.Count:N0} elements | {allParameters.Count:N0} parameters in {sw.Elapsed.TotalSeconds:F1}s\n\n" +
            "Generated:\n" +
            " • extract.sqlite (Interactive Dashboard DB)\n" +
            " • elements_wide.csv (Pivoted flat table for Power BI / Excel)\n" +
            " • elements.csv & parameters.csv (Normalized tables)\n\n" +
            "Would you like to open the Interactive Review Dashboard now?",
            "Extraction Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (res == MessageBoxResult.Yes)
        {
            OpenDashboard(runDir);
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

    private static void SaveToDatabaseAndCsv(
        string runDir,
        List<(int modelId, string name, string status, int count)> models,
        List<(int modelId, ExtractedElement elem)> elements,
        List<(int modelId, ExtractedParam param)> parameters,
        List<string> priorityColumns)
    {
        var dbPath = Path.Combine(runDir, "extract.sqlite");
        using var conn = new SqliteConnection("Data Source=" + dbPath);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA foreign_keys=OFF;
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE models (model_id INTEGER PRIMARY KEY, path TEXT NOT NULL, status TEXT NOT NULL,
                    started_utc TEXT NOT NULL, finished_utc TEXT, element_count INTEGER DEFAULT 0);
                CREATE TABLE elements (model_id INTEGER NOT NULL, element_id INTEGER NOT NULL,
                    unique_id TEXT, kind TEXT, category_id TEXT, category TEXT, family TEXT, type_name TEXT,
                    type_id INTEGER, name TEXT, PRIMARY KEY(model_id, element_id));
                CREATE TABLE parameters (model_id INTEGER NOT NULL, element_id INTEGER NOT NULL, scope TEXT,
                    parameter_key TEXT, name TEXT, storage_type TEXT, has_value INTEGER, raw_value TEXT,
                    display_value TEXT, unit_type_id TEXT);
                CREATE TABLE priority_columns (col_order INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE errors (utc TEXT, model_id INTEGER, element_id INTEGER, stage TEXT, exception_type TEXT, message TEXT);
            ";
            cmd.ExecuteNonQuery();
        }

        using (var trans = conn.BeginTransaction())
        {
            // 0. Priority Columns Metadata
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

            // 1. Models
            using (var mCmd = conn.CreateCommand())
            {
                mCmd.CommandText = "INSERT INTO models VALUES (@id, @path, @status, DateTime('now'), DateTime('now'), @count)";
                var pId = mCmd.Parameters.Add("@id", SqliteType.Integer);
                var pPath = mCmd.Parameters.Add("@path", SqliteType.Text);
                var pStatus = mCmd.Parameters.Add("@status", SqliteType.Text);
                var pCount = mCmd.Parameters.Add("@count", SqliteType.Integer);

                foreach (var m in models)
                {
                    pId.Value = m.modelId;
                    pPath.Value = m.name;
                    pStatus.Value = m.status;
                    pCount.Value = m.count;
                    mCmd.ExecuteNonQuery();
                }
            }

            // 2. Elements
            using (var eCmd = conn.CreateCommand())
            {
                eCmd.CommandText = "INSERT OR IGNORE INTO elements VALUES (@m, @id, @uid, @kind, @catId, @cat, @fam, @type, @tid, @name)";
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
                    eCmd.ExecuteNonQuery();
                }
            }

            // 3. Parameters
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

            trans.Commit();
        }

        // Create indexes after bulk insert for speed
        using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = @"
                CREATE INDEX ix_parameter_name ON parameters(name);
                CREATE INDEX ix_parameter_element ON parameters(model_id, element_id);
                CREATE INDEX ix_element_cat ON elements(category);
            ";
            idxCmd.ExecuteNonQuery();
        }

        // Write CSVs
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var modelNameMap = models.ToDictionary(m => m.modelId, m => m.name);

        // 1. models.csv
        var modelsCsv = Path.Combine(runDir, "models.csv");
        using (var swM = new StreamWriter(modelsCsv, false, utf8Bom))
        {
            swM.WriteLine("model_id,path,status,element_count");
            foreach (var m in models)
                swM.WriteLine($"{m.modelId},{EscapeCsv(m.name)},{m.status},{m.count}");
        }

        // 2. elements.csv
        var elementsCsv = Path.Combine(runDir, "elements.csv");
        using (var swE = new StreamWriter(elementsCsv, false, utf8Bom))
        {
            swE.WriteLine("model_id,model_name,element_id,unique_id,kind,category,family,type_name,type_id,name");
            foreach (var (mId, el) in elements)
            {
                var mName = modelNameMap.GetValueOrDefault(mId, "");
                swE.WriteLine($"{mId},{EscapeCsv(mName)},{el.ElementId},{EscapeCsv(el.UniqueId)},{el.Kind},{EscapeCsv(el.Category)},{EscapeCsv(el.Family)},{EscapeCsv(el.TypeName)},{el.TypeId},{EscapeCsv(el.Name)}");
            }
        }

        // 3. parameters.csv
        var paramsCsv = Path.Combine(runDir, "parameters.csv");
        using (var swP = new StreamWriter(paramsCsv, false, utf8Bom))
        {
            swP.WriteLine("model_id,element_id,scope,property_group,name,storage_type,raw_value,display_value,unit");
            foreach (var (mId, pr) in parameters)
            {
                swP.WriteLine($"{mId},{pr.ElementId},{pr.Scope},{EscapeCsv(pr.GroupName)},{EscapeCsv(pr.Name)},{pr.StorageType},{EscapeCsv(pr.RawValue)},{EscapeCsv(pr.DisplayValue)},{EscapeCsv(pr.Unit)}");
            }
        }

        // 4. elements_wide.csv (Pivoted flat table for Power BI / Excel / Tableau)
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

        // Build final Wide CSV columns: Priority columns first, then top populated parameters up to 60 total dynamic columns
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
            var coreHeaders = new[] { "ModelId", "ModelName", "ElementId", "UniqueId", "Kind", "Category", "Family", "TypeName", "Name" };
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
