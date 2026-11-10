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
    public string Discipline { get; set; } = "AR";
    public string Severity { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Family { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string ParameterName { get; set; } = "";
    public string ActualValue { get; set; } = "";
    public string ProposedValue { get; set; } = "";
    public string Message { get; set; } = "";
}

[Transaction(TransactionMode.Manual)]
public sealed class FixAssistantCommand : IExternalCommand
{
    private static readonly HashSet<string> ValidTeamCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "ID", "MEP", "STR"
    };

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

            var doc = uiDoc.Document;
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
                    Title = "Select IDS Fix Package JSON (fix_candidates.json or revit-fix-package-*.json)",
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
                Discipline = GetStr("Discipline", "discipline"),
                Severity = GetStr("Severity", "severity"),
                RuleId = GetStr("RuleId", "rule_id"),
                Category = GetStr("Category", "category"),
                Family = GetStr("Family", "family"),
                TypeName = GetStr("TypeName", "type_name"),
                ParameterName = GetStr("ParameterName", "parameter_name"),
                ActualValue = GetStr("ActualValue", "actual_value"),
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

    public FixAssistantWindow(UIDocument uiDoc, List<FixIssueItem> allIssues, string sourceJsonPath)
    {
        this.uiDoc = uiDoc;
        this.allIssues = allIssues;
        this.sourceJsonPath = sourceJsonPath;

        Title = $"IDS Fix Assistant - Active Model: {uiDoc.Document.Title}";
        Width = 980;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(11, 19, 43));
        Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top Authorization & Filter Bar
        var topPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        topPanel.Children.Add(new TextBlock
        {
            Text = "Team Authorization & IDS Issue Resolution (AR / ID / MEP / STR)",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(111, 255, 233)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var authRow = new WrapPanel { Orientation = Orientation.Horizontal };
        authRow.Children.Add(new TextBlock { Text = "Authorized Team Code:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

        cmbTeam = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 16, 0), Padding = new Thickness(6, 4, 6, 4) };
        cmbTeam.Items.Add("AR");
        cmbTeam.Items.Add("ID");
        cmbTeam.Items.Add("MEP");
        cmbTeam.Items.Add("STR");
        cmbTeam.SelectedIndex = 0;
        cmbTeam.SelectionChanged += (_, _) => RefreshFilteredList();
        authRow.Children.Add(cmbTeam);

        authRow.Children.Add(new TextBlock { Text = "Engineer / Reviewer ID:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        txtReviewer = new TextBox { Width = 160, Text = Environment.UserName, Margin = new Thickness(0, 0, 16, 0), Padding = new Thickness(6, 4, 6, 4) };
        authRow.Children.Add(txtReviewer);

        authRow.Children.Add(new TextBlock { Text = "Proposed Parameter Value (for Bulk Write):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        txtBulkValue = new TextBox { Width = 180, Padding = new Thickness(6, 4, 6, 4) };
        authRow.Children.Add(txtBulkValue);

        topPanel.Children.Add(authRow);
        Grid.SetRow(topPanel, 0);
        root.Children.Add(topPanel);

        // Issues ListView
        lstIssues = new ListView { Margin = new Thickness(0, 0, 0, 12) };
        var gridView = new GridView();
        gridView.Columns.Add(new GridViewColumn { Header = "Team", DisplayMemberBinding = new System.Windows.Data.Binding("Discipline"), Width = 55 });
        gridView.Columns.Add(new GridViewColumn { Header = "Rule ID", DisplayMemberBinding = new System.Windows.Data.Binding("RuleId"), Width = 100 });
        gridView.Columns.Add(new GridViewColumn { Header = "Element ID", DisplayMemberBinding = new System.Windows.Data.Binding("ElementId"), Width = 85 });
        gridView.Columns.Add(new GridViewColumn { Header = "Category", DisplayMemberBinding = new System.Windows.Data.Binding("Category"), Width = 120 });
        gridView.Columns.Add(new GridViewColumn { Header = "Family / Type", DisplayMemberBinding = new System.Windows.Data.Binding("TypeName"), Width = 180 });
        gridView.Columns.Add(new GridViewColumn { Header = "Parameter", DisplayMemberBinding = new System.Windows.Data.Binding("ParameterName"), Width = 120 });
        gridView.Columns.Add(new GridViewColumn { Header = "Current Value", DisplayMemberBinding = new System.Windows.Data.Binding("ActualValue"), Width = 100 });
        gridView.Columns.Add(new GridViewColumn { Header = "Message", DisplayMemberBinding = new System.Windows.Data.Binding("Message"), Width = 280 });
        lstIssues.View = gridView;
        Grid.SetRow(lstIssues, 1);
        root.Children.Add(lstIssues);

        // Bottom Action Buttons
        var bottomPanel = new DockPanel();
        txtStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)) };
        DockPanel.SetDock(txtStatus, Dock.Left);
        bottomPanel.Children.Add(txtStatus);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnSelectInRevit = new Button { Content = "Select & Zoom in Revit", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 10, 0) };
        btnSelectInRevit.Click += BtnSelectInRevit_Click;

        var btnApplyFix = new Button
        {
            Content = "Authorize & Write Parameter Fix to Model",
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

    private void RefreshFilteredList()
    {
        var team = cmbTeam.SelectedItem?.ToString() ?? "AR";
        var filtered = allIssues.Where(i => i.Discipline.Equals(team, StringComparison.OrdinalIgnoreCase)).ToList();
        lstIssues.ItemsSource = filtered;
        txtStatus.Text = $"Team [{team}]: {filtered.Count:N0} open IDS issue(s) loaded from {Path.GetFileName(sourceJsonPath)}.";
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
            Element? el = null;
            if (!string.IsNullOrEmpty(item.UniqueId)) el = doc.GetElement(item.UniqueId);
            el ??= doc.GetElement(new ElementId(item.ElementId));
            if (el != null) ids.Add(el.Id);
        }

        if (ids.Count == 0)
        {
            MessageBox.Show(this, "No matching elements from this issue list were found in the currently open Revit document.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        uiDoc.Selection.SetElementIds(ids);
        uiDoc.ShowElements(ids);
        txtStatus.Text = $"Selected and zoomed to {ids.Count:N0} element(s) in active Revit view.";
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

        var targets = GetTargetItems();
        if (targets.Count == 0) return;

        var confirm = MessageBox.Show(this,
            $"Authorization Confirmation:\n\n" +
            $"• Team Code: {teamCode}\n" +
            $"• Approver: {reviewer}\n" +
            $"• Target Elements: {targets.Count:N0}\n" +
            $"• New Value to Write: '{newValue}'\n\n" +
            "Proceed with Revit Transaction and log audit record?",
            "Confirm Model Modification", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var doc = uiDoc.Document;
        int updatedCount = 0;
        var auditLines = new List<string>();

        using (var trans = new Transaction(doc, $"RBDE Fix [{teamCode} - {reviewer}]"))
        {
            trans.Start();
            foreach (var item in targets)
            {
                Element? el = null;
                if (!string.IsNullOrEmpty(item.UniqueId)) el = doc.GetElement(item.UniqueId);
                el ??= doc.GetElement(new ElementId(item.ElementId));
                if (el == null) continue;

                var param = el.LookupParameter(item.ParameterName);
                if (param == null && el.GetTypeId() != ElementId.InvalidElementId)
                {
                    var typeEl = doc.GetElement(el.GetTypeId());
                    param = typeEl?.LookupParameter(item.ParameterName);
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
                            model = doc.Title,
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

        txtStatus.Text = $"Updated {updatedCount:N0} element(s) under Team [{teamCode}] authorization ({reviewer}). Audit log saved.";
        MessageBox.Show(this, $"Successfully updated {updatedCount:N0} element(s) in Revit and recorded audit log under Team [{teamCode}].", "Fix Applied", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
