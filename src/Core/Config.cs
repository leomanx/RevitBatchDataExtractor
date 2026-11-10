using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BatchExtractor;

public sealed class Config
{
    public List<string> Models { get; set; } = [];
    public string OutputDirectory { get; set; } = "output";
    public List<string> Categories { get; set; } = [];
    public List<string> Parameters { get; set; } = [];
    public List<Rule> Filters { get; set; } = [];
    public string FilterMode { get; set; } = "all";
    public bool IncludeTypes { get; set; } = true;
    public bool ExportCsv { get; set; } = true;
    public bool ExportSqlite { get; set; } = true;
    public bool CsvSafeForExcel { get; set; } = true;
    public bool EnableDashboard { get; set; } = true;

    public static Config Load(string path)
    {
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new ArgumentException("Empty configuration.");
        config.Validate();
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        config.OutputDirectory = Path.GetFullPath(config.OutputDirectory, root);
        config.Models = config.Models.Select(x => Path.GetFullPath(x, root)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return config;
    }

    public void Validate()
    {
        if (Models is null || Categories is null || Parameters is null || Filters is null)
            throw new ArgumentException("Lists must be arrays, not null.");
        if (Models.Concat(Categories).Concat(Parameters).Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("List entries cannot be blank.");
        if (string.IsNullOrWhiteSpace(OutputDirectory)) throw new ArgumentException("OutputDirectory is required.");
        if (!ExportCsv && !ExportSqlite) throw new ArgumentException("Enable at least one export format.");
        if (FilterMode is not ("all" or "any")) throw new ArgumentException("FilterMode must be all or any.");
        foreach (var filter in Filters) { if (filter is null) throw new ArgumentException("Null filter."); filter.Validate(); }
    }

    public bool Matches(IReadOnlyList<ParameterRow> parameters) => Filters.Count == 0 ||
        (FilterMode == "all" ? Filters.All(x => x.Matches(parameters)) : Filters.Any(x => x.Matches(parameters)));
}

public sealed class Rule
{
    public string Parameter { get; set; } = "";
    public string Scope { get; set; } = "any";
    public string Operator { get; set; } = "contains";
    public string Value { get; set; } = "";
    public string ValueSource { get; set; } = "display";
    public bool CaseSensitive { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Parameter)) throw new ArgumentException("Filter parameter is required.");
        if (Scope is not ("any" or "instance" or "type")) throw new ArgumentException("Invalid filter scope.");
        if (ValueSource is not ("display" or "raw")) throw new ArgumentException("ValueSource must be display or raw.");
        if (Operator is not ("equals" or "notEquals" or "contains" or "startsWith" or "regex" or "exists" or "missing"))
            throw new ArgumentException("Unknown filter operator: " + Operator);
        if (Value is null) throw new ArgumentException("Filter Value cannot be null.");
        if (Operator == "regex") _ = new Regex(Value, RegexOptions.None, TimeSpan.FromMilliseconds(200));
    }

    public bool Matches(IReadOnlyList<ParameterRow> rows)
    {
        var found = rows.Where(p => (Scope == "any" || p.Scope == Scope) && Selects(Parameter, p)).ToArray();
        if (Operator == "exists") return found.Any(p => p.HasValue);
        if (Operator == "missing") return !found.Any(p => p.HasValue);
        var values = found.Where(p => p.HasValue).Select(p => ValueSource == "raw" ? p.RawValue : p.DisplayValue);
        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Missing values do not match notEquals. With duplicate names, none may equal the excluded value.
        if (Operator == "notEquals") return values.Any() && values.All(v => !v.Equals(Value, comparison));
        return values.Any(v => Operator switch
        {
            "equals" => v.Equals(Value, comparison), "contains" => v.Contains(Value, comparison),
            "startsWith" => v.StartsWith(Value, comparison),
            "regex" => Regex.IsMatch(v, Value, CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
            _ => false
        });
    }

    public static bool Selects(string selector, ParameterRow p) =>
        selector.Equals(p.Name, StringComparison.OrdinalIgnoreCase) ||
        selector.Equals(p.Key, StringComparison.OrdinalIgnoreCase);
}

public sealed record ElementRow(long Id, string UniqueId, string Kind, string CategoryId, string Category,
    string Family, string Type, long TypeId, string Name);
public sealed record ParameterRow(string Scope, string Key, string Name, string StorageType,
    bool HasValue, string RawValue, string DisplayValue, string UnitTypeId);
