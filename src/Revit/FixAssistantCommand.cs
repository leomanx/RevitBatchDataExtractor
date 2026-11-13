using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

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
    public string ImpactScope { get; set; } = "Unchecked";
    public int AffectedInstanceCount { get; set; } = 0;
    public bool IsTypeParameter { get; set; }
    public string ProposedValue { get; set; } = "";
    public string Message { get; set; } = "";
}

[Transaction(TransactionMode.Manual)]
public sealed class FixAssistantCommand : IExternalCommand
{
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
            var win = new FixAssistantWindow(uiDoc, issues, jsonPath);
            win.ShowDialog();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static List<FixIssueItem> LoadFixIssues(string jsonPath)
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

internal sealed class FixAssistantWindow : Window
{
    private readonly UIDocument uiDoc;
    private readonly List<FixIssueItem> allIssues;
    private readonly string sourceJsonPath;
    private readonly ComboBox cmbTeam;
    private readonly TextBox txtReviewer;
    private readonly TextBox txtBulkValue;
    private readonly ListView lstIssues;
    private readonly TextBlock txtStatus;
    private readonly Dictionary<long, int> typeInstanceCounts = new();

    public FixAssistantWindow(UIDocument uiDoc, List<FixIssueItem> allIssues, string sourceJsonPath)
    {
        this.uiDoc = uiDoc;
        this.allIssues = allIssues;
        this.sourceJsonPath = sourceJsonPath;

        PrecomputeImpactForOpenModel();

        Title = $"BIM Fix Assistant - Active Model: {uiDoc.Document.Title}";
        Width = 1120;
        Height = 660;
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
            Text = "Team Authorization, Model Origin Verification & Blast-Radius Preview (AR / ID / MEP / STR)",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(111, 255, 233)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var authRow = new WrapPanel { Orientation = Orientation.Horizontal };
        authRow.Children.Add(new TextBlock { Text = "Authorized Team Code:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

        cmbTeam = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 16, 0), Padding = new Thickness(6, 4, 6, 4) };
        cmbTeam.Items.Add("AR");
        cmbTeam.Items.Add("ID");
        cmbTeam.Items.Add("MEP");
        cmbTeam.Items.Add("STR");
        cmbTeam.SelectedIndex = 0;
        cmbTeam.SelectionChanged += (_, _) => RefreshFilteredList();
        authRow.Children.Add(cmbTeam);

        authRow.Children.Add(new TextBlock { Text = "Engineer / Reviewer ID:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        txtReviewer = new TextBox { Width = 150, Text = Environment.UserName, Margin = new Thickness(0, 0, 16, 0), Padding = new Thickness(6, 4, 6, 4) };
        authRow.Children.Add(txtReviewer);

        authRow.Children.Add(new TextBlock { Text = "Proposed Parameter Value (Bulk Write):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        txtBulkValue = new TextBox { Width = 180, Padding = new Thickness(6, 4, 6, 4) };
        authRow.Children.Add(txtBulkValue);

        topPanel.Children.Add(authRow);
        Grid.SetRow(topPanel, 0);
        root.Children.Add(topPanel);

        lstIssues = new ListView { Margin = new Thickness(0, 0, 0, 12) };
        var gridView = new GridView();
        gridView.Columns.Add(new GridViewColumn { Header = "Team", DisplayMemberBinding = new System.Windows.Data.Binding("Discipline"), Width = 50 });
        gridView.Columns.Add(new GridViewColumn { Header = "State", DisplayMemberBinding = new System.Windows.Data.Binding("LifecycleState"), Width = 70 });
        gridView.Columns.Add(new GridViewColumn { Header = "Source Model", DisplayMemberBinding = new System.Windows.Data.Binding("ModelName"), Width = 145 });
        gridView.Columns.Add(new GridViewColumn { Header = "Rule ID", DisplayMemberBinding = new System.Windows.Data.Binding("RuleId"), Width = 90 });
        gridView.Columns.Add(new GridViewColumn { Header = "Element ID", DisplayMemberBinding = new System.Windows.Data.Binding("ElementId"), Width = 75 });
        gridView.Columns.Add(new GridViewColumn { Header = "Category", DisplayMemberBinding = new System.Windows.Data.Binding("Category"), Width = 105 });
        gridView.Columns.Add(new GridViewColumn { Header = "Parameter", DisplayMemberBinding = new System.Windows.Data.Binding("ParameterName"), Width = 110 });
        gridView.Columns.Add(new GridViewColumn { Header = "Write Scope & Impact", DisplayMemberBinding = new System.Windows.Data.Binding("ImpactScope"), Width = 175 });
        gridView.Columns.Add(new GridViewColumn { Header = "Current", DisplayMemberBinding = new System.Windows.Data.Binding("ActualValue"), Width = 80 });
        gridView.Columns.Add(new GridViewColumn { Header = "Expected", DisplayMemberBinding = new System.Windows.Data.Binding("ExpectedRule"), Width = 140 });
        lstIssues.View = gridView;
        Grid.SetRow(lstIssues, 1);
        root.Children.Add(lstIssues);

        var bottomPanel = new DockPanel();
        txtStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)) };
        DockPanel.SetDock(txtStatus, Dock.Left);
        bottomPanel.Children.Add(txtStatus);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnSelectInRevit = new Button { Content = "Select & Zoom in Revit", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 10, 0) };
        btnSelectInRevit.Click += BtnSelectInRevit_Click;

        var btnApplyFix = new Button
        {
            Content = "Preview Impact & Authorize Parameter Fix",
            Background = new SolidColorBrush(Color.FromRgb(0, 168, 150)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(14, 8, 14, 8)
        };
        btnApplyFix.Click += BtnApplyFix_Click;

        btnPanel.Children.Add(btnSelectInRevit);
        btnPanel.Children.Add(btnApplyFix);
        bottomPanel.Children.Add(btnPanel);

        Grid.SetRow(bottomPanel, 2);
        root.Children.Add(bottomPanel);

        Content = root;
        RefreshFilteredList();
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
        return openTitle.Equals(targetTitle, StringComparison.OrdinalIgnoreCase)
            || openTitle.Contains(targetTitle, StringComparison.OrdinalIgnoreCase)
            || targetTitle.Contains(openTitle, StringComparison.OrdinalIgnoreCase);
    }

    private Element? TryResolveVerifiedElement(Document doc, FixIssueItem item, out string resolutionNote)
    {
        resolutionNote = "";
        if (!IsSameSourceModel(doc, item.ModelName))
        {
            resolutionNote = $"Model Mismatch (Open: {doc.Title})";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.UniqueId))
        {
            var byUnique = doc.GetElement(item.UniqueId);
            if (byUnique != null)
            {
                resolutionNote = "Verified by UniqueId";
                return byUnique;
            }
        }

        if (item.ElementId > 0)
        {
            var byId = doc.GetElement(new ElementId(item.ElementId));
            if (byId != null)
            {
                var catName = byId.Category?.Name ?? "";
                if (string.IsNullOrEmpty(item.Category) || catName.Equals(item.Category, StringComparison.OrdinalIgnoreCase))
                {
                    resolutionNote = "Fallback ElementId (Category Verified)";
                    return byId;
                }
                resolutionNote = $"Blocked ElementId Fallback (Category '{catName}' != '{item.Category}')";
                return null;
            }
        }

        resolutionNote = "Element Not Found in Open Model";
        return null;
    }

