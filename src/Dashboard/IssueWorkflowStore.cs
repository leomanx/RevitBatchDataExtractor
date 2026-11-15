using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RBDE.Dashboard;

public static class IssueWorkflowStore
{
    public static void EnsureSchema(SqliteConnection db)
    {
        using var check = db.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='issue_registry'";
        if (check.ExecuteScalar() == null) throw new InvalidOperationException("Issue warehouse is not initialized.");
        foreach (var (name, sql) in new[] {
            ("assignee", "TEXT NOT NULL DEFAULT ''"), ("due_date", "TEXT NOT NULL DEFAULT ''"),
            ("comment", "TEXT NOT NULL DEFAULT ''"), ("reviewer_signoff", "TEXT NOT NULL DEFAULT ''"),
            ("reviewed_utc", "TEXT NOT NULL DEFAULT ''") })
        {
            using var exists = db.CreateCommand(); exists.CommandText = "SELECT 1 FROM pragma_table_info('issue_registry') WHERE name=$n";
            exists.Parameters.AddWithValue("$n",name);
            if (exists.ExecuteScalar() != null) continue;
            using var add = db.CreateCommand(); add.CommandText = $"ALTER TABLE issue_registry ADD COLUMN {name} {sql}"; add.ExecuteNonQuery();
        }
        using var init = db.CreateCommand();
        init.CommandText = """
            CREATE TABLE IF NOT EXISTS issue_workflow_events (
              event_id INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL,
              project_id TEXT NOT NULL, lineage_id TEXT NOT NULL, unique_id TEXT NOT NULL,
              rule_id TEXT NOT NULL, parameter_name TEXT NOT NULL, actor TEXT NOT NULL,
              field TEXT NOT NULL, old_value TEXT NOT NULL, new_value TEXT NOT NULL);
            """;
        init.ExecuteNonQuery();
    }

    public static Dictionary<string,string> Read(SqliteConnection db, string project, string lineage, string uniqueId, string rule, string parameter, SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction=transaction;
        cmd.CommandText = "SELECT status,assignee,due_date,comment,reviewer_signoff,reviewed_utc FROM issue_registry WHERE project_id=$p AND lineage_id=$l AND unique_id=$u AND rule_id=$r AND parameter_name=$n";
        Key(cmd,project,lineage,uniqueId,rule,parameter);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Issue not found in the warehouse.");
        return Enumerable.Range(0,reader.FieldCount).ToDictionary(reader.GetName,i=>reader.GetString(i));
    }

    public static Dictionary<string,string> Update(SqliteConnection db, string project, string lineage, string uniqueId, string rule, string parameter,
        string actor, string assignee, string dueDate, string comment, string signoff)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length>120 || assignee.Length>120 || comment.Length>4000 || signoff.Length>120)
            throw new ArgumentException("Actor is required; input exceeds maximum length.");
        if (dueDate.Length>0 && !DateOnly.TryParseExact(dueDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))
            throw new ArgumentException("Due date must be YYYY-MM-DD.");
        using var transaction = db.BeginTransaction();
        var old = Read(db,project,lineage,uniqueId,rule,parameter,transaction);
        if (signoff.Length>0 && old["status"] != "Closed") throw new ArgumentException("Only a fixed issue can be signed off after verification.");
        var next = new Dictionary<string,string> { ["assignee"]=assignee.Trim(), ["due_date"]=dueDate, ["comment"]=comment.Trim(), ["reviewer_signoff"]=signoff.Trim(), ["reviewed_utc"]=signoff.Length>0 ? DateTime.UtcNow.ToString("O") : "" };
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE issue_registry SET assignee=$a,due_date=$d,comment=$c,reviewer_signoff=$s,reviewed_utc=$t WHERE project_id=$p AND lineage_id=$l AND unique_id=$u AND rule_id=$r AND parameter_name=$n";
            Key(cmd,project,lineage,uniqueId,rule,parameter);
            cmd.Parameters.AddWithValue("$a",next["assignee"]);cmd.Parameters.AddWithValue("$d",next["due_date"]);
            cmd.Parameters.AddWithValue("$c",next["comment"]);cmd.Parameters.AddWithValue("$s",next["reviewer_signoff"]);cmd.Parameters.AddWithValue("$t",next["reviewed_utc"]);
            if (cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("Issue changed while saving.");
        }
        foreach (var field in next.Keys.Where(f=>old[f]!=next[f]))
        {
            using var audit = db.CreateCommand(); audit.Transaction=transaction;
            audit.CommandText="INSERT INTO issue_workflow_events(utc,project_id,lineage_id,unique_id,rule_id,parameter_name,actor,field,old_value,new_value) VALUES($utc,$p,$l,$u,$r,$n,$actor,$field,$old,$new)";
            Key(audit,project,lineage,uniqueId,rule,parameter);
            audit.Parameters.AddWithValue("$utc",DateTime.UtcNow.ToString("O"));audit.Parameters.AddWithValue("$actor",actor.Trim());
            audit.Parameters.AddWithValue("$field",field);audit.Parameters.AddWithValue("$old",old[field]);audit.Parameters.AddWithValue("$new",next[field]);audit.ExecuteNonQuery();
        }
        transaction.Commit(); return Read(db,project,lineage,uniqueId,rule,parameter);
    }
    private static void Key(SqliteCommand cmd,string project,string lineage,string uid,string rule,string parameter)
    {
        cmd.Parameters.AddWithValue("$p",project);cmd.Parameters.AddWithValue("$l",lineage);
        cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$r",rule);cmd.Parameters.AddWithValue("$n",parameter);
    }
}
