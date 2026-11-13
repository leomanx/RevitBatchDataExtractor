using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using ListView = System.Windows.Controls.ListView;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Transform = Autodesk.Revit.DB.Transform;

namespace RevitBatchDataExtractor;

public sealed class FixIssueItem
{
    public bool IsSelected { get; set; } = true;
    public long ElementId { get; set; }
    public string UniqueId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Discipline { get; set; } = "AR";
    public string Severity { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Family { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string ParameterName { get; set; } = "";
    public string ActualValue { get; set; } = "";
    public string ExpectedRule { get; set; } = "";
    public string LifecycleState { get; set; } = "New";
    public string LocationSource { get; set; } = "Unchecked";
    public string ImpactScope { get; set; } = "Unchecked";
    public int AffectedInstanceCount { get; set; } = 0;
    public bool IsTypeParameter { get; set; }
    public bool IsInLinkedModel { get; set; }
    public long LinkInstanceId { get; set; } = -1;
    public long ResolvedElementId { get; set; } = -1;
    public string ProposedValue { get; set; } = "";
    public string Message { get; set; } = "";
}

internal enum RevitActionKind
{
    None,
    ZoomInActiveView,
    SectionBoxIn3D,
    ApplyParameterFix
}

internal sealed class FixExternalEventHandler : IExternalEventHandler
{
    public RevitActionKind PendingAction { get; set; } = RevitActionKind.None;
    public FixAssistantWindow? WindowInstance { get; set; }

    public void Execute(UIApplication app)
    {
        if (WindowInstance == null || PendingAction == RevitActionKind.None) return;
        var action = PendingAction;
        PendingAction = RevitActionKind.None;

        try
        {
            WindowInstance.ExecuteRevitAction(app, action);
        }
        catch (Exception ex)
        {
            MessageBox.Show(WindowInstance, ex.Message, "Revit Action Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public string GetName() => "RBDE_FixAssistantExternalEventHandler";
}

[Transaction(TransactionMode.Manual)]
public sealed class FixAssistantCommand : IExternalCommand
{
    private static FixAssistantWindow? activeWindow;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                Autodesk.Revit.UI.TaskDialog.Show("BIM Fix Assistant", "Please open a Revit model before running the Fix Assistant.");
                return Result.Cancelled;
            }

            if (activeWindow != null && activeWindow.IsLoaded)
            {
                activeWindow.Activate();
                return Result.Succeeded;
            }

            var defaultDir = @"E:\Work\BIM\scripts\Dynamo\RevitBatchDataExtractor\output";
            string? jsonPath = null;

            if (Directory.Exists(defaultDir))
            {
                var latestBatch = Directory.GetDirectories(defaultDir, "batch-*")
                    .OrderByDescending(Directory.GetCreationTimeUtc)
                    .FirstOrDefault();
                if (latestBatch != null)
                {
                    var candidate = Path.Combine(latestBatch, "fix_candidates.json");
                    if (File.Exists(candidate)) jsonPath = candidate;
                }
            }

            if (jsonPath == null)
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "Select Fix Package JSON (fix_candidates.json or revit-fix-package-*.json)",
                    Filter = "Fix Package JSON|*.json"
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;
                jsonPath = dlg.FileName;
            }

            var issues = LoadFixIssues(jsonPath);
            var handler = new FixExternalEventHandler();
            var extEvent = ExternalEvent.Create(handler);

            activeWindow = new FixAssistantWindow(commandData.Application, issues, jsonPath, handler, extEvent);
            handler.WindowInstance = activeWindow;
            activeWindow.Closed += (_, _) => activeWindow = null;

            // Show modelessly so the user can freely orbit, pan, and inspect Host + Linked elements in Revit
            activeWindow.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    internal static List<FixIssueItem> LoadFixIssues(string jsonPath)
    {
        var list = new List<FixIssueItem>();
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!doc.RootElement.TryGetProperty("issues", out var arr)) return list;

        foreach (var item in arr.EnumerateArray())
        {
            long elId = 0;
            if (item.TryGetProperty("ElementId", out var pEl) && pEl.TryGetInt64(out var l1)) elId = l1;
            else if (item.TryGetProperty("element_id", out var pEl2) && pEl2.TryGetInt64(out var l2)) elId = l2;

            string GetStr(string pascal, string snake)
            {
                if (item.TryGetProperty(pascal, out var p1) && p1.ValueKind == JsonValueKind.String) return p1.GetString() ?? "";
                if (item.TryGetProperty(snake, out var p2) && p2.ValueKind == JsonValueKind.String) return p2.GetString() ?? "";
                return "";
            }

            list.Add(new FixIssueItem
            {
                ElementId = elId,
                UniqueId = GetStr("UniqueId", "unique_id"),
                ModelName = GetStr("ModelName", "model_name"),
                Discipline = GetStr("Discipline", "discipline"),
                Severity = GetStr("Severity", "severity"),
                RuleId = GetStr("RuleId", "rule_id"),
                Category = GetStr("Category", "category"),
                Family = GetStr("Family", "family"),
                TypeName = GetStr("TypeName", "type_name"),
                ParameterName = GetStr("ParameterName", "parameter_name"),
                ActualValue = GetStr("ActualValue", "actual_value"),
                ExpectedRule = GetStr("ExpectedRule", "expected_rule"),
                LifecycleState = GetStr("LifecycleState", "lifecycle_state"),
                Message = GetStr("Message", "message")
            });
        }

        return list;
    }
}

internal sealed class ResolvedRevitTarget
{
    public required FixIssueItem Issue { get; init; }
    public required Element Element { get; init; }
    public required Document TargetDocument { get; init; }
    public RevitLinkInstance? LinkInstance { get; init; }
    public bool IsLinked => LinkInstance != null;
    public string ResolutionNote { get; init; } = "";
}

internal sealed class FixAssistantWindow : Window
{
    private UIApplication uiApp;
    private List<FixIssueItem> allIssues;
    private string sourceJsonPath;
    private readonly FixExternalEventHandler extHandler;
    private readonly ExternalEvent extEvent;

    private readonly ComboBox cmbTeam;
    private readonly ComboBox cmbSourceFilter;
    private readonly TextBox txtReviewer;
    private readonly TextBox txtBulkValue;
    private readonly CheckBox chkAutoSectionBox;
    private readonly ListView lstIssues;
    private readonly TextBlock txtStatus;
    private readonly Dictionary<long, int> hostTypeInstanceCounts = new();

    public FixAssistantWindow(
        UIApplication uiApp,
        List<FixIssueItem> allIssues,
        string sourceJsonPath,
        FixExternalEventHandler extHandler,
        ExternalEvent extEvent)
    {
        this.uiApp = uiApp;
        this.allIssues = allIssues;
        this.sourceJsonPath = sourceJsonPath;
        this.extHandler = extHandler;
        this.extEvent = extEvent;

        var hostDoc = uiApp.ActiveUIDocument.Document;
        PrecomputeImpactForHostAndLinks(hostDoc);

        Title = $"BIM Fix & Link Navigator (Modeless) - Active Host: {hostDoc.Title}";
        Width = 1220;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(11, 19, 43));
        Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        topPanel.Children.Add(new TextBlock
        {
            Text = "Modeless Issue Navigator (Host + Revit Links) & Authorized Parameter Fixer",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(111, 255, 233)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var authRow = new WrapPanel { Orientation = Orientation.Horizontal };
        authRow.Children.Add(new TextBlock { Text = "Team Code:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });

        cmbTeam = new ComboBox { Width = 105, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(6, 4, 6, 4), Foreground = Brushes.Black, Background = Brushes.White };
        cmbTeam.Items.Add("ALL");
        cmbTeam.Items.Add("AR");
        cmbTeam.Items.Add("ID");
        cmbTeam.Items.Add("MEP");
        cmbTeam.Items.Add("STR");
        cmbTeam.SelectedIndex = 0;
        cmbTeam.SelectionChanged += (_, _) => RefreshFilteredList();
        authRow.Children.Add(cmbTeam);

        authRow.Children.Add(new TextBlock { Text = "Model Scope:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        cmbSourceFilter = new ComboBox { Width = 175, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(6, 4, 6, 4), Foreground = Brushes.Black, Background = Brushes.White };
        cmbSourceFilter.Items.Add("All (Host + Loaded Links)");
        cmbSourceFilter.Items.Add("Host Model Only (Writable)");
        cmbSourceFilter.Items.Add("Linked Models Only (In Link)");
        cmbSourceFilter.SelectedIndex = 0;
        cmbSourceFilter.SelectionChanged += (_, _) => RefreshFilteredList();
        authRow.Children.Add(cmbSourceFilter);

        authRow.Children.Add(new TextBlock { Text = "Reviewer ID:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        txtReviewer = new TextBox { Width = 135, Text = Environment.UserName, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(6, 4, 6, 4) };
        authRow.Children.Add(txtReviewer);

        authRow.Children.Add(new TextBlock { Text = "Proposed Value (Host Write):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        txtBulkValue = new TextBox { Width = 165, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(6, 4, 6, 4) };
        authRow.Children.Add(txtBulkValue);

        chkAutoSectionBox = new CheckBox
        {
            Content = "Double-Click Row -> Auto 3D Section Box",
            IsChecked = true,
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            VerticalAlignment = VerticalAlignment.Center
        };
        authRow.Children.Add(chkAutoSectionBox);

        topPanel.Children.Add(authRow);
        Grid.SetRow(topPanel, 0);
        root.Children.Add(topPanel);

        lstIssues = new ListView { Margin = new Thickness(0, 0, 0, 12) };
        lstIssues.MouseDoubleClick += LstIssues_MouseDoubleClick;
        var gridView = new GridView();
        gridView.Columns.Add(new GridViewColumn { Header = "Team", DisplayMemberBinding = new System.Windows.Data.Binding("Discipline"), Width = 50 });
        gridView.Columns.Add(new GridViewColumn { Header = "State", DisplayMemberBinding = new System.Windows.Data.Binding("LifecycleState"), Width = 68 });
        gridView.Columns.Add(new GridViewColumn { Header = "Host / Link Location", DisplayMemberBinding = new System.Windows.Data.Binding("LocationSource"), Width = 175 });
        gridView.Columns.Add(new GridViewColumn { Header = "Source File", DisplayMemberBinding = new System.Windows.Data.Binding("ModelName"), Width = 140 });
        gridView.Columns.Add(new GridViewColumn { Header = "Rule ID", DisplayMemberBinding = new System.Windows.Data.Binding("RuleId"), Width = 85 });
        gridView.Columns.Add(new GridViewColumn { Header = "Element ID", DisplayMemberBinding = new System.Windows.Data.Binding("ElementId"), Width = 75 });
        gridView.Columns.Add(new GridViewColumn { Header = "Category", DisplayMemberBinding = new System.Windows.Data.Binding("Category"), Width = 100 });
        gridView.Columns.Add(new GridViewColumn { Header = "Parameter", DisplayMemberBinding = new System.Windows.Data.Binding("ParameterName"), Width = 105 });
        gridView.Columns.Add(new GridViewColumn { Header = "Write Scope & Impact", DisplayMemberBinding = new System.Windows.Data.Binding("ImpactScope"), Width = 175 });
        gridView.Columns.Add(new GridViewColumn { Header = "Current", DisplayMemberBinding = new System.Windows.Data.Binding("ActualValue"), Width = 80 });
        gridView.Columns.Add(new GridViewColumn { Header = "Expected", DisplayMemberBinding = new System.Windows.Data.Binding("ExpectedRule"), Width = 130 });
        lstIssues.View = gridView;
        Grid.SetRow(lstIssues, 1);
        root.Children.Add(lstIssues);

        var bottomPanel = new DockPanel();
        txtStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)) };
        DockPanel.SetDock(txtStatus, Dock.Left);
        bottomPanel.Children.Add(txtStatus);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var btnRefreshLinks = new Button
        {
            Content = "Re-Scan Host & Links",
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnRefreshLinks.Click += (_, _) =>
        {
            if (uiApp.ActiveUIDocument?.Document != null)
            {
                PrecomputeImpactForHostAndLinks(uiApp.ActiveUIDocument.Document);
                RefreshFilteredList();
            }
        };

        var btnZoomActive = new Button
        {
            Content = "Select & Zoom in Active View (Host / Link)",
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnZoomActive.Click += (_, _) => RaiseAction(RevitActionKind.ZoomInActiveView);

        var btnSectionBox3D = new Button
        {
            Content = "Isolate with 3D Section Box (Host / Link)",
            Background = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnSectionBox3D.Click += (_, _) => RaiseAction(RevitActionKind.SectionBoxIn3D);

        var btnApplyFix = new Button
        {
            Content = "Preview Impact & Write Fix (Host Model)",
            Background = new SolidColorBrush(Color.FromRgb(0, 168, 150)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(14, 8, 14, 8)
        };
        btnApplyFix.Click += (_, _) => RaiseAction(RevitActionKind.ApplyParameterFix);

        btnPanel.Children.Add(btnRefreshLinks);
        btnPanel.Children.Add(btnZoomActive);
        btnPanel.Children.Add(btnSectionBox3D);
        btnPanel.Children.Add(btnApplyFix);
        bottomPanel.Children.Add(btnPanel);

        Grid.SetRow(bottomPanel, 2);
        root.Children.Add(bottomPanel);

        Content = root;
        RefreshFilteredList();
    }

    private void LstIssues_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (lstIssues.SelectedItem is not FixIssueItem) return;
        RaiseAction(chkAutoSectionBox.IsChecked == true ? RevitActionKind.SectionBoxIn3D : RevitActionKind.ZoomInActiveView);
    }

    private void RaiseAction(RevitActionKind kind)
    {
        extHandler.PendingAction = kind;
        extEvent.Raise();
    }

    private static string NormalizeModelTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = Path.GetFileNameWithoutExtension(raw.Trim());
        var vIdx = name.LastIndexOf(" (v", StringComparison.OrdinalIgnoreCase);
        if (vIdx > 0) name = name[..vIdx];
        return name.Trim();
    }

    private static bool IsSameSourceModel(Document doc, string issueModelName)
    {
        if (string.IsNullOrWhiteSpace(issueModelName)) return true;
        var openTitle = NormalizeModelTitle(doc.Title);
        var targetTitle = NormalizeModelTitle(issueModelName);
        if (string.IsNullOrEmpty(openTitle) || string.IsNullOrEmpty(targetTitle)) return false;
        return openTitle.Equals(targetTitle, StringComparison.OrdinalIgnoreCase)
            || openTitle.Contains(targetTitle, StringComparison.OrdinalIgnoreCase)
            || targetTitle.Contains(openTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static Element? TryFindInSingleDocument(Document targetDoc, FixIssueItem item, bool modelTitleMatched, out string note)
    {
        note = "";
        if (!string.IsNullOrWhiteSpace(item.UniqueId))
        {
            var byUnique = targetDoc.GetElement(item.UniqueId);
            if (byUnique != null)
            {
                note = "Verified by UniqueId";
                return byUnique;
            }
        }

        if (modelTitleMatched && item.ElementId > 0)
        {
            var byId = targetDoc.GetElement(new ElementId(item.ElementId));
            if (byId != null)
            {
                var catName = byId.Category?.Name ?? "";
                if (string.IsNullOrEmpty(item.Category) || catName.Equals(item.Category, StringComparison.OrdinalIgnoreCase))
                {
                    note = "Fallback ElementId (Category Verified)";
                    return byId;
                }
                note = $"Blocked ElementId Fallback (Category '{catName}' != '{item.Category}')";
                return null;
            }
        }

        return null;
    }

    private ResolvedRevitTarget? ResolveInHostOrLinks(Document hostDoc, List<RevitLinkInstance> loadedLinks, FixIssueItem item, out string failureReason)
    {
        failureReason = "";

        // 1. Check Host Document first
        bool hostMatchesName = IsSameSourceModel(hostDoc, item.ModelName);
        var hostElem = TryFindInSingleDocument(hostDoc, item, hostMatchesName, out var hostNote);
        if (hostElem != null)
        {
            return new ResolvedRevitTarget
            {
                Issue = item,
                Element = hostElem,
                TargetDocument = hostDoc,
                LinkInstance = null,
                ResolutionNote = hostNote
            };
        }

        // 2. Search across all loaded RevitLinkInstances (prioritizing links whose document title matches item.ModelName)
        var orderedLinks = loadedLinks
            .OrderByDescending(l =>
            {
                var lDoc = l.GetLinkDocument();
                return lDoc != null && IsSameSourceModel(lDoc, item.ModelName);
            })
            .ToList();

        foreach (var linkInst in orderedLinks)
        {
            var linkDoc = linkInst.GetLinkDocument();
            if (linkDoc == null) continue;

            bool linkMatchesName = IsSameSourceModel(linkDoc, item.ModelName);
            var linkElem = TryFindInSingleDocument(linkDoc, item, linkMatchesName, out var linkNote);
            if (linkElem != null)
            {
                return new ResolvedRevitTarget
                {
                    Issue = item,
                    Element = linkElem,
                    TargetDocument = linkDoc,
                    LinkInstance = linkInst,
                    ResolutionNote = linkNote
                };
            }
        }

        failureReason = "Not Found in Host or Loaded Links";
        return null;
    }

    private void PrecomputeImpactForHostAndLinks(Document hostDoc)
    {
        hostTypeInstanceCounts.Clear();

        var collector = new FilteredElementCollector(hostDoc).WhereElementIsNotElementType();
        foreach (var e in collector)
        {
            var tid = e.GetTypeId();
            if (tid != ElementId.InvalidElementId)
            {
                var key = tid.Value;
                hostTypeInstanceCounts[key] = hostTypeInstanceCounts.GetValueOrDefault(key, 0) + 1;
            }
        }

        var loadedLinks = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null)
            .ToList();

        foreach (var item in allIssues)
        {
            var resolved = ResolveInHostOrLinks(hostDoc, loadedLinks, item, out var failNote);
            if (resolved == null)
            {
                item.LocationSource = "Unloaded / Not Found";
                item.ImpactScope = failNote;
                item.IsInLinkedModel = false;
                item.LinkInstanceId = -1;
                item.ResolvedElementId = -1;
                item.AffectedInstanceCount = 0;
                item.IsTypeParameter = false;
                continue;
            }

            var el = resolved.Element;
            item.ResolvedElementId = el.Id.Value;

            if (resolved.IsLinked && resolved.LinkInstance != null)
            {
                item.IsInLinkedModel = true;
                item.LinkInstanceId = resolved.LinkInstance.Id.Value;
                item.LocationSource = $"In Link: {resolved.TargetDocument.Title}";
                item.ImpactScope = "Linked Element (Zoom/Inspect Only)";
                item.AffectedInstanceCount = 0;
                item.IsTypeParameter = false;
                continue;
            }

            // Host Document Element
            item.IsInLinkedModel = false;
            item.LinkInstanceId = -1;
            item.LocationSource = $"Host: {hostDoc.Title}";

            var instParam = el.LookupParameter(item.ParameterName);
            if (instParam != null)
            {
                item.IsTypeParameter = false;
                item.AffectedInstanceCount = 1;
                item.ImpactScope = instParam.IsReadOnly ? "Instance (Read-Only)" : "Host Instance (1 element)";
                continue;
            }

            var typeId = el.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var typeEl = hostDoc.GetElement(typeId);
                var typeParam = typeEl?.LookupParameter(item.ParameterName);
                if (typeParam != null)
                {
                    var count = hostTypeInstanceCounts.GetValueOrDefault(typeId.Value, 1);
                    item.IsTypeParameter = true;
                    item.AffectedInstanceCount = count;
                    item.ImpactScope = typeParam.IsReadOnly
                        ? $"Type Read-Only ({count:N0} inst)"
                        : $"Host Type (Affects {count:N0} instances)";
                    continue;
                }
            }

            item.ImpactScope = "Parameter Not Found on Element/Type";
            item.AffectedInstanceCount = 0;
        }
    }

    private void RefreshFilteredList()
    {
        var team = cmbTeam.SelectedItem?.ToString() ?? "ALL";
        var scopeIdx = cmbSourceFilter.SelectedIndex;

        var filtered = allIssues.Where(i =>
        {
            bool teamOk = team == "ALL" || i.Discipline.Equals(team, StringComparison.OrdinalIgnoreCase);
            if (!teamOk) return false;
            if (scopeIdx == 1) return !i.IsInLinkedModel && i.ResolvedElementId > 0;
            if (scopeIdx == 2) return i.IsInLinkedModel;
            return true;
        }).ToList();

        lstIssues.ItemsSource = filtered;
        int hostCount = filtered.Count(x => !x.IsInLinkedModel && x.ResolvedElementId > 0);
        int linkCount = filtered.Count(x => x.IsInLinkedModel);
        txtStatus.Text = $"Showing {filtered.Count:N0} issue(s) | Host Writable: {hostCount:N0} | In Loaded Revit Links: {linkCount:N0} (Double-click any row to zoom/isolate).";
    }

    private List<FixIssueItem> GetTargetItems()
    {
        if (lstIssues.SelectedItems.Count > 0)
            return lstIssues.SelectedItems.Cast<FixIssueItem>().ToList();
        return (lstIssues.ItemsSource as IEnumerable<FixIssueItem>)?.ToList() ?? [];
    }

    public void ExecuteRevitAction(UIApplication app, RevitActionKind action)
    {
        this.uiApp = app;
        var uiDoc = app.ActiveUIDocument;
        if (uiDoc?.Document == null) return;

        switch (action)
        {
            case RevitActionKind.ZoomInActiveView:
                NavigateToElements(uiDoc, isolateIn3DSectionBox: false);
                break;
            case RevitActionKind.SectionBoxIn3D:
                NavigateToElements(uiDoc, isolateIn3DSectionBox: true);
                break;
            case RevitActionKind.ApplyParameterFix:
                ApplyAuthorizedParameterFix(uiDoc);
                break;
        }
    }

    private static BoundingBoxXYZ? ComputeTransformedWorldBoundingBox(Element elem, Transform? linkTransform)
    {
        var bbox = elem.get_BoundingBox(null);
        if (bbox == null)
        {
            if (elem.Location is LocationPoint lp)
            {
                var pt = lp.Point;
                bbox = new BoundingBoxXYZ { Min = pt - new XYZ(2, 2, 2), Max = pt + new XYZ(2, 2, 2) };
            }
            else if (elem.Location is LocationCurve lc && lc.Curve != null)
            {
                var p0 = lc.Curve.GetEndPoint(0);
                var p1 = lc.Curve.GetEndPoint(1);
                bbox = new BoundingBoxXYZ
                {
                    Min = new XYZ(Math.Min(p0.X, p1.X) - 2, Math.Min(p0.Y, p1.Y) - 2, Math.Min(p0.Z, p1.Z) - 2),
                    Max = new XYZ(Math.Max(p0.X, p1.X) + 2, Math.Max(p0.Y, p1.Y) + 2, Math.Max(p0.Z, p1.Z) + 2)
                };
            }
        }

        if (bbox == null) return null;

        var totalTx = bbox.Transform != null && !bbox.Transform.IsIdentity
            ? (linkTransform != null ? linkTransform.Multiply(bbox.Transform) : bbox.Transform)
            : (linkTransform ?? Transform.Identity);

        // Transform all 8 corners of the element bounding box into Host World Coordinates
        var corners = new[]
        {
            new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
            new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
            new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
            new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
            new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
            new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
            new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
            new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
        }.Select(totalTx.OfPoint).ToList();

        return new BoundingBoxXYZ
        {
            Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
            Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z))
        };
    }

    private void NavigateToElements(UIDocument uiDoc, bool isolateIn3DSectionBox)
    {
        var hostDoc = uiDoc.Document;
        var targets = GetTargetItems();
        if (targets.Count == 0) return;

        var loadedLinks = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null)
            .ToList();

        var hostElementIds = new List<ElementId>();
        var linkReferences = new List<Reference>();
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool hasWorldBounds = false;
        int resolvedHostCount = 0;
        int resolvedLinkCount = 0;

        foreach (var item in targets)
        {
            var res = ResolveInHostOrLinks(hostDoc, loadedLinks, item, out _);
            if (res == null) continue;

            Transform? linkTx = null;
            if (res.IsLinked && res.LinkInstance != null)
            {
                resolvedLinkCount++;
                linkTx = res.LinkInstance.GetTotalTransform();
                try
                {
                    var linkRef = new Reference(res.Element).CreateLinkReference(res.LinkInstance);
                    if (linkRef != null) linkReferences.Add(linkRef);
                }
                catch
                {
                    // Fallback: select the RevitLinkInstance itself if individual link reference fails
                    hostElementIds.Add(res.LinkInstance.Id);
                }
            }
            else
            {
                resolvedHostCount++;
                hostElementIds.Add(res.Element.Id);
            }

            var worldBox = ComputeTransformedWorldBoundingBox(res.Element, linkTx);
            if (worldBox != null)
            {
                hasWorldBounds = true;
                minX = Math.Min(minX, worldBox.Min.X);
                minY = Math.Min(minY, worldBox.Min.Y);
                minZ = Math.Min(minZ, worldBox.Min.Z);
                maxX = Math.Max(maxX, worldBox.Max.X);
                maxY = Math.Max(maxY, worldBox.Max.Y);
                maxZ = Math.Max(maxZ, worldBox.Max.Z);
            }
        }

        if (resolvedHostCount == 0 && resolvedLinkCount == 0)
        {
            MessageBox.Show(this,
                "None of the selected elements were found in the active Host Document or any currently loaded Revit Links.",
                "Element Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Pad the world bounding box by 3.5 feet (~1.05 m) so the element has clear visual context
        const double pad = 3.5;
        var paddedMin = new XYZ(minX - pad, minY - pad, minZ - pad);
        var paddedMax = new XYZ(maxX + pad, maxY + pad, maxZ + pad);

        // Optional: Switch to or update 3D View with a tight Section Box (works for BOTH Host and Linked elements!)
        if (isolateIn3DSectionBox && hasWorldBounds)
        {
            View3D? target3D = hostDoc.ActiveView as View3D;
            if (target3D == null || target3D.IsTemplate || target3D.IsPerspective)
            {
                target3D = new FilteredElementCollector(hostDoc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .OrderByDescending(v => v.Name.Equals("{3D}", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective);
            }

            if (target3D != null)
            {
                using (var trans = new Transaction(hostDoc, "RBDE Section Box around Host/Linked Element"))
                {
                    trans.Start();
                    target3D.SetSectionBox(new BoundingBoxXYZ
                    {
                        Min = paddedMin,
                        Max = paddedMax
                    });
                    trans.Commit();
                }

                if (uiDoc.ActiveView.Id != target3D.Id)
                {
                    uiDoc.ActiveView = target3D;
                }
            }
        }

        // Highlight selection in Revit viewport (supports Linked elements via SetReferences and Host elements via SetElementIds)
        try
        {
            if (linkReferences.Count > 0 && hostElementIds.Count == 0)
            {
                uiDoc.Selection.SetReferences(linkReferences);
            }
            else if (hostElementIds.Count > 0)
            {
                uiDoc.Selection.SetElementIds(hostElementIds);
            }
        }
        catch { }

        // Zoom camera directly to the transformed coordinates in the active UIView
        if (hasWorldBounds)
        {
            var openUiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uiDoc.ActiveView.Id)
                          ?? uiDoc.GetOpenUIViews().FirstOrDefault();
            openUiView?.ZoomAndCenterRectangle(paddedMin, paddedMax);
        }
        else if (hostElementIds.Count > 0)
        {
            uiDoc.ShowElements(hostElementIds);
        }

        txtStatus.Text = $"Navigated in '{uiDoc.ActiveView.Name}': {resolvedHostCount:N0} Host element(s) + {resolvedLinkCount:N0} Linked element(s) zoomed & highlighted.";
    }

    private void ApplyAuthorizedParameterFix(UIDocument uiDoc)
    {
        var teamCode = (cmbTeam.SelectedItem?.ToString() ?? "").Trim().ToUpperInvariant();
        var reviewer = txtReviewer.Text.Trim();
        var newValue = txtBulkValue.Text.Trim();

        if (teamCode is not ("AR" or "ID" or "MEP" or "STR") || string.IsNullOrEmpty(reviewer))
        {
            MessageBox.Show(this, "Please select a specific Team Authorization Code (AR, ID, MEP, or STR) and enter your Reviewer ID before writing to the model.", "Authorization Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(newValue))
        {
            MessageBox.Show(this, "Please enter a Proposed Parameter Value to write to the selected Host elements.", "Value Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var hostDoc = uiDoc.Document;
        var targets = GetTargetItems();
        if (targets.Count == 0) return;

        var loadedLinks = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null)
            .ToList();

        int instanceWrites = 0;
        var uniqueTypeIds = new HashSet<long>();
        int totalTypeAffectedInstances = 0;
        int linkedReadOnlyCount = 0;
        int skippedMismatch = 0;

        foreach (var item in targets)
        {
            var res = ResolveInHostOrLinks(hostDoc, loadedLinks, item, out _);
            if (res == null) { skippedMismatch++; continue; }
            if (res.IsLinked) { linkedReadOnlyCount++; continue; }

            var el = res.Element;
            var instParam = el.LookupParameter(item.ParameterName);
            if (instParam != null && !instParam.IsReadOnly)
            {
                instanceWrites++;
            }
            else if (el.GetTypeId() != ElementId.InvalidElementId)
            {
                var tid = el.GetTypeId().Value;
                var typeEl = hostDoc.GetElement(el.GetTypeId());
                var typeParam = typeEl?.LookupParameter(item.ParameterName);
                if (typeParam != null && !typeParam.IsReadOnly && uniqueTypeIds.Add(tid))
                {
                    totalTypeAffectedInstances += hostTypeInstanceCounts.GetValueOrDefault(tid, 1);
                }
            }
        }

        if (instanceWrites == 0 && uniqueTypeIds.Count == 0)
        {
            MessageBox.Show(this,
                $"No writable Host Document parameters matched in '{hostDoc.Title}'.\n\n" +
                $"• Elements inside Revit Links (Read-Only from Host): {linkedReadOnlyCount:N0}\n" +
                $"• Unmatched / Read-Only: {skippedMismatch:N0}\n\n" +
                "Tip: You can still Select, Zoom, and Isolate Linked Elements in 3D from this window, or open the linked RVT source file directly to write parameter fixes.",
                "Linked / Read-Only Elements", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Authorization & Blast-Radius Impact Preview:\n\n" +
            $"• Active Host Document: {hostDoc.Title}\n" +
            $"• Authorized Team: {teamCode} (Approver: {reviewer})\n" +
            $"• Direct Host Instance Writes: {instanceWrites:N0} element(s)\n" +
            $"• Shared Host Family Type Writes: {uniqueTypeIds.Count:N0} type(s) (Affects {totalTypeAffectedInstances:N0} total instances in model)\n" +
            $"• Skipped Linked Elements (Read-Only in Host): {linkedReadOnlyCount:N0}\n" +
            $"• Skipped Unmatched: {skippedMismatch:N0}\n" +
            $"• New Value to Write: '{newValue}'\n\n" +
            "Proceed with Revit Transaction and append to audit log?",
            "Confirm Model Modification & Impact Scope", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        int updatedCount = 0;
        var auditLines = new List<string>();

        using (var trans = new Transaction(hostDoc, $"RBDE Fix [{teamCode} - {reviewer}]"))
        {
            trans.Start();
            foreach (var item in targets)
            {
                var res = ResolveInHostOrLinks(hostDoc, loadedLinks, item, out _);
                if (res == null || res.IsLinked) continue;

                var el = res.Element;
                var param = el.LookupParameter(item.ParameterName);
                string writeScope = "Instance";
                int blastRadius = 1;

                if (param == null && el.GetTypeId() != ElementId.InvalidElementId)
                {
                    var typeEl = hostDoc.GetElement(el.GetTypeId());
                    param = typeEl?.LookupParameter(item.ParameterName);
                    writeScope = "Type";
                    blastRadius = hostTypeInstanceCounts.GetValueOrDefault(el.GetTypeId().Value, 1);
                }

                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                {
                    var oldVal = param.AsString() ?? "";
                    if (param.Set(newValue))
                    {
                        updatedCount++;
                        auditLines.Add(JsonSerializer.Serialize(new
                        {
                            utc = DateTime.UtcNow.ToString("O"),
                            teamCode,
                            approver = reviewer,
                            openModel = hostDoc.Title,
                            sourceModel = item.ModelName,
                            resolution = res.ResolutionNote,
                            writeScope,
                            affectedInstancesInModel = blastRadius,
                            ruleId = item.RuleId,
                            elementId = item.ElementId,
                            uniqueId = item.UniqueId,
                            parameter = item.ParameterName,
                            oldValue = oldVal,
                            newValue
                        }));
                    }
                }
            }
            trans.Commit();
        }

        if (auditLines.Count > 0)
        {
            var auditPath = Path.Combine(Path.GetDirectoryName(sourceJsonPath)!, "fix_audit_log.jsonl");
            File.AppendAllLines(auditPath, auditLines, Encoding.UTF8);
        }

        PrecomputeImpactForHostAndLinks(hostDoc);
        RefreshFilteredList();
        txtStatus.Text = $"Updated {updatedCount:N0} Host parameter target(s) under Team [{teamCode}] ({reviewer}). Audit log saved.";
        MessageBox.Show(this, $"Successfully updated {updatedCount:N0} parameter target(s) in '{hostDoc.Title}' and recorded full impact audit log.", "Fix Applied", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
