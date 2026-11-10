using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length == 0 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: RBDE.Dashboard <extract.sqlite> [--no-browser]");
    return 1;
}

var database = Path.GetFullPath(args[0]);
var runDir = Path.GetDirectoryName(database)!;
var warehousePath = Path.Combine(Path.GetDirectoryName(runDir) ?? runDir, "warehouse.sqlite");

using var listener = new HttpListener();
int port = 0;
for (int attempt = 0; attempt < 20; attempt++)
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    listener.Prefixes.Clear();
    listener.Prefixes.Add($"http://localhost:{port}/");
    try { listener.Start(); break; } catch (HttpListenerException) when (attempt < 19) { }
}

var token = Guid.NewGuid().ToString("N");
var url = $"http://localhost:{port}/{token}/";
Console.WriteLine(url);
if (!args.Contains("--no-browser"))
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

var assembly = Assembly.GetExecutingAssembly();
using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(".index.html")))!;
using var resource = new StreamReader(stream);
var html = await resource.ReadToEndAsync();
var lastRequest = DateTime.UtcNow;

while (listener.IsListening)
{
    var pending = listener.GetContextAsync();
    while (!pending.IsCompleted && DateTime.UtcNow - lastRequest < TimeSpan.FromMinutes(30))
        await Task.WhenAny(pending, Task.Delay(10000));
    if (!pending.IsCompleted) break;

    var context = await pending;
    lastRequest = DateTime.UtcNow;
    try
    {
        if (context.Request.HttpMethod != "GET" || !context.Request.Url!.AbsolutePath.StartsWith('/' + token + '/', StringComparison.Ordinal))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            continue;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'";

        var route = context.Request.Url.AbsolutePath[('/' + token + '/').Length..];
        if (route == "")
        {
            await Send(html, "text/html; charset=utf-8");
        }
        else
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            db.Open();

            var q = context.Request.QueryString;

            List<Dictionary<string, object?>> Query(string sql, Dictionary<string, object>? parameters = null, SqliteConnection? targetConn = null)
            {
                using var cmd = (targetConn ?? db).CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 30;
                if (parameters != null)
                {
                    foreach (var p in parameters)
                        cmd.Parameters.AddWithValue(p.Key, p.Value);
                }
                using var reader = cmd.ExecuteReader();
                var rows = new List<Dictionary<string, object?>>();
                while (reader.Read())
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows;
            }

            bool TableExists(string tableName)
            {
                var r = Query("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$t", new() { ["$t"] = tableName });
                return r.Count > 0;
            }

            var values = new Dictionary<string, object>();
            var terms = new List<string> { "1=1" };

            foreach (var (key, column) in new[] { ("model", "e.model_id"), ("category", "e.category"), ("kind", "e.kind"), ("status", "m.status") })
            {
                if (!string.IsNullOrEmpty(q[key]))
                {
                    terms.Add(column + "=$" + key);
                    values["$" + key] = q[key]!;
                }
            }

            foreach (var (key, column) in new[] { ("family", "e.family"), ("type", "e.type_name"), ("search", "e.name || ' ' || e.unique_id || ' ' || e.element_id") })
            {
                if (!string.IsNullOrEmpty(q[key]))
                {
                    terms.Add($"instr(lower({column}),lower(${key}))>0");
                    values["$" + key] = q[key]!;
                }
            }

            if (!string.IsNullOrEmpty(q["level"]))
            {
                values["$level"] = q["level"]!;
                terms.Add("EXISTS(SELECT 1 FROM parameters pl WHERE pl.model_id=e.model_id AND pl.element_id=e.element_id AND pl.name IN ('Level','Base Constraint','Reference Level','Schedule Level') AND pl.display_value=$level)");
            }

            if (!string.IsNullOrEmpty(q["parameter"]))
            {
                values["$parameter"] = q["parameter"]!;
                var clause = "p.model_id=e.model_id AND p.element_id=e.element_id AND (lower(p.name)=lower($parameter) OR lower(p.parameter_key)=lower($parameter))";
                if (!string.IsNullOrEmpty(q["scope"]))
                {
                    clause += " AND p.scope=$scope";
                    values["$scope"] = q["scope"]!;
                }
                clause += " AND p.has_value=1 AND trim(p.display_value)<>''";
                if (q["missing"] != "1" && !string.IsNullOrEmpty(q["value"]))
                {
                    clause += " AND instr(lower(p.display_value),lower($value))>0";
                    values["$value"] = q["value"]!;
                }
                terms.Add((q["missing"] == "1" ? "NOT " : "") + "EXISTS(SELECT 1 FROM parameters p WHERE " + clause + ")");
            }

            var from = " FROM elements e JOIN models m USING(model_id) WHERE " + string.Join(" AND ", terms);

            object result = route switch
            {
                "api/overview" => BuildOverview(),
                "api/elements" => BuildElementsResponse(),
                "api/heatmap" => BuildHeatmapResponse(),
                "api/values" => BuildValuesDistributionResponse(),
                "api/issues" => BuildIssuesResponse(),
                "api/detail" => Query(
                    "SELECT scope,parameter_key,name,storage_type,has_value,raw_value,display_value,unit_type_id FROM parameters WHERE model_id=$model AND element_id=$element ORDER BY scope,name",
                    new() { ["$model"] = q["model"] ?? "", ["$element"] = q["element"] ?? "" }),
                "api/errors" => new
                {
                    total = Query("SELECT count(*) count FROM errors" + (string.IsNullOrEmpty(q["model"]) ? "" : " WHERE model_id=$model"), values),
                    rows = Query("SELECT r.*,m.path FROM errors r LEFT JOIN models m USING(model_id)" +
                        (string.IsNullOrEmpty(q["model"]) ? "" : " WHERE r.model_id=$model") +
                        " ORDER BY r.utc DESC LIMIT 100 OFFSET " +
                        (int.TryParse(q["page"], out var ep) ? Math.Clamp(ep, 0, 100000000) * 100L : 0), values)
                },
                _ => throw new ArgumentException("Unknown route")
            };

            object BuildOverview()
            {
                var priorityCols = TableExists("priority_columns")
                    ? Query("SELECT name FROM priority_columns ORDER BY col_order").Select(r => r["name"]?.ToString() ?? "").Where(s => s.Length > 0).ToList()
                    : new List<string> { "Level", "Base Constraint", "Type Mark", "Mark", "Comments", "Type Comments", "Area", "Volume", "Length", "Material", "Fire Rating", "Workset", "Phase Created" };

                var availParams = Query("SELECT name, count(DISTINCT model_id || ':' || element_id) elem_count FROM parameters WHERE has_value=1 AND trim(display_value)<>'' GROUP BY name ORDER BY elem_count DESC, name ASC LIMIT 250");
                var levels = Query("SELECT display_value AS level, count(*) count FROM parameters WHERE name IN ('Level','Base Constraint','Reference Level','Schedule Level') AND trim(display_value)<>'' GROUP BY display_value ORDER BY count DESC LIMIT 40");

                var hasIssues = TableExists("issues");
                var issueTotals = hasIssues
                    ? Query("SELECT discipline, severity, count(*) count FROM issues GROUP BY discipline, severity")
                    : [];

                var historyRuns = new List<Dictionary<string, object?>>();
                if (File.Exists(warehousePath))
                {
                    try
                    {
                        using var wConn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = warehousePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                        wConn.Open();
                        historyRuns = Query("SELECT * FROM runs ORDER BY started_utc DESC LIMIT 12", null, wConn);
                    }
                    catch { }
                }

                return new
                {
                    run = Path.GetFileName(runDir),
                    models = Query("SELECT m.*, (SELECT count(*) FROM errors r WHERE r.model_id=m.model_id) error_count FROM models m ORDER BY model_id"),
                    categories = Query("SELECT DISTINCT category FROM elements WHERE category IS NOT NULL AND category<>'' ORDER BY category"),
                    levels,
                    totals = Query("SELECT kind, count(*) count FROM elements GROUP BY kind"),
                    errors = Query("SELECT count(*) count FROM errors"),
                    issueSummary = issueTotals,
                    historyRuns,
                    priorityColumns = priorityCols,
                    availableParameters = availParams
                };
            }

            object BuildHeatmapResponse()
            {
                var cols = (q["columns"] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();

                var topCats = Query($"SELECT e.category, count(*) total {from} AND e.kind='instance' GROUP BY e.category ORDER BY total DESC LIMIT 12", values);
                if (topCats.Count == 0 || cols.Count == 0)
                    return new { categories = topCats, columns = cols, cells = Array.Empty<object>() };

                var catNames = topCats.Select(c => c["category"]?.ToString() ?? "").Where(s => s.Length > 0).ToList();
                var hParams = new Dictionary<string, object>(values);
                var catPl = new List<string>();
                for (int i = 0; i < catNames.Count; i++) { var k = $"$hcat{i}"; catPl.Add(k); hParams[k] = catNames[i]; }
                var colPl = new List<string>();
                for (int i = 0; i < cols.Count; i++) { var k = $"$hcol{i}"; colPl.Add(k); hParams[k] = cols[i]; }

                var sql = $@"
                    SELECT e.category, p.name, count(DISTINCT e.model_id || ':' || e.element_id) AS filled
                    FROM parameters p
                    JOIN elements e ON e.model_id=p.model_id AND e.element_id=p.element_id
                    JOIN models m ON m.model_id=e.model_id
                    WHERE {string.Join(" AND ", terms)}
                      AND e.kind='instance'
                      AND e.category IN ({string.Join(",", catPl)})
                      AND p.name IN ({string.Join(",", colPl)})
                      AND p.has_value=1 AND trim(p.display_value)<>''
                    GROUP BY e.category, p.name";

                var cells = Query(sql, hParams);
                return new { categories = topCats, columns = cols, cells };
            }

            object BuildValuesDistributionResponse()
            {
                var targetParam = q["targetParam"] ?? "Type Mark";
                var vParams = new Dictionary<string, object>(values) { ["$tparam"] = targetParam };

                var distSql = $@"
                    SELECT p.display_value AS val, count(DISTINCT e.model_id || ':' || e.element_id) AS count
                    FROM parameters p
                    JOIN elements e ON e.model_id=p.model_id AND e.element_id=p.element_id
                    JOIN models m ON m.model_id=e.model_id
                    WHERE {string.Join(" AND ", terms)}
                      AND lower(p.name)=lower($tparam)
                      AND p.has_value=1 AND trim(p.display_value)<>''
                    GROUP BY p.display_value
                    ORDER BY count DESC, val ASC
                    LIMIT 25";

                return new
                {
                    parameter = targetParam,
                    distribution = Query(distSql, vParams)
                };
            }

            object BuildIssuesResponse()
            {
                if (!TableExists("issues"))
                {
                    return new
                    {
                        total = 0,
                        byDiscipline = Array.Empty<object>(),
                        byRule = Array.Empty<object>(),
                        rows = Array.Empty<object>()
                    };
                }

                var iVals = new Dictionary<string, object>();
                var iTerms = new List<string> { "1=1" };
                if (!string.IsNullOrEmpty(q["discipline"])) { iTerms.Add("i.discipline=$disc"); iVals["$disc"] = q["discipline"]!; }
                if (!string.IsNullOrEmpty(q["severity"])) { iTerms.Add("i.severity=$sev"); iVals["$sev"] = q["severity"]!; }
                if (!string.IsNullOrEmpty(q["rule"])) { iTerms.Add("i.rule_id=$rule"); iVals["$rule"] = q["rule"]!; }
                if (!string.IsNullOrEmpty(q["model"])) { iTerms.Add("i.model_id=$model"); iVals["$model"] = q["model"]!; }

                var whereClause = " WHERE " + string.Join(" AND ", iTerms);
                int limit = int.TryParse(q["limit"], out var rl) ? Math.Clamp(rl, 1, 5000) : 100;
                long offset = int.TryParse(q["page"], out var ip) ? Math.Clamp(ip, 0, 1000000) * (long)limit : 0;

                var tot = Query("SELECT count(*) count FROM issues i" + whereClause, iVals);
                var byDisc = Query("SELECT discipline, severity, count(*) count FROM issues GROUP BY discipline, severity ORDER BY discipline, severity");
                var byRule = Query("SELECT rule_id, requirement_id, discipline, severity, parameter_name, count(*) count FROM issues GROUP BY rule_id, requirement_id, discipline, severity, parameter_name ORDER BY count DESC");
                var rows = Query($"SELECT i.*, m.path FROM issues i LEFT JOIN models m USING(model_id){whereClause} ORDER BY i.issue_id LIMIT {limit} OFFSET {offset}", iVals);

                return new
                {
                    total = tot[0]["count"],
                    byDiscipline = byDisc,
                    byRule,
                    rows
                };
            }

            object BuildElementsResponse()
            {
                var requestedCols = (q["columns"] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(35)
                    .ToList();

                int limit = int.TryParse(q["limit"], out var reqLimit) ? Math.Clamp(reqLimit, 1, 10000) : 50;
                long offset = int.TryParse(q["page"], out var page) ? Math.Clamp(page, 0, 100000000) * (long)limit : 0;

                var totalRows = Query("SELECT count(*) count" + from, values);
                var totalCount = Convert.ToInt64(totalRows[0]["count"] ?? 0L, CultureInfo.InvariantCulture);

                var catBreakdown = Query("SELECT e.category, count(*) count" + from + " GROUP BY e.category ORDER BY count DESC LIMIT 14", values);

                var rows = Query(
                    $"SELECT e.*, m.path, m.status{from} ORDER BY e.model_id, e.element_id LIMIT {limit} OFFSET {offset}",
                    values);

                if (rows.Count > 0 && requestedCols.Count > 0)
                {
                    var rowMap = new Dictionary<(long mId, long elId), Dictionary<string, string?>>();
                    var pairClauses = new List<string>();
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var mId = Convert.ToInt64(rows[i]["model_id"], CultureInfo.InvariantCulture);
                        var elId = Convert.ToInt64(rows[i]["element_id"], CultureInfo.InvariantCulture);
                        var pDict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        rowMap[(mId, elId)] = pDict;
                        rows[i]["params"] = pDict;
                        pairClauses.Add($"({mId},{elId})");
                    }

                    var colParams = new Dictionary<string, object>();
                    var colPlaceholders = new List<string>();
                    for (int c = 0; c < requestedCols.Count; c++)
                    {
                        var pKey = $"$col{c}";
                        colPlaceholders.Add(pKey);
                        colParams[pKey] = requestedCols[c];
                    }

                    var sqlParams = $"SELECT model_id, element_id, name, display_value FROM parameters WHERE (model_id, element_id) IN ({string.Join(",", pairClauses)}) AND name IN ({string.Join(",", colPlaceholders)})";
                    var paramRecords = Query(sqlParams, colParams);
                    foreach (var pr in paramRecords)
                    {
                        var mId = Convert.ToInt64(pr["model_id"], CultureInfo.InvariantCulture);
                        var elId = Convert.ToInt64(pr["element_id"], CultureInfo.InvariantCulture);
                        var pName = pr["name"]?.ToString() ?? "";
                        var pVal = pr["display_value"]?.ToString();
                        if (rowMap.TryGetValue((mId, elId), out var dict) && !dict.ContainsKey(pName))
                        {
                            dict[pName] = pVal;
                        }
                    }
                }

                var completeness = new List<object>();
                var qtoTotals = new List<Dictionary<string, object?>>();

                if (limit <= 100)
                {
                    var colsForStats = requestedCols.Take(10).ToList();
                    if (colsForStats.Count > 0 && totalCount > 0)
                    {
                        var statParams = new Dictionary<string, object>(values);
                        var statPlaceholders = new List<string>();
                        for (int c = 0; c < colsForStats.Count; c++)
                        {
                            var pKey = $"$scol{c}";
                            statPlaceholders.Add(pKey);
                            statParams[pKey] = colsForStats[c];
                        }

                        var statSql = $@"
                            SELECT p.name, count(DISTINCT p.model_id || ':' || p.element_id) AS filled
                            FROM parameters p
                            JOIN elements e ON e.model_id=p.model_id AND e.element_id=p.element_id
                            JOIN models m ON m.model_id=e.model_id
                            WHERE {string.Join(" AND ", terms)}
                              AND p.name IN ({string.Join(",", statPlaceholders)})
                              AND p.has_value=1 AND trim(p.display_value)<>''
                            GROUP BY p.name";

                        var statRows = Query(statSql, statParams);
                        var statLookup = statRows.ToDictionary(
                            r => r["name"]?.ToString() ?? "",
                            r => Convert.ToInt64(r["filled"] ?? 0L, CultureInfo.InvariantCulture),
                            StringComparer.OrdinalIgnoreCase);

                        foreach (var colName in colsForStats)
                        {
                            var filled = statLookup.GetValueOrDefault(colName, 0L);
                            completeness.Add(new
                            {
                                name = colName,
                                filled,
                                total = totalCount,
                                pct = totalCount > 0 ? Math.Round(100.0 * filled / totalCount, 1) : 0.0
                            });
                        }
                    }

                    var qtoSql = $@"
                        SELECT p.name,
                               sum(CAST(p.raw_value AS REAL)) AS total_value,
                               max(p.unit_type_id) AS unit,
                               count(DISTINCT p.model_id || ':' || p.element_id) AS elem_count
                        FROM parameters p
                        JOIN elements e ON e.model_id=p.model_id AND e.element_id=p.element_id
                        JOIN models m ON m.model_id=e.model_id
                        WHERE {string.Join(" AND ", terms)}
                          AND p.name IN ('Area', 'Volume', 'Length')
                          AND p.storage_type IN ('Double', 'Integer')
                        GROUP BY p.name";
                    qtoTotals = Query(qtoSql, values);
                }

                return new
                {
                    total = totalRows,
                    categories = catBreakdown,
                    completeness,
                    qto = qtoTotals,
                    rows
                };
            }

            await Send(JsonSerializer.Serialize(result), "application/json; charset=utf-8");
        }

        async Task Send(string body, string type)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        }
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }));
        context.Response.ContentType = "application/json";
        try { await context.Response.OutputStream.WriteAsync(bytes); } catch (IOException) { }
    }
    finally
    {
        context.Response.Close();
    }
}

listener.Stop();
return 0;