    private void PrecomputeImpactForOpenModel()
    {
        var doc = uiDoc.Document;
        typeInstanceCounts.Clear();

        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        foreach (var e in collector)
        {
            var tid = e.GetTypeId();
            if (tid != ElementId.InvalidElementId)
            {
                var key = tid.Value;
                typeInstanceCounts[key] = typeInstanceCounts.GetValueOrDefault(key, 0) + 1;
            }
        }

        foreach (var item in allIssues)
        {
            var el = TryResolveVerifiedElement(doc, item, out var note);
            if (el == null)
            {
                item.ImpactScope = note;
                item.AffectedInstanceCount = 0;
                item.IsTypeParameter = false;
                continue;
            }

            var instParam = el.LookupParameter(item.ParameterName);
            if (instParam != null)
            {
                item.IsTypeParameter = false;
                item.AffectedInstanceCount = 1;
                item.ImpactScope = instParam.IsReadOnly ? "Instance (Read-Only)" : "Instance (1 element)";
                continue;
            }

            var typeId = el.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var typeEl = doc.GetElement(typeId);
                var typeParam = typeEl?.LookupParameter(item.ParameterName);
                if (typeParam != null)
                {
                    var count = typeInstanceCounts.GetValueOrDefault(typeId.Value, 1);
                    item.IsTypeParameter = true;
                    item.AffectedInstanceCount = count;
                    item.ImpactScope = typeParam.IsReadOnly
                        ? $"Type Read-Only ({count:N0} inst)"
                        : $"Type Parameter (Affects {count:N0} instances)";
                    continue;
                }
            }

            item.ImpactScope = "Parameter Not Found on Element/Type";
            item.AffectedInstanceCount = 0;
        }
    }

    private void RefreshFilteredList()
    {
        var team = cmbTeam.SelectedItem?.ToString() ?? "AR";
        var filtered = allIssues.Where(i => i.Discipline.Equals(team, StringComparison.OrdinalIgnoreCase)).ToList();
        lstIssues.ItemsSource = filtered;
        txtStatus.Text = $"Team [{team}]: {filtered.Count:N0} open issue(s) loaded from {Path.GetFileName(sourceJsonPath)}.";
    }

    private List<FixIssueItem> GetTargetItems()
    {
        if (lstIssues.SelectedItems.Count > 0)
            return lstIssues.SelectedItems.Cast<FixIssueItem>().ToList();
        return (lstIssues.ItemsSource as IEnumerable<FixIssueItem>)?.ToList() ?? [];
    }

