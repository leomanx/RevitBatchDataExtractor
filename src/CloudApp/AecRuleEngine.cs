using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RBDE.CloudApp;

public sealed record IdsIssueRecord(
    int ModelId,
    long ElementId,
    string UniqueId,
    string Category,
    string Family,
    string TypeName,
    string RuleId,
    string RequirementId,
    string Discipline,
    string Severity,
    string ParameterName,
    string ActualValue,
    string ExpectedRule,
    string EvaluationStatus, // "Failed", "InvalidValue", "NotEvaluated"
    string LifecycleState,   // "New", "Active", "Regressed"
    string Message);

public sealed record RuleEvaluationSummary(
    int ApplicableChecks,
    int EvaluatedChecks,
    int PassedChecks,
    int FailedChecks,
    int InvalidValueChecks,
    int NotEvaluatedChecks,
    List<IdsIssueRecord> Issues);

public sealed class IdsSpecification
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string Discipline { get; set; } = "AR";
    public string Severity { get; set; } = "Medium";
    public List<string> Categories { get; set; } = [];
    public List<IdsRequirement> Requirements { get; set; } = [];
}

public sealed class IdsRequirement
{
    public string PropertySet { get; set; } = "";
    public string Scope { get; set; } = ""; // optional: "instance" or "type"
    public List<string> BaseNames { get; set; } = [];
    public string Cardinality { get; set; } = "required";
    public string? Pattern { get; set; }
    public List<string>? AllowedValues { get; set; }
    public double? MinInclusive { get; set; }
    public double? MaxInclusive { get; set; }
    public string Instructions { get; set; } = "";
}

public static class AecRuleEngine
{
    public static List<IdsSpecification> LoadSpecifications(string jsonPath)
    {
        var specs = new List<IdsSpecification>();
        if (!File.Exists(jsonPath)) return specs;

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!doc.RootElement.TryGetProperty("specifications", out var specArr)) return specs;

        foreach (var s in specArr.EnumerateArray())
        {
            var spec = new IdsSpecification
            {
                Id = s.TryGetProperty("id", out var pId) ? pId.GetString() ?? "" : "",
                Name = s.TryGetProperty("name", out var pName) ? pName.GetString() ?? "" : "",
                RequirementId = s.TryGetProperty("requirementId", out var pReq) ? pReq.GetString() ?? "" : "",
                Discipline = s.TryGetProperty("discipline", out var pDisc) ? pDisc.GetString() ?? "AR" : "AR",
                Severity = s.TryGetProperty("severity", out var pSev) ? pSev.GetString() ?? "Medium" : "Medium"
            };

            if (s.TryGetProperty("applicability", out var app) && app.TryGetProperty("categories", out var cats))
            {
                foreach (var c in cats.EnumerateArray())
                {
                    var cv = c.GetString();
                    if (!string.IsNullOrWhiteSpace(cv)) spec.Categories.Add(cv);
                }
            }

            if (s.TryGetProperty("requirements", out var reqs))
            {
                foreach (var r in reqs.EnumerateArray())
                {
                    var req = new IdsRequirement
                    {
                        PropertySet = r.TryGetProperty("propertySet", out var ps) ? ps.GetString() ?? "" : "",
                        Scope = r.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "",
                        Cardinality = r.TryGetProperty("cardinality", out var card) ? card.GetString() ?? "required" : "required",
                        Pattern = r.TryGetProperty("pattern", out var pat) ? pat.GetString() : null,
                        MinInclusive = r.TryGetProperty("minInclusive", out var minV) && minV.TryGetDouble(out var dMin) ? dMin : null,
                        MaxInclusive = r.TryGetProperty("maxInclusive", out var maxV) && maxV.TryGetDouble(out var dMax) ? dMax : null,
                        Instructions = r.TryGetProperty("instructions", out var inst) ? inst.GetString() ?? "" : ""
                    };

                    if (r.TryGetProperty("baseNames", out var bNames))
                    {
                        foreach (var bn in bNames.EnumerateArray())
                        {
                            var bnv = bn.GetString();
                            if (!string.IsNullOrWhiteSpace(bnv)) req.BaseNames.Add(bnv);
                        }
                    }

                    if (r.TryGetProperty("allowedValues", out var avs))
                    {
                        req.AllowedValues = [];
                        foreach (var av in avs.EnumerateArray())
                        {
                            var avv = av.GetString();
                            if (!string.IsNullOrWhiteSpace(avv)) req.AllowedValues.Add(avv);
                        }
                    }

                    spec.Requirements.Add(req);
                }
            }

            specs.Add(spec);
        }

