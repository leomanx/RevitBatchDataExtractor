using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RBDE.Dashboard;

public sealed record SpatialBoxInput(
    string UniqueId,
    long ElementId,
    string Category,
    string Family,
    string TypeName,
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ);

public static class SpatialDiffStore
{
    public static void EnsureSchema(SqliteConnection warehouseDb)
    {
        using var cmd = warehouseDb.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS spatial_snapshots (
              model_key TEXT NOT NULL,
              unique_id TEXT NOT NULL,
              element_id INTEGER NOT NULL,
              category TEXT NOT NULL DEFAULT '',
              family TEXT NOT NULL DEFAULT '',
              type_name TEXT NOT NULL DEFAULT '',
              cx REAL NOT NULL,
              cy REAL NOT NULL,
              cz REAL NOT NULL,
              dx REAL NOT NULL,
              dy REAL NOT NULL,
              dz REAL NOT NULL,
              recorded_utc TEXT NOT NULL,
              PRIMARY KEY (model_key, unique_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static object SyncAndComputeDiffs(
        SqliteConnection warehouseDb,
        SqliteConnection extractDb,
        int modelId,
        string modelKey,
        IReadOnlyList<SpatialBoxInput> boxes)
    {
        EnsureSchema(warehouseDb);

        var prev = new Dictionary<string, (double cx, double cy, double cz, double dx, double dy, double dz)>(StringComparer.OrdinalIgnoreCase);
        using (var q = warehouseDb.CreateCommand())
        {
            q.CommandText = "SELECT unique_id, cx, cy, cz, dx, dy, dz FROM spatial_snapshots WHERE model_key=$mk";
            q.Parameters.AddWithValue("$mk", modelKey);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                prev[r.GetString(0)] = (r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6));
            }
        }

        bool isBaseline = prev.Count == 0;
        var diffs = new List<(string changeType, long elementId, string uid, string cat, string fam, string typ, string pName, string oldV, string newV)>();
        const double Tol = 0.005; // 5mm tolerance in meters

        using (var wTrans = warehouseDb.BeginTransaction())
        {
            using var up = warehouseDb.CreateCommand();
            up.Transaction = wTrans;
            up.CommandText = """
                INSERT INTO spatial_snapshots(model_key, unique_id, element_id, category, family, type_name, cx, cy, cz, dx, dy, dz, recorded_utc)
                VALUES($mk, $uid, $el, $cat, $fam, $typ, $cx, $cy, $cz, $dx, $dy, $dz, $utc)
                ON CONFLICT(model_key, unique_id) DO UPDATE SET
                  element_id=excluded.element_id, category=excluded.category, family=excluded.family, type_name=excluded.type_name,
                  cx=excluded.cx, cy=excluded.cy, cz=excluded.cz, dx=excluded.dx, dy=excluded.dy, dz=excluded.dz, recorded_utc=excluded.recorded_utc
                """;
            var pMk = up.Parameters.Add("$mk", SqliteType.Text);
            var pUid = up.Parameters.Add("$uid", SqliteType.Text);
            var pEl = up.Parameters.Add("$el", SqliteType.Integer);
            var pCat = up.Parameters.Add("$cat", SqliteType.Text);
            var pFam = up.Parameters.Add("$fam", SqliteType.Text);
            var pTyp = up.Parameters.Add("$typ", SqliteType.Text);
            var pCx = up.Parameters.Add("$cx", SqliteType.Real);
            var pCy = up.Parameters.Add("$cy", SqliteType.Real);
            var pCz = up.Parameters.Add("$cz", SqliteType.Real);
            var pDx = up.Parameters.Add("$dx", SqliteType.Real);
            var pDy = up.Parameters.Add("$dy", SqliteType.Real);
            var pDz = up.Parameters.Add("$dz", SqliteType.Real);
            var pUtc = up.Parameters.Add("$utc", SqliteType.Text);
            var now = DateTime.UtcNow.ToString("O");

            foreach (var b in boxes)
            {
                if (string.IsNullOrWhiteSpace(b.UniqueId)) continue;
                var cx = (b.MinX + b.MaxX) * 0.5;
                var cy = (b.MinY + b.MaxY) * 0.5;
                var cz = (b.MinZ + b.MaxZ) * 0.5;
                var dx = Math.Max(0, b.MaxX - b.MinX);
                var dy = Math.Max(0, b.MaxY - b.MinY);
                var dz = Math.Max(0, b.MaxZ - b.MinZ);

                if (prev.TryGetValue(b.UniqueId, out var p))
                {
                    var ddx = cx - p.cx;
                    var ddy = cy - p.cy;
                    var ddz = cz - p.cz;
                    var dist = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                    if (dist > Tol)
                    {
                        diffs.Add((
                            "Moved",
                            b.ElementId,
                            b.UniqueId,
                            b.Category,
                            b.Family,
                            b.TypeName,
                            "Location (X,Y,Z)",
                            string.Create(CultureInfo.InvariantCulture, $"({p.cx:F2}, {p.cy:F2}, {p.cz:F2})"),
                            string.Create(CultureInfo.InvariantCulture, $"({cx:F2}, {cy:F2}, {cz:F2}) [Δ={dist:F2}m]")));
                    }
                    else if (Math.Abs(dx - p.dx) > Tol || Math.Abs(dy - p.dy) > Tol || Math.Abs(dz - p.dz) > Tol)
                    {
                        diffs.Add((
                            "GeometryChanged",
                            b.ElementId,
                            b.UniqueId,
                            b.Category,
                            b.Family,
                            b.TypeName,
                            "BBox Size (W×D×H)",
                            string.Create(CultureInfo.InvariantCulture, $"{p.dx:F2}×{p.dy:F2}×{p.dz:F2}m"),
                            string.Create(CultureInfo.InvariantCulture, $"{dx:F2}×{dy:F2}×{dz:F2}m")));
                    }
                }

                pMk.Value = modelKey;
                pUid.Value = b.UniqueId;
                pEl.Value = b.ElementId;
                pCat.Value = b.Category;
                pFam.Value = b.Family;
                pTyp.Value = b.TypeName;
                pCx.Value = cx;
                pCy.Value = cy;
                pCz.Value = cz;
                pDx.Value = dx;
                pDy.Value = dy;
                pDz.Value = dz;
                pUtc.Value = now;
                up.ExecuteNonQuery();
            }

            wTrans.Commit();
        }

        // Persist detected geometric diffs into extract.sqlite element_diffs if table exists
        using (var chk = extractDb.CreateCommand())
        {
            chk.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='element_diffs'";
            if (chk.ExecuteScalar() != null)
            {
                using var eTrans = extractDb.BeginTransaction();
                using (var del = extractDb.CreateCommand())
                {
                    del.Transaction = eTrans;
                    del.CommandText = "DELETE FROM element_diffs WHERE model_id=$m AND change_type IN ('Moved','GeometryChanged')";
                    del.Parameters.AddWithValue("$m", modelId);
                    del.ExecuteNonQuery();
                }

                using (var ins = extractDb.CreateCommand())
                {
                    ins.Transaction = eTrans;
                    ins.CommandText = """
                        INSERT INTO element_diffs(model_id, change_type, element_id, unique_id, category, family, type_name, parameter_name, old_value, new_value)
                        VALUES($m, $ct, $el, $uid, $cat, $fam, $typ, $pn, $ov, $nv)
                        """;
                    var pM = ins.Parameters.Add("$m", SqliteType.Integer);
                    var pCt = ins.Parameters.Add("$ct", SqliteType.Text);
                    var pEl = ins.Parameters.Add("$el", SqliteType.Integer);
                    var pUid = ins.Parameters.Add("$uid", SqliteType.Text);
                    var pCat = ins.Parameters.Add("$cat", SqliteType.Text);
                    var pFam = ins.Parameters.Add("$fam", SqliteType.Text);
                    var pTyp = ins.Parameters.Add("$typ", SqliteType.Text);
                    var pPn = ins.Parameters.Add("$pn", SqliteType.Text);
                    var pOv = ins.Parameters.Add("$ov", SqliteType.Text);
                    var pNv = ins.Parameters.Add("$nv", SqliteType.Text);

                    foreach (var d in diffs)
                    {
                        pM.Value = modelId;
                        pCt.Value = d.changeType;
                        pEl.Value = d.elementId;
                        pUid.Value = d.uid;
                        pCat.Value = d.cat;
                        pFam.Value = d.fam;
                        pTyp.Value = d.typ;
                        pPn.Value = d.pName;
                        pOv.Value = d.oldV;
                        pNv.Value = d.newV;
                        ins.ExecuteNonQuery();
                    }
                }
                eTrans.Commit();
            }
        }

        return new
        {
            checkedCount = boxes.Count,
            baselineCreated = isBaseline,
            movedCount = diffs.Count(d => d.changeType == "Moved"),
            resizedCount = diffs.Count(d => d.changeType == "GeometryChanged"),
            diffs = diffs.Take(50).Select(d => new
            {
                changeType = d.changeType,
                elementId = d.elementId,
                uniqueId = d.uid,
                category = d.cat,
                parameterName = d.pName,
                oldValue = d.oldV,
                newValue = d.newV
            })
        };
    }
}
