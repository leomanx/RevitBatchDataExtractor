using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BatchExtractor;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection db;
    public string DirectoryPath { get; }
    public Store(string directory)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "extract.sqlite") }.ToString());
        db.Open();
        Execute("""
            PRAGMA foreign_keys=ON;
            CREATE TABLE models (model_id INTEGER PRIMARY KEY, path TEXT NOT NULL, status TEXT NOT NULL,
                started_utc TEXT NOT NULL, finished_utc TEXT, element_count INTEGER DEFAULT 0);
            CREATE TABLE elements (model_id INTEGER NOT NULL REFERENCES models, element_id INTEGER NOT NULL,
                unique_id TEXT, kind TEXT, category_id TEXT, category TEXT, family TEXT, type_name TEXT,
                type_id INTEGER, name TEXT, PRIMARY KEY(model_id, element_id));
            CREATE TABLE parameters (model_id INTEGER NOT NULL, element_id INTEGER NOT NULL, scope TEXT,
                parameter_key TEXT, name TEXT, storage_type TEXT, has_value INTEGER, raw_value TEXT,
                display_value TEXT, unit_type_id TEXT,
                FOREIGN KEY(model_id,element_id) REFERENCES elements(model_id,element_id));
            CREATE TABLE errors (utc TEXT, model_id INTEGER, element_id INTEGER, stage TEXT, exception_type TEXT, message TEXT);
            CREATE INDEX ix_parameter_name ON parameters(name);
            CREATE INDEX ix_parameter_element ON parameters(model_id,element_id);
            """);
    }

    public void Execute(string sql, params object?[] values)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void StartModel(int id, string path) => Execute("INSERT INTO models(model_id,path,status,started_utc) VALUES($p0,$p1,'running',$p2)", id, path, DateTime.UtcNow.ToString("O"));
    public void FinishModel(int id, string status, int count) => Execute("UPDATE models SET status=$p1,finished_utc=$p2,element_count=$p3 WHERE model_id=$p0", id, status, DateTime.UtcNow.ToString("O"), count);
    public void BeginModel() => Execute("BEGIN TRANSACTION");
    public void CommitModel() => Execute("COMMIT");
    public void RollbackModel() => Execute("ROLLBACK");
    public void AddElement(int model, ElementRow e, IEnumerable<ParameterRow> parameters)
    {
        Execute("SAVEPOINT element_write");
        try
        {
            Execute("INSERT INTO elements VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", model, e.Id, e.UniqueId, e.Kind, e.CategoryId, e.Category, e.Family, e.Type, e.TypeId, e.Name);
            foreach (var p in parameters)
                Execute("INSERT INTO parameters VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", model, e.Id, p.Scope, p.Key, p.Name, p.StorageType, p.HasValue ? 1 : 0, p.RawValue, p.DisplayValue, p.UnitTypeId);
            Execute("RELEASE element_write");
        }
        catch { Execute("ROLLBACK TO element_write"); Execute("RELEASE element_write"); throw; }
    }
    public void Error(int? model, long? element, string stage, Exception error)
    {
        var utc = DateTime.UtcNow.ToString("O");
        // Durable sidecar survives rollback of a failed model transaction.
        File.AppendAllText(Path.Combine(DirectoryPath, "errors.jsonl"), System.Text.Json.JsonSerializer.Serialize(new { utc, model, element, stage, type = error.GetType().FullName, message = error.Message, detail = error.ToString() }) + Environment.NewLine);
        Execute("INSERT INTO errors VALUES($p0,$p1,$p2,$p3,$p4,$p5)", utc, model, element, stage, error.GetType().FullName, error.Message);
    }
    public void ReconcileErrors()
    {
        var path = Path.Combine(DirectoryPath, "errors.jsonl");
        if (!File.Exists(path)) return;
        Execute("BEGIN TRANSACTION");
        try
        {
            Execute("DELETE FROM errors");
            foreach (var line in File.ReadLines(path))
            {
                using var entry = System.Text.Json.JsonDocument.Parse(line);
                var r = entry.RootElement;
                object? Number(string key) => r.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null ? null : r.GetProperty(key).GetInt64();
                Execute("INSERT INTO errors VALUES($p0,$p1,$p2,$p3,$p4,$p5)", r.GetProperty("utc").GetString(), Number("model"), Number("element"), r.GetProperty("stage").GetString(), r.GetProperty("type").GetString(), r.GetProperty("message").GetString());
            }
            Execute("COMMIT");
        }
        catch { Execute("ROLLBACK"); throw; }
    }
    public void ExportCsv(bool excelSafe)
    {
        foreach (var table in new[] { "models", "elements", "parameters", "errors" })
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table;
            using var reader = command.ExecuteReader();
            using var writer = new StreamWriter(Path.Combine(DirectoryPath, table + ".csv"), false, new UTF8Encoding(true));
            writer.WriteLine(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => Csv(reader.GetName(i), false))));
            while (reader.Read()) writer.WriteLine(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => Csv(reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!, excelSafe))));
        }
    }
    public static string Csv(string value, bool safe)
    {
        var trimmed = value.TrimStart();
        if (safe && trimmed.Length > 0 && ("=+-@".Contains(trimmed[0]) || value[0] is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public void Dispose() => db.Dispose();
}