        return specs;
    }

    public static RuleEvaluationSummary Evaluate(
        List<IdsSpecification> specs,
        List<(int modelId, ExtractedElement elem)> elements,
        List<(int modelId, ExtractedParam param)> parameters,
        bool extractAllCategories,
        HashSet<string> extractedCategories,
        bool extractAllParameters,
        HashSet<string> extractedParameters)
    {
        var issues = new List<IdsIssueRecord>();
        int applicableChecks = 0;
        int evaluatedChecks = 0;
        int passedChecks = 0;
        int failedChecks = 0;
        int invalidValueChecks = 0;
        int notEvaluatedChecks = 0;

        // Index all parameters per element as a list so Scope and PropertySet (GroupName) can be disambiguated
        var paramLookup = new Dictionary<(int mId, long elId), List<ExtractedParam>>();
        foreach (var (mId, pr) in parameters)
        {
            var key = (mId, pr.ElementId);
            if (!paramLookup.TryGetValue(key, out var list))
            {
                list = [];
                paramLookup[key] = list;
            }
            list.Add(pr);
        }

        // 1. Evaluate IDS-inspired Specifications on Instances
        foreach (var (mId, el) in elements)
        {
            if (el.Kind != "instance") continue;
            paramLookup.TryGetValue((mId, el.ElementId), out var pList);

            foreach (var spec in specs)
            {
                if (spec.Categories.Count > 0 && !spec.Categories.Any(c => c.Equals(el.Category, StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreach (var req in spec.Requirements)
                {
                    if (req.BaseNames.Count == 0) continue;
                    applicableChecks++;

                    var primaryParamName = req.BaseNames[0];
                    var expectedDesc = BuildExpectedDescription(req);

                    // Check Data Confidence: Was this parameter in the extraction scope?
                    bool paramInExtractionScope = extractAllParameters || req.BaseNames.Any(extractedParameters.Contains);
                    if (!paramInExtractionScope)
                    {
                        notEvaluatedChecks++;
                        issues.Add(new IdsIssueRecord(
                            mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                            spec.Id, spec.RequirementId, spec.Discipline, "Low",
                            primaryParamName, "(not extracted)", expectedDesc,
                            "NotEvaluated", "New",
                            $"Cannot evaluate rule '{spec.Id}': Parameter '{primaryParamName}' was excluded from the extraction filter."));
                        continue;
                    }

                    evaluatedChecks++;
                    var matchedParam = FindMatchingParameter(pList, req);

                    if (matchedParam == null)
                    {
                        if (req.Cardinality.Equals("required", StringComparison.OrdinalIgnoreCase))
                        {
                            failedChecks++;
                            issues.Add(new IdsIssueRecord(
                                mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                                primaryParamName, "(missing in model)", expectedDesc,
                                "Failed", "New",
                                string.IsNullOrEmpty(req.Instructions) ? $"Missing required parameter '{primaryParamName}' in model." : req.Instructions));
                        }
                        else
                        {
                            passedChecks++;
                        }
                        continue;
                    }

                    bool ruleFailed = false;

                    // Numeric bounds validation (never silently pass if unparseable!)
                    if (req.MinInclusive.HasValue || req.MaxInclusive.HasValue)
                    {
                        if (!double.TryParse(matchedParam.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numVal))
                        {
                            ruleFailed = true;
                            invalidValueChecks++;
                            issues.Add(new IdsIssueRecord(
                                mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                                matchedParam.Name, matchedParam.DisplayValue, expectedDesc,
                                "InvalidValue", "New",
                                $"Parameter '{matchedParam.Name}' has non-numeric value '{matchedParam.DisplayValue}' and cannot be verified against numeric bounds ({expectedDesc})."));
                        }
                        else
                        {
                            if (req.MinInclusive.HasValue && numVal < req.MinInclusive.Value)
                            {
                                ruleFailed = true;
                                failedChecks++;
                                issues.Add(new IdsIssueRecord(
                                    mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                    spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                                    matchedParam.Name, matchedParam.DisplayValue, expectedDesc,
                                    "Failed", "New",
                                    $"Value '{matchedParam.DisplayValue}' is below minimum ({req.MinInclusive.Value}). {req.Instructions}"));
                            }
                            else if (req.MaxInclusive.HasValue && numVal > req.MaxInclusive.Value)
                            {
                                ruleFailed = true;
                                failedChecks++;
                                issues.Add(new IdsIssueRecord(
                                    mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                    spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                                    matchedParam.Name, matchedParam.DisplayValue, expectedDesc,
                                    "Failed", "New",
                                    $"Value '{matchedParam.DisplayValue}' exceeds maximum ({req.MaxInclusive.Value}). {req.Instructions}"));
                            }
                        }
                    }

                    // Enumeration check
                    if (!ruleFailed && req.AllowedValues is { Count: > 0 } &&
                        !req.AllowedValues.Any(a => a.Equals(matchedParam.DisplayValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        ruleFailed = true;
                        failedChecks++;
                        issues.Add(new IdsIssueRecord(
                            mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                            spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                            matchedParam.Name, matchedParam.DisplayValue, expectedDesc,
                            "Failed", "New",
                            $"Value '{matchedParam.DisplayValue}' is not in allowed values ({string.Join(", ", req.AllowedValues)})."));
                    }

                    // Regex pattern check
                    if (!ruleFailed && !string.IsNullOrEmpty(req.Pattern) && !Regex.IsMatch(matchedParam.DisplayValue, req.Pattern))
                    {
                        ruleFailed = true;
                        failedChecks++;
                        issues.Add(new IdsIssueRecord(
                            mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                            spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                            matchedParam.Name, matchedParam.DisplayValue, expectedDesc,
                            "Failed", "New",
                            $"Value '{matchedParam.DisplayValue}' does not match required pattern '{req.Pattern}'."));
                    }

                    if (!ruleFailed)
                    {
                        passedChecks++;
                    }
                }
            }
        }

        // 2. Built-in Duplicate Mark Check per (Model, Category, Mark)
        var markGroups = new Dictionary<(int mId, string cat, string mark), List<ExtractedElement>>();
        foreach (var (mId, el) in elements)
        {
            if (el.Kind != "instance") continue;
            if (!paramLookup.TryGetValue((mId, el.ElementId), out var pList)) continue;
            var markParam = pList.FirstOrDefault(p => p.Name.Equals("Mark", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.DisplayValue));
            if (markParam == null) continue;

            var key = (mId, el.Category.ToUpperInvariant(), markParam.DisplayValue.Trim().ToUpperInvariant());
            if (!markGroups.TryGetValue(key, out var list))
            {
                list = [];
                markGroups[key] = list;
            }
            list.Add(el);
        }

        foreach (var (key, dupList) in markGroups)
        {
            if (dupList.Count <= 1) continue;
            var disc = key.cat is "FURNITURE" or "CASEWORK" ? "ID" : "AR";
            foreach (var el in dupList)
            {
                applicableChecks++;
                evaluatedChecks++;
                failedChecks++;
                issues.Add(new IdsIssueRecord(
                    key.mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                    "IDS-QA-DUPMARK", "EIR-QA-009", disc, "Medium",
                    "Mark", key.mark, "Unique Mark per Category",
                    "Failed", "New",
                    $"Duplicate Mark '{key.mark}' shared by {dupList.Count} elements in category '{el.Category}'."));
            }
        }

        return new RuleEvaluationSummary(
            applicableChecks,
            evaluatedChecks,
            passedChecks,
            failedChecks,
            invalidValueChecks,
            notEvaluatedChecks,
            issues);
    }

    private static ExtractedParam? FindMatchingParameter(List<ExtractedParam>? pList, IdsRequirement req)
    {
        if (pList == null || pList.Count == 0) return null;

        foreach (var candidateName in req.BaseNames)
        {
            // 1. Exact match on Name + Scope + PropertySet (if specified)
            var exact = pList.FirstOrDefault(p =>
                p.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(p.DisplayValue) &&
                (string.IsNullOrEmpty(req.Scope) || p.Scope.Equals(req.Scope, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(req.PropertySet) || p.GroupName.Equals(req.PropertySet, StringComparison.OrdinalIgnoreCase)));
            if (exact != null) return exact;

            // 2. Match on Name + Scope (when PropertySet name varies by locale/group)
            var scopeMatch = pList.FirstOrDefault(p =>
                p.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(p.DisplayValue) &&
                (string.IsNullOrEmpty(req.Scope) || p.Scope.Equals(req.Scope, StringComparison.OrdinalIgnoreCase)));
            if (scopeMatch != null) return scopeMatch;
        }

        return null;
    }

    private static string BuildExpectedDescription(IdsRequirement req)
    {
        var parts = new List<string>();
        if (req.Cardinality.Equals("required", StringComparison.OrdinalIgnoreCase))
            parts.Add("Non-empty");
        if (req.MinInclusive.HasValue && req.MaxInclusive.HasValue)
            parts.Add($"[{req.MinInclusive.Value} .. {req.MaxInclusive.Value}]");
        else if (req.MinInclusive.HasValue)
            parts.Add($">= {req.MinInclusive.Value}");
        else if (req.MaxInclusive.HasValue)
            parts.Add($"<= {req.MaxInclusive.Value}");
        if (req.AllowedValues is { Count: > 0 })
            parts.Add($"One of ({string.Join("|", req.AllowedValues)})");
        if (!string.IsNullOrEmpty(req.Pattern))
            parts.Add($"Regex /{req.Pattern}/");

        return parts.Count > 0 ? string.Join(", ", parts) : "Valid value";
    }
}
