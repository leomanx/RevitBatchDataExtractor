using BatchExtractor;
using Microsoft.Data.Sqlite;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
var rows = new List<ParameterRow> {
    new("instance", "builtin:ALL_MODEL_MARK", "Mark", "String", true, "D-01", "D-01", ""),
    new("type", "guid:01234567-89ab-cdef-0123-456789abcdef", "Fire Rating", "String", true, "60", "60 min", ""),
    new("instance", "id:7", "Blank", "String", false, "", "", "")
};
Check(new Rule { Parameter = "Mark", Value = "d-", Operator = "startsWith" }.Matches(rows), "case-insensitive lookup");
Check(!new Rule { Parameter = "Mark", Scope = "type", Value = "D-01", Operator = "equals" }.Matches(rows), "scope isolation");
Check(new Rule { Parameter = "builtin:ALL_MODEL_MARK", Value = "D-01", Operator = "equals" }.Matches(rows), "stable built-in selector");
Check(new Rule { Parameter = rows[1].Key, Value = "60", ValueSource = "raw", Operator = "equals" }.Matches(rows), "GUID and raw values");
Check(new Rule { Parameter = "Fire Rating", Value = "^60\\s+min$", Operator = "regex" }.Matches(rows), "regex filter");
Check(new Rule { Parameter = "Blank", Operator = "missing" }.Matches(rows), "unset parameter treated as missing");
Check(!new Rule { Parameter = "Absent", Value = "x", Operator = "notEquals" }.Matches(rows), "missing does not match inequality");
var duplicates = rows.Concat(new[] { rows[0] with { Key = "id:88", RawValue = "D-02", DisplayValue = "D-02" } }).ToList();
Check(!new Rule { Parameter = "Mark", Value = "D-01", Operator = "notEquals" }.Matches(duplicates), "duplicate names inequality");
var config = new Config { Filters = [new() { Parameter = "Mark", Value = "D" }, new() { Parameter = "Absent", Operator = "exists" }] };
Check(!config.Matches(rows), "AND filter"); config.FilterMode = "any";
Check(config.Matches(rows), "OR filter");
try { new Config { FilterMode = "typo" }.Validate(); throw new Exception("Expected rejection"); } catch (ArgumentException) { passed++; }
Check(Store.Csv("a,\"b\"\r\nc", false) == "\"a,\"\"b\"\"\r\nc\"", "CSV quoting");
Check(Store.Csv(" =SUM(A1)", true).StartsWith("\"'"), "spreadsheet formula neutralization");
var directory = Path.GetFullPath(Path.Combine("test-output", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(directory);
var configPath = Path.Combine(directory, "config.json");
File.WriteAllText(configPath, "{\"models\":[\"a.rvt\",\"A.rvt\"],\"outputDirectory\":\"out\"}");
Check(Config.Load(configPath).Models.Count == 1 && Config.Load(configPath).OutputDirectory == Path.Combine(directory, "out"), "relative paths and duplicate models");
File.WriteAllText(configPath, "{\"paramters\":[]}");
try { Config.Load(configPath); throw new Exception("Expected rejection"); } catch (System.Text.Json.JsonException) { passed++; }
using (var store = new Store(directory))
{
    for (int m = 1; m <= 35; m++)
    {
        store.StartModel(m, $"model-{m}.rvt"); store.BeginModel();
        store.AddElement(m, new(1, "uid", "instance", "-1", "ประตู", "Family", "Type", 2, "a,\"b\"\nไทย"), rows);
        if (m == 17) { store.Error(m, 1, "parameter", new Exception("Error before rollback")); store.RollbackModel(); store.Error(m, null, "open", new IOException("Synthetic failure")); store.FinishModel(m, "failed", 0); }
        else { store.CommitModel(); store.FinishModel(m, "succeeded", 1); }
    }
    store.ReconcileErrors();
    store.ExportCsv(true);
}
using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "extract.sqlite") }.ToString()))
{
    db.Open();
    long Count(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!; }
    Check(Count("SELECT count(*) FROM models") == 35, "35-model export bookkeeping");
    Check(Count("SELECT count(*) FROM elements") == 34, "failed model rollback and continuation");
    Check(Count("SELECT count(*) FROM parameters") == 102, "normalized instance/type parameters");
    Check(Count("SELECT count(*) FROM errors") == 2, "SQLite error log survives model rollback");
    using var check = db.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
    Check((string)check.ExecuteScalar()! == "ok", "SQLite integrity");
}
Check(File.ReadAllText(Path.Combine(directory, "elements.csv")).Contains("ไทย"), "UTF-8 Thai export");
Check(File.ReadAllText(Path.Combine(directory, "errors.jsonl")).Contains("Synthetic failure"), "durable JSONL errors");
Console.WriteLine($"{passed} checks passed. Test artifacts: {directory}");