    private void BtnSelectInRevit_Click(object sender, RoutedEventArgs e)
    {
        var doc = uiDoc.Document;
        var targets = GetTargetItems();
        var ids = new List<ElementId>();

        foreach (var item in targets)
        {
            var el = TryResolveVerifiedElement(doc, item, out _);
            if (el != null) ids.Add(el.Id);
        }

        if (ids.Count == 0)
        {
            MessageBox.Show(this, "No verified elements from the selected issue rows match the currently open Revit document.", "Model / Element Verification", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        uiDoc.Selection.SetElementIds(ids);
        uiDoc.ShowElements(ids);
        txtStatus.Text = $"Selected and zoomed to {ids.Count:N0} verified element(s) in active Revit view.";
    }

    private void BtnApplyFix_Click(object sender, RoutedEventArgs e)
    {
        var teamCode = (cmbTeam.SelectedItem?.ToString() ?? "").Trim().ToUpperInvariant();
        var reviewer = txtReviewer.Text.Trim();
        var newValue = txtBulkValue.Text.Trim();

        if (teamCode is not ("AR" or "ID" or "MEP" or "STR") || string.IsNullOrEmpty(reviewer))
        {
            MessageBox.Show(this, "Valid Team Authorization Code (AR, ID, MEP, STR) and Reviewer ID are required before writing to the model.", "Authorization Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(newValue))
        {
            MessageBox.Show(this, "Please enter a Proposed Parameter Value to write to the selected elements.", "Value Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var doc = uiDoc.Document;
        var targets = GetTargetItems();
        if (targets.Count == 0) return;

        int instanceWrites = 0;
        var uniqueTypeIds = new HashSet<long>();
        int totalTypeAffectedInstances = 0;
        int skippedMismatch = 0;

        foreach (var item in targets)
        {
            var el = TryResolveVerifiedElement(doc, item, out _);
            if (el == null) { skippedMismatch++; continue; }

            var instParam = el.LookupParameter(item.ParameterName);
            if (instParam != null && !instParam.IsReadOnly)
            {
                instanceWrites++;
            }
            else if (el.GetTypeId() != ElementId.InvalidElementId)
            {
                var tid = el.GetTypeId().Value;
                var typeEl = doc.GetElement(el.GetTypeId());
                var typeParam = typeEl?.LookupParameter(item.ParameterName);
                if (typeParam != null && !typeParam.IsReadOnly && uniqueTypeIds.Add(tid))
                {
                    totalTypeAffectedInstances += typeInstanceCounts.GetValueOrDefault(tid, 1);
                }
            }
        }

        if (instanceWrites == 0 && uniqueTypeIds.Count == 0)
        {
            MessageBox.Show(this,
                $"No writable target parameters matched the open document '{doc.Title}'.\n" +
                $"Skipped due to model/element mismatch: {skippedMismatch:N0}.",
                "Write Blocked by Safety Check", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Authorization & Blast-Radius Impact Preview:\n\n" +
            $"• Active Revit Document: {doc.Title}\n" +
            $"• Authorized Team: {teamCode} (Approver: {reviewer})\n" +
            $"• Direct Instance Parameter Writes: {instanceWrites:N0} element(s)\n" +
            $"• Shared Family Type Parameter Writes: {uniqueTypeIds.Count:N0} type(s) (Affects {totalTypeAffectedInstances:N0} total instances in model)\n" +
            $"• Skipped (Model/Category Mismatch): {skippedMismatch:N0}\n" +
            $"• New Value to Write: '{newValue}'\n\n" +
            "Proceed with Revit Transaction and append to audit log?",
            "Confirm Model Modification & Impact Scope", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        int updatedCount = 0;
        var auditLines = new List<string>();

        using (var trans = new Transaction(doc, $"RBDE Fix [{teamCode} - {reviewer}]"))
        {
            trans.Start();
            foreach (var item in targets)
            {
                var el = TryResolveVerifiedElement(doc, item, out var resolutionNote);
                if (el == null) continue;

                var param = el.LookupParameter(item.ParameterName);
                string writeScope = "Instance";
                int blastRadius = 1;

                if (param == null && el.GetTypeId() != ElementId.InvalidElementId)
                {
                    var typeEl = doc.GetElement(el.GetTypeId());
                    param = typeEl?.LookupParameter(item.ParameterName);
                    writeScope = "Type";
                    blastRadius = typeInstanceCounts.GetValueOrDefault(el.GetTypeId().Value, 1);
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
                            openModel = doc.Title,
                            sourceModel = item.ModelName,
                            resolution = resolutionNote,
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

        PrecomputeImpactForOpenModel();
        RefreshFilteredList();
        txtStatus.Text = $"Updated {updatedCount:N0} parameter target(s) under Team [{teamCode}] ({reviewer}). Audit log saved.";
        MessageBox.Show(this, $"Successfully updated {updatedCount:N0} parameter target(s) in '{doc.Title}' and recorded full impact audit log.", "Fix Applied", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
