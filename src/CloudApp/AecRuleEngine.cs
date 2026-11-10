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
    string Message);

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

    public static List<IdsIssueRecord> Evaluate(
        List<IdsSpecification> specs,
        List<(int modelId, ExtractedElement elem)> elements,
        List<(int modelId, ExtractedParam param)> parameters)
    {
        var issues = new List<IdsIssueRecord>();

        var paramLookup = new Dictionary<(int mId, long elId), Dictionary<string, ExtractedParam>>();
        foreach (var (mId, pr) in parameters)
        {
            var key = (mId, pr.ElementId);
            if (!paramLookup.TryGetValue(key, out var dict))
            {
                dict = new Dictionary<string, ExtractedParam>(StringComparer.OrdinalIgnoreCase);
                paramLookup[key] = dict;
            }
            if (!dict.ContainsKey(pr.Name))
            {
                dict[pr.Name] = pr;
            }
        }

        // 1. Evaluate IDS Specifications on Instances
        foreach (var (mId, el) in elements)
        {
            if (el.Kind != "instance") continue;
            paramLookup.TryGetValue((mId, el.ElementId), out var pDict);

            foreach (var spec in specs)
            {
                if (spec.Categories.Count > 0 && !spec.Categories.Any(c => c.Equals(el.Category, StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreach (var req in spec.Requirements)
                {
                    if (req.BaseNames.Count == 0) continue;

                    ExtractedParam? matchedParam = null;
                    if (pDict != null)
                    {
                        foreach (var candidateName in req.BaseNames)
                        {
                            if (pDict.TryGetValue(candidateName, out var found) && !string.IsNullOrWhiteSpace(found.DisplayValue))
                            {
                                matchedParam = found;
                                break;
                            }
                        }
                    }

                    var primaryParamName = req.BaseNames[0];

                    if (matchedParam == null)
                    {
                        if (req.Cardinality.Equals("required", StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(new IdsIssueRecord(
                                mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                                primaryParamName, "(missing)",
                                string.IsNullOrEmpty(req.Instructions) ? $"Missing required parameter '{primaryParamName}'." : req.Instructions));
                        }
                        continue;
                    }

                    if (req.MinInclusive.HasValue &&
                        double.TryParse(matchedParam.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numVal))
                    {
                        if (numVal < req.MinInclusive.Value)
                        {
                            issues.Add(new IdsIssueRecord(
                                mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                                spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                                matchedParam.Name, matchedParam.DisplayValue,
                                $"Value '{matchedParam.DisplayValue}' is below minimum threshold ({req.MinInclusive.Value}). {req.Instructions}"));
                        }
                    }

                    if (req.AllowedValues is { Count: > 0 } &&
                        !req.AllowedValues.Any(a => a.Equals(matchedParam.DisplayValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add(new IdsIssueRecord(
                            mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                            spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                            matchedParam.Name, matchedParam.DisplayValue,
                            $"Value '{matchedParam.DisplayValue}' is not in allowed enumeration ({string.Join(", ", req.AllowedValues)})."));
                    }

                    if (!string.IsNullOrEmpty(req.Pattern) && !Regex.IsMatch(matchedParam.DisplayValue, req.Pattern))
                    {
                        issues.Add(new IdsIssueRecord(
                            mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                            spec.Id, spec.RequirementId, spec.Discipline, spec.Severity,
                            matchedParam.Name, matchedParam.DisplayValue,
                            $"Value '{matchedParam.DisplayValue}' does not match pattern '{req.Pattern}'."));
                    }
                }
            }
        }

        // 2. Built-in Duplicate Mark Check per (Model, Category, Mark)
        var markGroups = new Dictionary<(int mId, string cat, string mark), List<ExtractedElement>>();
        foreach (var (mId, el) in elements)
        {
            if (el.Kind != "instance") continue;
            if (!paramLookup.TryGetValue((mId, el.ElementId), out var pDict)) continue;
            if (!pDict.TryGetValue("Mark", out var markParam) || string.IsNullOrWhiteSpace(markParam.DisplayValue)) continue;

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
                issues.Add(new IdsIssueRecord(
                    key.mId, el.ElementId, el.UniqueId, el.Category, el.Family, el.TypeName,
                    "IDS-QA-DUPMARK", "EIR-QA-009", disc, "Medium",
                    "Mark", key.mark,
                    $"Duplicate Mark '{key.mark}' shared by {dupList.Count} elements in category '{el.Category}'."));
            }
        }

        return issues;
    }
}
