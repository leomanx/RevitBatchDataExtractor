using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RBDE.Dashboard;

public sealed class TelegramUserSession
{
    public long ChatId { get; set; }
    public string ActiveDatabasePath { get; set; } = "";
    public string ActiveRunName { get; set; } = "";
    public string ActiveProjectName { get; set; } = "";
    public int? ActiveModelId { get; set; }
    public string? ActiveCategory { get; set; }
    public string? ActiveLevel { get; set; }
    public string LastQueryType { get; set; } = "menu";
    public string LastQueryArg { get; set; } = "";
    public int LastPage { get; set; } = 0;
}

public sealed record BotExecutionResult(
    string HtmlText,
    string PlainPreview,
    List<List<(string Label, string CallbackData)>> Buttons,
    byte[]? AttachmentBytes = null,
    string? AttachmentFileName = null,
    string? AttachmentMime = null);

public sealed class TelegramBotEngine
{
    private readonly string defaultDatabasePath;
    private readonly string outputRootDir;
    private readonly string warehousePath;
    private readonly HttpClient httpClient;
    private readonly Func<Task<string>> getApsAccessTokenAsync;
    private readonly ConcurrentDictionary<long, TelegramUserSession> sessions = new();
    private readonly ConcurrentQueue<string> activityLogs = new();

    private CancellationTokenSource? pollingCts;
    private Task? pollingTask;
    private string? activeBotToken;
    private string? botUsername;
    private long lastUpdateId = 0;
    private int totalQueriesHandled = 0;

    private static readonly string CredFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MMK_BIM", "RBDE", "telegram.dpapi.json");

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("RBDE.Dashboard.TelegramToken.v1");

    public TelegramBotEngine(
        string defaultDatabasePath,
        string outputRootDir,
        string warehousePath,
        HttpClient httpClient,
        Func<Task<string>> getApsAccessTokenAsync)
    {
        this.defaultDatabasePath = defaultDatabasePath;
        this.outputRootDir = outputRootDir;
        this.warehousePath = warehousePath;
        this.httpClient = httpClient;
        this.getApsAccessTokenAsync = getApsAccessTokenAsync;

        TryAutoStartFromStoredToken();
    }

    public bool IsRunning => pollingTask != null && !pollingTask.IsCompleted && pollingCts is { IsCancellationRequested: false };
    public bool HasStoredToken => !string.IsNullOrWhiteSpace(LoadTokenFromDpapi());

    public object GetStatusSnapshot()
    {
        return new
        {
            isRunning = IsRunning,
            hasStoredToken = HasStoredToken,
            botUsername = botUsername ?? "",
            totalQueriesHandled,
            activeSessions = sessions.Count,
            logs = activityLogs.ToArray().Reverse().Take(40).ToArray()
        };
    }

    private void LogActivity(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        activityLogs.Enqueue(line);
        while (activityLogs.Count > 100) activityLogs.TryDequeue(out _);
    }

    private void TryAutoStartFromStoredToken()
    {
        var saved = LoadTokenFromDpapi();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            _ = StartPollingAsync(saved);
        }
    }

    public static string? LoadTokenFromDpapi()
    {
        if (OperatingSystem.IsWindows() && File.Exists(CredFilePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(CredFilePath));
                if (doc.RootElement.TryGetProperty("encryptedBotToken", out var enc) && !string.IsNullOrWhiteSpace(enc.GetString()))
                {
                    var cipher = Convert.FromBase64String(enc.GetString()!);
                    var plain = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch { }
        }
        return Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    }

    public static void SaveTokenToDpapi(string token)
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.GetDirectoryName(CredFilePath)!;
        Directory.CreateDirectory(dir);
        var plain = Encoding.UTF8.GetBytes(token.Trim());
        var cipher = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
        var json = JsonSerializer.Serialize(new
        {
            encryptedBotToken = Convert.ToBase64String(cipher),
            updatedUtc = DateTime.UtcNow.ToString("O")
        });
        File.WriteAllText(CredFilePath, json, Encoding.UTF8);
    }

    public async Task<object> StartPollingAsync(string? newToken = null)
    {
        await StopPollingAsync();

        var tokenToUse = !string.IsNullOrWhiteSpace(newToken) ? newToken.Trim() : LoadTokenFromDpapi();
        if (string.IsNullOrWhiteSpace(tokenToUse))
            throw new InvalidOperationException("Telegram Bot Token is empty. Create a bot via @BotFather and paste the token.");

        // Verify token via getMe
        var meUrl = $"https://api.telegram.org/bot{tokenToUse}/getMe";
        using var resp = await httpClient.GetAsync(meUrl);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Telegram API rejected token ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
            throw new InvalidOperationException("Telegram getMe returned ok=false.");

        var resEl = doc.RootElement.GetProperty("result");
        botUsername = resEl.TryGetProperty("username", out var un) ? "@" + un.GetString() : "TelegramBot";

        if (!string.IsNullOrWhiteSpace(newToken))
        {
            SaveTokenToDpapi(tokenToUse);
        }

        // Register slash commands menu in Telegram
        _ = RegisterBotCommandsMenuAsync(tokenToUse);

        activeBotToken = tokenToUse;
        pollingCts = new CancellationTokenSource();
        pollingTask = Task.Run(() => RunLongPollingLoopAsync(tokenToUse, pollingCts.Token));

        LogActivity($"Started Telegram Long-Polling Bot ({botUsername}) — Zero-AI Deterministic SQLite Engine.");
        return GetStatusSnapshot();
    }

    public async Task<object> StopPollingAsync()
    {
        if (pollingCts != null)
        {
            pollingCts.Cancel();
            try
            {
                if (pollingTask != null) await Task.WhenAny(pollingTask, Task.Delay(1500));
            }
            catch { }
            pollingCts.Dispose();
            pollingCts = null;
            pollingTask = null;
            LogActivity("Stopped Telegram Long-Polling listener.");
        }
        return GetStatusSnapshot();
    }

    private async Task RegisterBotCommandsMenuAsync(string token)
    {
        try
        {
            var payload = new
            {
                commands = new[]
                {
                    new { command = "menu", description = "Open Interactive Hub/Project/Category Menu" },
                    new { command = "projects", description = "Select Hub / Project / Extraction Run" },
                    new { command = "models", description = "Select Specific .RVT Model or Federated" },
                    new { command = "cats", description = "Browse Categories with Count & Area/Volume" },
                    new { command = "qto", description = "Quantity Take-Off (Area, Volume, Length, Count)" },
                    new { command = "rooms", description = "Room & Area Schedule (m2 by Level)" },
                    new { command = "find", description = "Search Elements (/find <keyword>)" },
                    new { command = "num", description = "Numeric Filter (/num Area > 50)" },
                    new { command = "issues", description = "QA/QC Issues by Team (/issues AR)" },
                    new { command = "sheets", description = "List 2D Sheets & Download Cloud Vector PDF" },
                    new { command = "status", description = "Project Delivery Readiness & Diff Summary" }
                }
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await httpClient.PostAsync($"https://api.telegram.org/bot{token}/setMyCommands", content);
        }
        catch { }
    }

    private async Task RunLongPollingLoopAsync(string token, CancellationToken ct)
    {
        using var pollHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{token}/getUpdates?offset={lastUpdateId + 1}&timeout=25&allowed_updates=%5B%22message%22,%22callback_query%22%5D";
                using var resp = await pollHttp.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    await Task.Delay(3000, ct);
                    continue;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var upd in arr.EnumerateArray())
                {
                    var updId = upd.GetProperty("update_id").GetInt64();
                    if (updId > lastUpdateId) lastUpdateId = updId;

                    if (upd.TryGetProperty("message", out var msg))
                    {
                        var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();
                        var text = msg.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "" : "";
                        var user = msg.TryGetProperty("from", out var fProp) && fProp.TryGetProperty("first_name", out var fn) ? fn.GetString() ?? "User" : "User";
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            Interlocked.Increment(ref totalQueriesHandled);
                            LogActivity($"Chat [{user}]: {text}");
                            var result = await ExecuteCommandOrCallbackAsync(chatId, text, isCallback: false);
                            await SendTelegramResultAsync(token, chatId, null, result, ct);
                        }
                    }
                    else if (upd.TryGetProperty("callback_query", out var cb))
                    {
                        var cbId = cb.GetProperty("id").GetString() ?? "";
                        var data = cb.TryGetProperty("data", out var dProp) ? dProp.GetString() ?? "" : "";
                        long chatId = 0;
                        long? msgId = null;
                        if (cb.TryGetProperty("message", out var cbMsg))
                        {
                            chatId = cbMsg.GetProperty("chat").GetProperty("id").GetInt64();
                            msgId = cbMsg.GetProperty("message_id").GetInt64();
                        }

                        if (chatId != 0 && !string.IsNullOrWhiteSpace(data))
                        {
                            Interlocked.Increment(ref totalQueriesHandled);
                            LogActivity($"Button Tap [{chatId}]: {data}");
                            _ = AnswerCallbackQueryAsync(token, cbId, ct);
                            var result = await ExecuteCommandOrCallbackAsync(chatId, data, isCallback: true);
                            await SendTelegramResultAsync(token, chatId, msgId, result, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogActivity($"Polling warning: {ex.Message}");
                await Task.Delay(3000, ct);
            }
        }
    }

    private async Task AnswerCallbackQueryAsync(string token, string callbackQueryId, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { callback_query_id = callbackQueryId });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await httpClient.PostAsync($"https://api.telegram.org/bot{token}/answerCallbackQuery", content, ct);
        }
        catch { }
    }

    private async Task SendTelegramResultAsync(string token, long chatId, long? editMessageId, BotExecutionResult res, CancellationToken ct)
    {
        try
        {
            if (res.AttachmentBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(res.AttachmentFileName))
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(chatId.ToString(CultureInfo.InvariantCulture)), "chat_id");
                form.Add(new StringContent(res.HtmlText), "caption");
                form.Add(new StringContent("HTML"), "parse_mode");
                var fileContent = new ByteArrayContent(res.AttachmentBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(res.AttachmentMime ?? "application/octet-stream");
                form.Add(fileContent, "document", res.AttachmentFileName);
                await httpClient.PostAsync($"https://api.telegram.org/bot{token}/sendDocument", form, ct);
                return;
            }

            var inlineKeyboard = res.Buttons.Select(row =>
                row.Select(b => new { text = b.Label, callback_data = b.CallbackData }).ToArray()
            ).ToArray();

            if (editMessageId.HasValue)
            {
                var editPayload = new
                {
                    chat_id = chatId,
                    message_id = editMessageId.Value,
                    text = res.HtmlText,
                    parse_mode = "HTML",
                    reply_markup = new { inline_keyboard = inlineKeyboard }
                };
                using var editContent = new StringContent(JsonSerializer.Serialize(editPayload), Encoding.UTF8, "application/json");
                using var editResp = await httpClient.PostAsync($"https://api.telegram.org/bot{token}/editMessageText", editContent, ct);
                if (editResp.IsSuccessStatusCode) return;
            }

            var sendPayload = new
            {
                chat_id = chatId,
                text = res.HtmlText,
                parse_mode = "HTML",
                reply_markup = new { inline_keyboard = inlineKeyboard }
            };
            using var sendContent = new StringContent(JsonSerializer.Serialize(sendPayload), Encoding.UTF8, "application/json");
            await httpClient.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", sendContent, ct);
        }
        catch (Exception ex)
        {
            LogActivity($"Send message error: {ex.Message}");
        }
    }

    private TelegramUserSession GetOrCreateSession(long chatId)
    {
        return sessions.GetOrAdd(chatId, id =>
        {
            var runName = Path.GetFileName(Path.GetDirectoryName(defaultDatabasePath) ?? "") ?? "Current Run";
            var projName = ResolveProjectNameForRun(runName) ?? "Active ACC Project";
            return new TelegramUserSession
            {
                ChatId = id,
                ActiveDatabasePath = defaultDatabasePath,
                ActiveRunName = runName,
                ActiveProjectName = projName
            };
        });
    }

    private string? ResolveProjectNameForRun(string runFolderName)
    {
        var runId = runFolderName.Replace("batch-", "", StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(warehousePath)) return null;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = warehousePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT project_name FROM runs WHERE run_id = $r ORDER BY started_utc DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$r", runId);
            return cmd.ExecuteScalar()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public async Task<BotExecutionResult> ExecuteCommandOrCallbackAsync(long chatId, string rawInput, bool isCallback)
    {
        var session = GetOrCreateSession(chatId);
        if (string.IsNullOrWhiteSpace(session.ActiveDatabasePath) || !File.Exists(session.ActiveDatabasePath))
        {
            session.ActiveDatabasePath = defaultDatabasePath;
        }

        var input = rawInput.Trim();

        // Handle Callback Data patterns: "act:arg1:arg2"
        if (isCallback || input.StartsWith("cb:", StringComparison.Ordinal))
        {
            var cleanCb = input.StartsWith("cb:", StringComparison.Ordinal) ? input[3..] : input;
            var parts = cleanCb.Split(':', 3);
            var action = parts[0];
            var arg1 = parts.Length > 1 ? parts[1] : "";
            var arg2 = parts.Length > 2 ? parts[2] : "";

            switch (action)
            {
                case "menu":
                    return BuildMainMenu(session);
                case "projects":
                    return BuildProjectsSelector(session);
                case "setrun":
                    SwitchActiveRun(session, arg1);
                    return BuildMainMenu(session, $"Switched active project/run to: {session.ActiveProjectName} ({session.ActiveRunName})");
                case "models":
                    return BuildModelsSelector(session);
                case "setmodel":
                    session.ActiveModelId = arg1 == "all" || !int.TryParse(arg1, out var mid) ? null : mid;
                    return BuildMainMenu(session, session.ActiveModelId.HasValue ? $"Filtered to Model #{session.ActiveModelId}" : "Scope set to All Models (Federated)");
                case "levels":
                    return BuildLevelsSelector(session);
                case "setlevel":
                    session.ActiveLevel = arg1 == "all" ? null : arg1;
                    return BuildMainMenu(session, session.ActiveLevel != null ? $"Filtered to Level: {session.ActiveLevel}" : "Cleared Level filter (All Levels)");
                case "cats":
                    return BuildCategoriesMenu(session, int.TryParse(arg1, out var cp) ? cp : 0);
                case "setcat":
                    session.ActiveCategory = arg1 == "all" ? null : arg1;
                    if (session.ActiveCategory == null)
                        return BuildMainMenu(session, "Cleared Category filter (All Categories)");
                    return BuildCategoryQtoDigest(session, session.ActiveCategory, 0);
                case "catpage":
                    return BuildCategoryQtoDigest(session, arg1, int.TryParse(arg2, out var cpg) ? cpg : 0);
                case "qto":
                    return BuildQtoOverview(session, string.IsNullOrWhiteSpace(arg1) ? session.ActiveCategory : arg1);
                case "rooms":
                    return BuildRoomsSchedule(session, arg1, int.TryParse(arg2, out var rpg) ? rpg : 0);
                case "status":
                    return BuildProjectStatusCard(session);
                case "issues":
                    return BuildIssuesCard(session, arg1, int.TryParse(arg2, out var ipg) ? ipg : 0);
                case "diff":
                    return BuildDiffsCard(session, arg1);
                case "sheets":
                    return await BuildSheetsCardAsync(session, int.TryParse(arg1, out var spg) ? spg : 0);
                case "sendpdf":
                    return await DownloadAndSendSheetPdfAsync(session, int.TryParse(arg1, out var sIdx) ? sIdx : 0);
                case "findpage":
                    return BuildSearchDigest(session, arg1, int.TryParse(arg2, out var fpg) ? fpg : 0);
                case "numpage":
                    return BuildNumericFilterDigest(session, arg1, int.TryParse(arg2, out var npg) ? npg : 0);
                case "id":
                    return BuildElementDetailCard(session, arg1);
                case "csv":
                    return BuildCsvAttachment(session, arg1, arg2);
                case "reset":
                    session.ActiveModelId = null;
                    session.ActiveCategory = null;
                    session.ActiveLevel = null;
                    return BuildMainMenu(session, "Reset all Model, Level, and Category filters.");
            }
        }

        // Handle Slash Commands or Natural Keyword Search
        string cmd = input;
        string arg = "";
        if (input.StartsWith('/'))
        {
            var sp = input.IndexOf(' ');
            if (sp > 0)
            {
                cmd = input[..sp].ToLowerInvariant();
                arg = input[(sp + 1)..].Trim();
            }
            else
            {
                cmd = input.ToLowerInvariant();
            }
            // Strip @botusername if present
            var atIdx = cmd.IndexOf('@');
            if (atIdx > 0) cmd = cmd[..atIdx];
        }
        else
        {
            // If user types a plain keyword or number without slash:
            // - Pure integer -> Element ID lookup
            // - Comparison like "Area > 50" -> Numeric query
            // - Otherwise -> /find <keyword>
            if (long.TryParse(input, out _))
            {
                cmd = "/id";
                arg = input;
            }
            else if (Regex.IsMatch(input, @"^.+?(>=|<=|!=|>|<|=)\s*-?\d+(\.\d+)?"))
            {
                cmd = "/num";
                arg = input;
            }
            else
            {
                cmd = "/find";
                arg = input;
            }
        }

        return cmd switch
        {
            "/start" or "/menu" or "/help" => BuildMainMenu(session),
            "/projects" or "/hubs" => BuildProjectsSelector(session),
            "/models" => BuildModelsSelector(session),
            "/levels" => BuildLevelsSelector(session),
            "/cats" or "/categories" => BuildCategoriesMenu(session, 0),
            "/qto" or "/area" => BuildQtoOverview(session, string.IsNullOrWhiteSpace(arg) ? session.ActiveCategory : arg),
            "/sum" => BuildParameterSumCard(session, string.IsNullOrWhiteSpace(arg) ? "Area" : arg),
            "/rooms" or "/spaces" => BuildRoomsSchedule(session, arg, 0),
            "/num" or "/filter" => BuildNumericFilterDigest(session, string.IsNullOrWhiteSpace(arg) ? "Area > 20" : arg, 0),
            "/find" or "/search" => string.IsNullOrWhiteSpace(arg)
                ? BuildCategoriesMenu(session, 0)
                : BuildSearchDigest(session, arg, 0),
            "/id" or "/el" => BuildElementDetailCard(session, arg),
            "/status" or "/summary" => BuildProjectStatusCard(session),
            "/issues" or "/qa" => BuildIssuesCard(session, arg, 0),
            "/diff" or "/changes" => BuildDiffsCard(session, arg),
            "/sheets" => await BuildSheetsCardAsync(session, 0),
            "/pdf" => await DownloadAndSendSheetPdfByNameAsync(session, arg),
            "/csv" => BuildCsvAttachment(session, "search", arg),
            "/reset" => ExecuteResetAndMenu(session),
            _ => BuildSearchDigest(session, input.TrimStart('/'), 0)
        };
    }

    private BotExecutionResult ExecuteResetAndMenu(TelegramUserSession session)
    {
        session.ActiveModelId = null;
        session.ActiveCategory = null;
        session.ActiveLevel = null;
        return BuildMainMenu(session, "All filters reset.");
    }

    private void SwitchActiveRun(TelegramUserSession session, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return;
        var candidateDir = Path.Combine(outputRootDir, Path.GetFileName(folderName));
        var dbCandidate = Path.Combine(candidateDir, "extract.sqlite");
        if (File.Exists(dbCandidate))
        {
            session.ActiveDatabasePath = dbCandidate;
            session.ActiveRunName = Path.GetFileName(candidateDir);
            session.ActiveProjectName = ResolveProjectNameForRun(session.ActiveRunName) ?? session.ActiveRunName;
            session.ActiveModelId = null;
            session.ActiveCategory = null;
            session.ActiveLevel = null;
        }
    }

    private SqliteConnection OpenSessionDb(TelegramUserSession session)
    {
        var dbPath = File.Exists(session.ActiveDatabasePath) ? session.ActiveDatabasePath : defaultDatabasePath;
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();
        return conn;
    }

    private static List<Dictionary<string, object?>> QueryDb(SqliteConnection conn, string sql, Dictionary<string, object>? parameters = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 20;
        if (parameters != null)
        {
            foreach (var kv in parameters)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value);
        }
        using var reader = cmd.ExecuteReader();
        var list = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        var r = QueryDb(conn, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$t", new() { ["$t"] = tableName });
        return r.Count > 0;
    }

    private (string WhereSql, Dictionary<string, object> Params, string ScopeBanner) BuildScopeFilter(TelegramUserSession session, SqliteConnection conn, string? overrideCategory = null)
    {
        var terms = new List<string> { "1=1" };
        var p = new Dictionary<string, object>();
        string modelLabel = "All Models (Federated)";

        if (session.ActiveModelId.HasValue)
        {
            terms.Add("e.model_id = $mid");
            p["$mid"] = session.ActiveModelId.Value;
            var mRow = QueryDb(conn, "SELECT path FROM models WHERE model_id = $mid", new() { ["$mid"] = session.ActiveModelId.Value }).FirstOrDefault();
            if (mRow != null) modelLabel = $"#{session.ActiveModelId}: {mRow["path"]}";
        }

        var cat = overrideCategory ?? session.ActiveCategory;
        if (!string.IsNullOrWhiteSpace(cat))
        {
            terms.Add("lower(e.category) = lower($cat)");
            p["$cat"] = cat;
        }

        if (!string.IsNullOrWhiteSpace(session.ActiveLevel))
        {
            terms.Add("EXISTS(SELECT 1 FROM parameters pl WHERE pl.model_id=e.model_id AND pl.element_id=e.element_id AND pl.name IN ('Level','Base Constraint','Reference Level','Schedule Level') AND pl.display_value=$lvl)");
            p["$lvl"] = session.ActiveLevel;
        }

        var banner = $"🏢 <b>Project:</b> {H(session.ActiveProjectName)} <i>({H(session.ActiveRunName)})</i>\n" +
                     $"📦 <b>Model:</b> {H(modelLabel)}" +
                     (session.ActiveLevel != null ? $" | 🏢 <b>Level:</b> {H(session.ActiveLevel)}" : "") +
                     (cat != null ? $" | 🧱 <b>Cat:</b> {H(cat)}" : "");

        return (string.Join(" AND ", terms), p, banner);
    }

    // =========================================================================
    // 1. MAIN MENU & HIERARCHY NAVIGATION (Hub/Project -> Model -> Level -> Cat)
    // =========================================================================
    private BotExecutionResult BuildMainMenu(TelegramUserSession session, string? notice = null)
    {
        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn);

        var totInstances = Convert.ToInt64(QueryDb(conn, $"SELECT count(*) c FROM elements e WHERE {whereSql} AND e.kind='instance'", sqlParams)[0]["c"] ?? 0L, CultureInfo.InvariantCulture);
        var totTypes = Convert.ToInt64(QueryDb(conn, $"SELECT count(*) c FROM elements e WHERE {whereSql} AND e.kind='type'", sqlParams)[0]["c"] ?? 0L, CultureInfo.InvariantCulture);
        var modelCount = Convert.ToInt64(QueryDb(conn, "SELECT count(*) c FROM models")[0]["c"] ?? 0L, CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine("🧭 <b>RBDE Deterministic BIM & QTO Query Bot</b>");
        sb.AppendLine("<i>Zero-AI • Direct SQLite & Cloud Manifest • Instant Response</i>");
        sb.AppendLine("────────────────────────");
        if (!string.IsNullOrWhiteSpace(notice))
        {
            sb.AppendLine($"✅ <b>{H(notice)}</b>\n");
        }
        sb.AppendLine(banner);
        sb.AppendLine($"📊 <b>Scope Elements:</b> <code>{totInstances:N0}</code> instances | <code>{totTypes:N0}</code> types ({modelCount} models)");
        sb.AppendLine("────────────────────────");
        sb.AppendLine("<b>Quick Commands (or tap buttons below):</b>");
        sb.AppendLine("• <code>/projects</code> — Select Hub / Project / Run");
        sb.AppendLine("• <code>/models</code> | <code>/levels</code> | <code>/cats</code> — Filter Scope");
        sb.AppendLine("• <code>/qto [Category]</code> — Area, Volume, Length & Counts");
        sb.AppendLine("• <code>/rooms [Level]</code> — Room & Floor Area Schedule (m²)");
        sb.AppendLine("• <code>/num Area &gt; 50</code> — Numeric Parameter Filter");
        sb.AppendLine("• <code>/find &lt;keyword&gt;</code> — Search Family / Type / Mark");
        sb.AppendLine("• <code>/id &lt;ElementId&gt;</code> — Full Element Parameter Card");
        sb.AppendLine("• <code>/sheets</code> | <code>/pdf &lt;Sheet&gt;</code> — 2D Sheets & Vector PDF");

        var buttons = new List<List<(string, string)>>
        {
            new() { ("📁 Switch Project / Run", "projects"), ("🏗 Select Model", "models") },
            new() { ("🏢 Filter Level", "levels"), ("🧱 Browse Categories", "cats:0") },
            new() { ("📐 QTO & Area Summary", "qto:"), ("🚪 Room Area Schedule", "rooms::0") },
            new() { ("⚠️ QA/QC Issues", "issues::0"), ("🔄 Model Diffs", "diff:") },
            new() { ("📐 2D Sheets & Cloud PDF", "sheets:0"), ("📈 Readiness Status", "status") }
        };
        if (session.ActiveModelId != null || session.ActiveCategory != null || session.ActiveLevel != null)
        {
            buttons.Add(new() { ("🧹 Clear Active Filters", "reset") });
        }

        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildProjectsSelector(TelegramUserSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📁 <b>Select Hub / Project / Extraction Run</b>");
        sb.AppendLine($"Current: <b>{H(session.ActiveProjectName)}</b> (<code>{H(session.ActiveRunName)}</code>)");
        sb.AppendLine("────────────────────────");

        var buttons = new List<List<(string, string)>>();
        var runDirs = Directory.Exists(outputRootDir)
            ? Directory.GetDirectories(outputRootDir, "batch-*")
                .Concat(Directory.GetDirectories(outputRootDir, "derivative-*"))
                .OrderByDescending(Directory.GetCreationTimeUtc)
                .Take(8)
                .ToList()
            : [];

        foreach (var dir in runDirs)
        {
            var folder = Path.GetFileName(dir);
            var dbFile = Path.Combine(dir, "extract.sqlite");
            if (!File.Exists(dbFile)) continue;

            var projName = ResolveProjectNameForRun(folder) ?? "ACC Project";
            long elCount = 0;
            try
            {
                using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                c.Open();
                elCount = Convert.ToInt64(QueryDb(c, "SELECT count(*) c FROM elements WHERE kind='instance'")[0]["c"] ?? 0L, CultureInfo.InvariantCulture);
            }
            catch { }

            var isCurrent = string.Equals(folder, session.ActiveRunName, StringComparison.OrdinalIgnoreCase);
            var mark = isCurrent ? "🟢 " : "⚪ ";
            sb.AppendLine($"{mark}<b>{H(projName)}</b> — <code>{H(folder)}</code> ({elCount:N0} el)");
            buttons.Add(new() { ($"{mark}{projName} ({folder})", $"setrun:{folder}") });
        }

        buttons.Add(new() { ("⬅ Back to Main Menu", "menu") });
        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildModelsSelector(TelegramUserSession session)
    {
        using var conn = OpenSessionDb(session);
        var models = QueryDb(conn, "SELECT model_id, path, version_number, element_count, status FROM models ORDER BY model_id");

        var sb = new StringBuilder();
        sb.AppendLine("🏗 <b>Select Model Scope</b>");
        sb.AppendLine($"Project: <b>{H(session.ActiveProjectName)}</b>");
        sb.AppendLine("────────────────────────");

        var buttons = new List<List<(string, string)>>
        {
            new() { (session.ActiveModelId == null ? "🟢 All Models (Federated)" : "⚪ All Models (Federated)", "setmodel:all") }
        };

        foreach (var m in models)
        {
            var mid = Convert.ToInt32(m["model_id"], CultureInfo.InvariantCulture);
            var name = m["path"]?.ToString() ?? $"Model {mid}";
            var vnum = m["version_number"]?.ToString() ?? "1";
            var cnt = Convert.ToInt64(m["element_count"] ?? 0L, CultureInfo.InvariantCulture);
            var active = session.ActiveModelId == mid;
            var icon = active ? "🟢" : "🏗";

            sb.AppendLine($"{icon} <b>[{mid}] {H(name)}</b> (v{H(vnum)}) — <code>{cnt:N0}</code> elements");
            buttons.Add(new() { ($"{icon} [{mid}] {Trunc(name, 28)} ({cnt:N0})", $"setmodel:{mid}") });
        }

        buttons.Add(new() { ("⬅ Back to Main Menu", "menu") });
        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildLevelsSelector(TelegramUserSession session)
    {
        using var conn = OpenSessionDb(session);
        var mFilter = session.ActiveModelId.HasValue ? $"AND model_id = {session.ActiveModelId.Value}" : "";
        var levels = QueryDb(conn, $@"
            SELECT display_value AS level, count(DISTINCT model_id || ':' || element_id) AS cnt
            FROM parameters
            WHERE name IN ('Level','Base Constraint','Reference Level','Schedule Level')
              AND trim(display_value) <> '' {mFilter}
            GROUP BY display_value
            ORDER BY cnt DESC
            LIMIT 14");

        var sb = new StringBuilder();
        sb.AppendLine("🏢 <b>Select Floor Level Filter</b>");
        sb.AppendLine($"Current Level: <b>{H(session.ActiveLevel ?? "All Levels")}</b>");
        sb.AppendLine("────────────────────────");

        var buttons = new List<List<(string, string)>>
        {
            new() { (session.ActiveLevel == null ? "🟢 All Floor Levels" : "⚪ All Floor Levels", "setlevel:all") }
        };

        var row = new List<(string, string)>();
        foreach (var lv in levels)
        {
            var lvName = lv["level"]?.ToString() ?? "";
            var cnt = Convert.ToInt64(lv["cnt"] ?? 0L, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(lvName)) continue;

            var active = string.Equals(session.ActiveLevel, lvName, StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"{(active ? "🟢" : "•")} <b>{H(lvName)}</b>: <code>{cnt:N0}</code> elements");

            var safeCb = lvName.Length > 24 ? lvName[..24] : lvName;
            row.Add(($"{(active ? "🟢 " : "")}{Trunc(lvName, 16)} ({cnt:N0})", $"setlevel:{safeCb}"));
            if (row.Count == 2)
            {
                buttons.Add(row);
                row = new List<(string, string)>();
            }
        }
        if (row.Count > 0) buttons.Add(row);
        buttons.Add(new() { ("⬅ Back to Main Menu", "menu") });

        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildCategoriesMenu(TelegramUserSession session, int page)
    {
        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn, overrideCategory: null);
        // Remove category filter when listing categories
        var termsNoCat = whereSql.Replace("AND lower(e.category) = lower($cat)", "");

        var cats = QueryDb(conn, $@"
            SELECT e.category, count(*) AS cnt
            FROM elements e
            WHERE {termsNoCat} AND e.kind='instance' AND e.category IS NOT NULL AND e.category <> ''
            GROUP BY e.category
            ORDER BY cnt DESC", sqlParams);

        int pageSize = 10;
        int totalPages = Math.Max(1, (int)Math.Ceiling(cats.Count / (double)pageSize));
        page = Math.Clamp(page, 0, totalPages - 1);
        var slice = cats.Skip(page * pageSize).Take(pageSize).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("🧱 <b>Browse Categories (Tap to view Count, Area, Volume & Items)</b>");
        sb.AppendLine(banner);
        sb.AppendLine($"Showing Page <b>{page + 1}/{totalPages}</b> ({cats.Count} categories total)");
        sb.AppendLine("────────────────────────");

        var buttons = new List<List<(string, string)>>();
        var row = new List<(string, string)>();

        foreach (var c in slice)
        {
            var cName = c["category"]?.ToString() ?? "";
            var cnt = Convert.ToInt64(c["cnt"] ?? 0L, CultureInfo.InvariantCulture);
            sb.AppendLine($"• <b>{H(cName)}</b> — <code>{cnt:N0}</code> instances");

            row.Add(($"{Trunc(cName, 18)} ({cnt:N0})", $"setcat:{cName}"));
            if (row.Count == 2)
            {
                buttons.Add(row);
                row = new List<(string, string)>();
            }
        }
        if (row.Count > 0) buttons.Add(row);

        var navRow = new List<(string, string)>();
        if (page > 0) navRow.Add(("◀ Prev Cats", $"cats:{page - 1}"));
        if (page + 1 < totalPages) navRow.Add(("Next Cats ▶", $"cats:{page + 1}"));
        if (navRow.Count > 0) buttons.Add(navRow);

        buttons.Add(new() { ("📐 Full QTO Summary", "qto:"), ("⬅ Main Menu", "menu") });
        return ToResult(sb.ToString(), buttons);
    }

    // =========================================================================
    // 2. CATEGORY DIGEST + AUTOMATIC QUANTITY TAKE-OFF (Area / Volume / Length)
    // =========================================================================
    private BotExecutionResult BuildCategoryQtoDigest(TelegramUserSession session, string category, int page)
    {
        using var conn = OpenSessionDb(session);
        session.ActiveCategory = category;
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn, category);

        var totalCount = Convert.ToInt64(QueryDb(conn, $"SELECT count(*) c FROM elements e WHERE {whereSql} AND e.kind='instance'", sqlParams)[0]["c"] ?? 0L, CultureInfo.InvariantCulture);

        // 1. Numeric Aggregations (Area, Volume, Length, Perimeter, Width, Height, Cost)
        var qtoRows = QueryDb(conn, $@"
            SELECT p.name,
                   count(DISTINCT e.model_id || ':' || e.element_id) AS elem_cnt,
                   sum(CAST(p.raw_value AS REAL)) AS sum_val,
                   avg(CAST(p.raw_value AS REAL)) AS avg_val,
                   min(CAST(p.raw_value AS REAL)) AS min_val,
                   max(CAST(p.raw_value AS REAL)) AS max_val,
                   max(p.unit_type_id) AS unit
            FROM parameters p
            JOIN elements e ON e.model_id=p.model_id AND e.element_id=p.element_id
            WHERE {whereSql}
              AND e.kind='instance'
              AND p.name IN ('Area','Surface Area','Gross Floor Area','Volume','Length','Perimeter','Unconnected Height','Height','Width','Thickness','Cost')
              AND p.storage_type IN ('Double','Integer')
              AND CAST(p.raw_value AS REAL) > 0
            GROUP BY p.name
            ORDER BY elem_cnt DESC", sqlParams);

        // 2. Top 5 Family & Types Breakdown with Area/Length sum
        var topTypes = QueryDb(conn, $@"
            SELECT e.family, e.type_name, count(*) AS cnt,
                   (SELECT sum(CAST(pa.raw_value AS REAL)) FROM parameters pa WHERE pa.model_id=e.model_id AND pa.element_id=e.element_id AND pa.name='Area' AND pa.storage_type IN ('Double','Integer')) AS area_sum
            FROM elements e
            WHERE {whereSql} AND e.kind='instance'
            GROUP BY e.family, e.type_name
            ORDER BY cnt DESC
            LIMIT 5", sqlParams);

        // 3. Paginated Sample Elements (5 per page — never messy!)
        int pageSize = 5;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 0, totalPages - 1);
        var items = QueryDb(conn, $@"
            SELECT e.model_id, e.element_id, e.family, e.type_name, e.name,
                   (SELECT pl.display_value FROM parameters pl WHERE pl.model_id=e.model_id AND pl.element_id=e.element_id AND pl.name IN ('Level','Base Constraint','Reference Level') LIMIT 1) AS lvl,
                   (SELECT pa.display_value FROM parameters pa WHERE pa.model_id=e.model_id AND pa.element_id=e.element_id AND pa.name='Area' LIMIT 1) AS area_val,
                   (SELECT pv.display_value FROM parameters pv WHERE pv.model_id=e.model_id AND pv.element_id=e.element_id AND pv.name IN ('Volume','Length') LIMIT 1) AS dim_val
            FROM elements e
            WHERE {whereSql} AND e.kind='instance'
            ORDER BY e.model_id, e.element_id
            LIMIT {pageSize} OFFSET {page * pageSize}", sqlParams);

        var sb = new StringBuilder();
        sb.AppendLine($"🧱 <b>Category Digest: {H(category)}</b>");
        sb.AppendLine(banner);
        sb.AppendLine($"🔢 <b>Total Instances:</b> <code>{totalCount:N0}</code>");
        sb.AppendLine("────────────────────────");

        if (qtoRows.Count > 0)
        {
            sb.AppendLine("📐 <b>Quantity & Numeric Rollup (QTO):</b>");
            foreach (var q in qtoRows)
            {
                var pName = q["name"]?.ToString() ?? "";
                var sumVal = Convert.ToDouble(q["sum_val"] ?? 0.0, CultureInfo.InvariantCulture);
                var avgVal = Convert.ToDouble(q["avg_val"] ?? 0.0, CultureInfo.InvariantCulture);
                var minVal = Convert.ToDouble(q["min_val"] ?? 0.0, CultureInfo.InvariantCulture);
                var maxVal = Convert.ToDouble(q["max_val"] ?? 0.0, CultureInfo.InvariantCulture);
                var unit = FormatCleanUnit(pName, q["unit"]?.ToString());

                if (pName is "Area" or "Surface Area" or "Gross Floor Area" or "Volume" or "Length" or "Perimeter" or "Cost")
                {
                    sb.AppendLine($"• <b>Total {H(pName)}:</b> <code>{sumVal:N2} {H(unit)}</code> <i>(Avg: {avgVal:N2}, Min: {minVal:N2}, Max: {maxVal:N2})</i>");
                }
                else
                {
                    sb.AppendLine($"• <b>{H(pName)}:</b> Avg <code>{avgVal:N2} {H(unit)}</code> <i>(Min: {minVal:N2} — Max: {maxVal:N2})</i>");
                }
            }
            sb.AppendLine("────────────────────────");
        }

        if (topTypes.Count > 0)
        {
            sb.AppendLine("🏷 <b>Top 5 Family / Types:</b>");
            foreach (var t in topTypes)
            {
                var fam = t["family"]?.ToString() ?? "";
                var typ = t["type_name"]?.ToString() ?? "(No Type)";
                var cnt = Convert.ToInt64(t["cnt"] ?? 0L, CultureInfo.InvariantCulture);
                var label = string.IsNullOrEmpty(fam) ? typ : $"{fam} : {typ}";
                sb.AppendLine($"• <code>{cnt,4:N0}x</code> {H(Trunc(label, 38))}");
            }
            sb.AppendLine("────────────────────────");
        }

        sb.AppendLine($"📋 <b>Items Preview (Page {page + 1}/{totalPages}):</b>");
        var idButtons = new List<(string, string)>();
        foreach (var it in items)
        {
            var eid = it["element_id"]?.ToString() ?? "";
            var fam = it["family"]?.ToString() ?? "";
            var typ = it["type_name"]?.ToString() ?? it["name"]?.ToString() ?? "";
            var lvl = it["lvl"]?.ToString() ?? "";
            var area = it["area_val"]?.ToString() ?? "";
            var dim = it["dim_val"]?.ToString() ?? "";

            var metricTag = !string.IsNullOrWhiteSpace(area) ? $" | Area: <code>{H(area)}</code>"
                          : !string.IsNullOrWhiteSpace(dim) ? $" | <code>{H(dim)}</code>" : "";
            var lvlTag = !string.IsNullOrWhiteSpace(lvl) ? $" [{H(lvl)}]" : "";

            sb.AppendLine($"• <code>#{eid}</code> <b>{H(Trunc(typ, 26))}</b>{lvlTag}{metricTag}");
            if (idButtons.Count < 3) idButtons.Add(($"🔍 #{eid}", $"id:{eid}"));
        }

        var buttons = new List<List<(string, string)>>();
        if (idButtons.Count > 0) buttons.Add(idButtons);

        var nav = new List<(string, string)>();
        if (page > 0) nav.Add(("◀ Prev 5", $"catpage:{category}:{page - 1}"));
        if (page + 1 < totalPages) nav.Add(("Next 5 ▶", $"catpage:{category}:{page + 1}"));
        if (nav.Count > 0) buttons.Add(nav);

        buttons.Add(new()
        {
            ("📥 Download .CSV", $"csv:cat:{category}"),
            ("🧱 Other Categories", "cats:0"),
            ("⬅ Menu", "menu")
        });

        return ToResult(sb.ToString(), buttons);
    }

    // =========================================================================
    // 3. QUANTITY TAKE-OFF (QTO) & PARAMETER SUMMATION (/qto, /sum, /rooms)
    // =========================================================================
    private BotExecutionResult BuildQtoOverview(TelegramUserSession session, string? filterArg)
    {
        if (!string.IsNullOrWhiteSpace(filterArg))
        {
            return BuildCategoryQtoDigest(session, filterArg, 0);
        }

        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn, overrideCategory: null);
        var cleanWhere = whereSql.Replace("AND lower(e.category) = lower($cat)", "");

        var rows = QueryDb(conn, $@"
            SELECT e.category,
                   count(DISTINCT e.model_id || ':' || e.element_id) AS elem_cnt,
                   sum(CASE WHEN p.name IN ('Area','Gross Floor Area') AND p.storage_type IN ('Double','Integer') THEN CAST(p.raw_value AS REAL) ELSE 0 END) AS total_area,
                   sum(CASE WHEN p.name = 'Volume' AND p.storage_type IN ('Double','Integer') THEN CAST(p.raw_value AS REAL) ELSE 0 END) AS total_vol,
                   sum(CASE WHEN p.name = 'Length' AND p.storage_type IN ('Double','Integer') THEN CAST(p.raw_value AS REAL) ELSE 0 END) AS total_len
            FROM elements e
            LEFT JOIN parameters p ON p.model_id=e.model_id AND p.element_id=e.element_id
                AND p.name IN ('Area','Gross Floor Area','Volume','Length')
            WHERE {cleanWhere} AND e.kind='instance' AND e.category IS NOT NULL AND e.category <> ''
            GROUP BY e.category
            ORDER BY total_area DESC, elem_cnt DESC
            LIMIT 12", sqlParams);

        var sb = new StringBuilder();
        sb.AppendLine("📐 <b>Project Quantity Take-Off (QTO) Rollup</b>");
        sb.AppendLine(banner);
        sb.AppendLine("<i>Aggregated Count, Area, Volume & Length across Categories:</i>");
        sb.AppendLine("────────────────────────");

        double grandArea = 0, grandVol = 0, grandLen = 0;
        var buttons = new List<List<(string, string)>>();
        var btnRow = new List<(string, string)>();

        foreach (var r in rows)
        {
            var cat = r["category"]?.ToString() ?? "";
            var cnt = Convert.ToInt64(r["elem_cnt"] ?? 0L, CultureInfo.InvariantCulture);
            var area = Convert.ToDouble(r["total_area"] ?? 0.0, CultureInfo.InvariantCulture);
            var vol = Convert.ToDouble(r["total_vol"] ?? 0.0, CultureInfo.InvariantCulture);
            var len = Convert.ToDouble(r["total_len"] ?? 0.0, CultureInfo.InvariantCulture);

            grandArea += area;
            grandVol += vol;
            grandLen += len;

            var parts = new List<string> { $"<code>{cnt:N0}</code> el" };
            if (area > 0.01) parts.Add($"Area: <code>{area:N1} m²</code>");
            if (vol > 0.01) parts.Add($"Vol: <code>{vol:N1} m³</code>");
            if (len > 0.01) parts.Add($"Len: <code>{len:N1} m</code>");

            sb.AppendLine($"• <b>{H(cat)}</b>: {string.Join(" | ", parts)}");

            btnRow.Add(($"📐 {Trunc(cat, 16)}", $"setcat:{cat}"));
            if (btnRow.Count == 2)
            {
                buttons.Add(btnRow);
                btnRow = new List<(string, string)>();
            }
        }
        if (btnRow.Count > 0) buttons.Add(btnRow);

        sb.AppendLine("────────────────────────");
        sb.AppendLine($"Σ <b>Grand Total Area:</b> <code>{grandArea:N2} m²</code>");
        sb.AppendLine($"Σ <b>Grand Total Volume:</b> <code>{grandVol:N2} m³</code>");
        if (grandLen > 0) sb.AppendLine($"Σ <b>Grand Total Length:</b> <code>{grandLen:N2} m</code>");

        buttons.Add(new() { ("🚪 Room Area Schedule", "rooms::0"), ("⬅ Main Menu", "menu") });
        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildParameterSumCard(TelegramUserSession session, string paramName)
    {
        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn);
        sqlParams["$pname"] = paramName.Trim();

        var stats = QueryDb(conn, $@"
            SELECT e.category,
                   count(DISTINCT e.model_id || ':' || e.element_id) AS cnt,
                   sum(CAST(p.raw_value AS REAL)) AS sum_v,
                   avg(CAST(p.raw_value AS REAL)) AS avg_v,
                   min(CAST(p.raw_value AS REAL)) AS min_v,
                   max(CAST(p.raw_value AS REAL)) AS max_v,
                   max(p.unit_type_id) AS unit
            FROM parameters p
            JOIN elements e ON e.model_id=p.model_id AND e.element_id=e.element_id
            WHERE {whereSql}
              AND lower(p.name) = lower($pname)
              AND p.storage_type IN ('Double','Integer')
            GROUP BY e.category
            ORDER BY sum_v DESC
            LIMIT 12", sqlParams);

        var sb = new StringBuilder();
        sb.AppendLine($"∑ <b>Parameter Aggregation: {H(paramName)}</b>");
        sb.AppendLine(banner);
        sb.AppendLine("────────────────────────");

        if (stats.Count == 0)
        {
            sb.AppendLine($"No numeric values found for parameter <code>{H(paramName)}</code> in active scope.");
        }
        else
        {
            double totalSum = 0;
            long totalCnt = 0;
            string unitStr = "";
            foreach (var r in stats)
            {
                var cat = r["category"]?.ToString() ?? "Uncategorized";
                var cnt = Convert.ToInt64(r["cnt"] ?? 0L, CultureInfo.InvariantCulture);
                var sumV = Convert.ToDouble(r["sum_v"] ?? 0.0, CultureInfo.InvariantCulture);
                var avgV = Convert.ToDouble(r["avg_v"] ?? 0.0, CultureInfo.InvariantCulture);
                var minV = Convert.ToDouble(r["min_v"] ?? 0.0, CultureInfo.InvariantCulture);
                var maxV = Convert.ToDouble(r["max_v"] ?? 0.0, CultureInfo.InvariantCulture);
                unitStr = FormatCleanUnit(paramName, r["unit"]?.ToString());

                totalSum += sumV;
                totalCnt += cnt;
                sb.AppendLine($"• <b>{H(cat)}</b> (<code>{cnt:N0}</code>): Sum = <code>{sumV:N2} {H(unitStr)}</code> <i>(Avg {avgV:N2}, Min {minV:N2}, Max {maxV:N2})</i>");
            }
            sb.AppendLine("────────────────────────");
            sb.AppendLine($"🏆 <b>Grand Total ({totalCnt:N0} items):</b> <code>{totalSum:N2} {H(unitStr)}</code>");
        }

        return ToResult(sb.ToString(), new() { new() { ("📐 QTO Rollup", "qto:"), ("⬅ Main Menu", "menu") } });
    }

    private BotExecutionResult BuildRoomsSchedule(TelegramUserSession session, string filterArg, int page)
    {
        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn, overrideCategory: null);
        var cleanWhere = whereSql.Replace("AND lower(e.category) = lower($cat)", "");

        var extraFilter = "";
        if (!string.IsNullOrWhiteSpace(filterArg))
        {
            extraFilter = " AND (instr(lower(e.name), lower($rf)) > 0 OR instr(lower(e.family), lower($rf)) > 0 OR EXISTS(SELECT 1 FROM parameters pr WHERE pr.model_id=e.model_id AND pr.element_id=e.element_id AND instr(lower(pr.display_value), lower($rf)) > 0))";
            sqlParams["$rf"] = filterArg.Trim();
        }

        var roomWhere = $"{cleanWhere} AND e.kind='instance' AND e.category IN ('Rooms','Areas','Spaces'){extraFilter}";
        var totRooms = Convert.ToInt64(QueryDb(conn, $"SELECT count(*) c FROM elements e WHERE {roomWhere}", sqlParams)[0]["c"] ?? 0L, CultureInfo.InvariantCulture);

        var areaStats = QueryDb(conn, $@"
            SELECT sum(CAST(p.raw_value AS REAL)) AS total_area,
                   avg(CAST(p.raw_value AS REAL)) AS avg_area,
                   min(CAST(p.raw_value AS REAL)) AS min_area,
                   max(CAST(p.raw_value AS REAL)) AS max_area
            FROM parameters p
            JOIN elements e ON e.model_id=p.model_id AND e.element_id=e.element_id
            WHERE {roomWhere} AND p.name='Area' AND p.storage_type IN ('Double','Integer')", sqlParams).FirstOrDefault();

        int pageSize = 6;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totRooms / (double)pageSize));
        page = Math.Clamp(page, 0, totalPages - 1);

        var rows = QueryDb(conn, $@"
            SELECT e.element_id, e.category, e.name,
                   (SELECT p1.display_value FROM parameters p1 WHERE p1.model_id=e.model_id AND p1.element_id=e.element_id AND p1.name='Number' LIMIT 1) AS rm_num,
                   (SELECT p2.display_value FROM parameters p2 WHERE p2.model_id=e.model_id AND p2.element_id=e.element_id AND p2.name IN ('Level','Reference Level') LIMIT 1) AS rm_lvl,
                   (SELECT p3.display_value FROM parameters p3 WHERE p3.model_id=e.model_id AND p3.element_id=e.element_id AND p3.name='Area' LIMIT 1) AS rm_area,
                   (SELECT CAST(p4.raw_value AS REAL) FROM parameters p4 WHERE p4.model_id=e.model_id AND p4.element_id=e.element_id AND p4.name='Area' LIMIT 1) AS raw_area
            FROM elements e
            WHERE {roomWhere}
            ORDER BY raw_area DESC, e.element_id ASC
            LIMIT {pageSize} OFFSET {page * pageSize}", sqlParams);

        var sb = new StringBuilder();
        sb.AppendLine("🚪 <b>Room / Space & Area Schedule</b>");
        sb.AppendLine(banner);
        if (!string.IsNullOrWhiteSpace(filterArg)) sb.AppendLine($"🔍 Filter: <code>{H(filterArg)}</code>");
        sb.AppendLine("────────────────────────");

        if (totRooms == 0)
        {
            sb.AppendLine("No Rooms/Spaces/Areas matched in the current scope. Try <code>/qto Floors</code> or clear filters.");
        }
        else
        {
            var totArea = Convert.ToDouble(areaStats?["total_area"] ?? 0.0, CultureInfo.InvariantCulture);
            var avgArea = Convert.ToDouble(areaStats?["avg_area"] ?? 0.0, CultureInfo.InvariantCulture);
            var maxArea = Convert.ToDouble(areaStats?["max_area"] ?? 0.0, CultureInfo.InvariantCulture);

            sb.AppendLine($"📊 <b>Total Rooms/Spaces:</b> <code>{totRooms:N0}</code>");
            sb.AppendLine($"📐 <b>Total Floor Area:</b> <code>{totArea:N2} m²</code> <i>(Avg: {avgArea:N2} m², Max: {maxArea:N2} m²)</i>");
            sb.AppendLine("────────────────────────");
            sb.AppendLine($"<b>Schedule Preview (Page {page + 1}/{totalPages}, sorted by Area):</b>");

            foreach (var r in rows)
            {
                var eid = r["element_id"]?.ToString() ?? "";
                var num = r["rm_num"]?.ToString() ?? "";
                var name = r["name"]?.ToString() ?? "Room";
                var lvl = r["rm_lvl"]?.ToString() ?? "";
                var areaDisp = r["rm_area"]?.ToString() ?? "N/A";
                var numTag = !string.IsNullOrWhiteSpace(num) ? $"{num} - " : "";
                var lvlTag = !string.IsNullOrWhiteSpace(lvl) ? $" ({lvl})" : "";

                sb.AppendLine($"• <code>#{eid}</code> <b>{H(numTag + name)}</b>{H(lvlTag)} — <code>{H(areaDisp)}</code>");
            }
        }

        var buttons = new List<List<(string, string)>>();
        var nav = new List<(string, string)>();
        if (page > 0) nav.Add(("◀ Prev 6", $"rooms:{filterArg}:{page - 1}"));
        if (page + 1 < totalPages) nav.Add(("Next 6 ▶", $"rooms:{filterArg}:{page + 1}"));
        if (nav.Count > 0) buttons.Add(nav);

        buttons.Add(new() { ("📥 Export Rooms .CSV", "csv:cat:Rooms"), ("⬅ Main Menu", "menu") });
        return ToResult(sb.ToString(), buttons);
    }

    // =========================================================================
    // 4. NUMERIC & PARAMETER FILTER ENGINE (/num Area > 50)
    // =========================================================================
    private BotExecutionResult BuildNumericFilterDigest(TelegramUserSession session, string expr, int page)
    {
        // Parse "<ParameterName> <Operator> <Value>" e.g. "Area > 50", "Unconnected Height >= 3.5", "Fire Rating = 2 HR"
        var m = Regex.Match(expr.Trim(), @"^(.+?)\s*(>=|<=|!=|>|<|=|~)\s*(.+)$");
        if (!m.Success)
        {
            return ToResult(
                "🔢 <b>Numeric & Parameter Query Syntax</b>\n" +
                "Use: <code>/num &lt;Parameter&gt; &lt;Operator&gt; &lt;Value&gt;</code>\n\n" +
                "<b>Examples:</b>\n" +
                "• <code>/num Area &gt; 50</code>\n" +
                "• <code>/num Volume &gt;= 10</code>\n" +
                "• <code>/num Unconnected Height &gt; 3.5</code>\n" +
                "• <code>/num Fire Rating = 2 HR</code>",
                new() { new() { ("📐 Area > 50", "numpage:Area > 50:0"), ("📏 Height > 3", "numpage:Unconnected Height > 3:0") }, new() { ("⬅ Main Menu", "menu") } });
        }

        var paramName = m.Groups[1].Value.Trim();
        var op = m.Groups[2].Value.Trim();
        var valStr = m.Groups[3].Value.Trim();
        bool isNumeric = double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var numTarget);

        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn);
        sqlParams["$pname"] = paramName;

        string condSql;
        if (isNumeric && op is ">" or ">=" or "<" or "<=" or "=" or "!=")
        {
            var sqlOp = op == "=" ? "=" : op;
            condSql = $"p.storage_type IN ('Double','Integer') AND CAST(p.raw_value AS REAL) {sqlOp} $nval";
            sqlParams["$nval"] = numTarget;
        }
        else
        {
            condSql = op == "!="
                ? "lower(p.display_value) <> lower($sval)"
                : "instr(lower(p.display_value), lower($sval)) > 0";
            sqlParams["$sval"] = valStr;
        }

        var summary = QueryDb(conn, $@"
            SELECT count(DISTINCT e.model_id || ':' || e.element_id) AS cnt,
                   sum(CASE WHEN p.storage_type IN ('Double','Integer') THEN CAST(p.raw_value AS REAL) ELSE 0 END) AS sum_v,
                   avg(CASE WHEN p.storage_type IN ('Double','Integer') THEN CAST(p.raw_value AS REAL) ELSE 0 END) AS avg_v,
                   min(CASE WHEN p.storage_type IN ('Double','Integer') THEN CAST(p.raw_value AS REAL) ELSE 0 END) AS min_v,
                   max(CASE WHEN p.storage_type IN ('Double','Integer') THEN CAST(p.raw_value AS REAL) ELSE 0 END) AS max_v,
                   max(p.unit_type_id) AS unit
            FROM parameters p
            JOIN elements e ON e.model_id=p.model_id AND e.element_id=e.element_id
            WHERE {whereSql} AND e.kind='instance' AND lower(p.name)=lower($pname) AND {condSql}", sqlParams).FirstOrDefault();

        var totalMatched = Convert.ToInt64(summary?["cnt"] ?? 0L, CultureInfo.InvariantCulture);
        var sumV = Convert.ToDouble(summary?["sum_v"] ?? 0.0, CultureInfo.InvariantCulture);
        var avgV = Convert.ToDouble(summary?["avg_v"] ?? 0.0, CultureInfo.InvariantCulture);
        var maxV = Convert.ToDouble(summary?["max_v"] ?? 0.0, CultureInfo.InvariantCulture);
        var unit = FormatCleanUnit(paramName, summary?["unit"]?.ToString());

        int pageSize = 5;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalMatched / (double)pageSize));
        page = Math.Clamp(page, 0, totalPages - 1);

        var rows = QueryDb(conn, $@"
            SELECT e.element_id, e.category, e.family, e.type_name, p.display_value, CAST(p.raw_value AS REAL) AS rval
            FROM parameters p
            JOIN elements e ON e.model_id=p.model_id AND e.element_id=e.element_id
            WHERE {whereSql} AND e.kind='instance' AND lower(p.name)=lower($pname) AND {condSql}
            ORDER BY rval DESC, e.element_id ASC
            LIMIT {pageSize} OFFSET {page * pageSize}", sqlParams);

        var sb = new StringBuilder();
        sb.AppendLine($"🔢 <b>Parameter Query:</b> <code>{H(paramName)} {H(op)} {H(valStr)}</code>");
        sb.AppendLine(banner);
        sb.AppendLine("────────────────────────");
        sb.AppendLine($"✅ <b>Matched Elements:</b> <code>{totalMatched:N0}</code>");
        if (isNumeric && totalMatched > 0)
        {
            sb.AppendLine($"Σ <b>Total {H(paramName)}:</b> <code>{sumV:N2} {H(unit)}</code> <i>(Avg: {avgV:N2}, Max: {maxV:N2})</i>");
        }
        sb.AppendLine("────────────────────────");

        var idButtons = new List<(string, string)>();
        if (rows.Count == 0)
        {
            sb.AppendLine("No elements matched this condition in the active scope.");
        }
        else
        {
            sb.AppendLine($"<b>Top Results (Page {page + 1}/{totalPages}):</b>");
            foreach (var r in rows)
            {
                var eid = r["element_id"]?.ToString() ?? "";
                var cat = r["category"]?.ToString() ?? "";
                var typ = r["type_name"]?.ToString() ?? r["family"]?.ToString() ?? "";
                var disp = r["display_value"]?.ToString() ?? "";
                sb.AppendLine($"• <code>#{eid}</code> [{H(cat)}] <b>{H(Trunc(typ, 24))}</b> = <code>{H(disp)}</code>");
                if (idButtons.Count < 3) idButtons.Add(($"🔍 #{eid}", $"id:{eid}"));
            }
        }

        var buttons = new List<List<(string, string)>>();
        if (idButtons.Count > 0) buttons.Add(idButtons);

        var nav = new List<(string, string)>();
        var safeExpr = expr.Replace(":", " ");
        if (page > 0) nav.Add(("◀ Prev 5", $"numpage:{safeExpr}:{page - 1}"));
        if (page + 1 < totalPages) nav.Add(("Next 5 ▶", $"numpage:{safeExpr}:{page + 1}"));
        if (nav.Count > 0) buttons.Add(nav);
        buttons.Add(new() { ("⬅ Main Menu", "menu") });

        return ToResult(sb.ToString(), buttons);
    }

    // =========================================================================
    // 5. SMART KEYWORD SEARCH (/find) & ELEMENT DEEP-DIVE CARD (/id)
    // =========================================================================
    private BotExecutionResult BuildSearchDigest(TelegramUserSession session, string keyword, int page)
    {
        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, banner) = BuildScopeFilter(session, conn);
        sqlParams["$kw"] = keyword.Trim();

        var searchCond = $@"({whereSql}) AND e.kind='instance' AND (
            instr(lower(e.category), lower($kw)) > 0 OR
            instr(lower(e.family), lower($kw)) > 0 OR
            instr(lower(e.type_name), lower($kw)) > 0 OR
            instr(lower(e.name), lower($kw)) > 0 OR
            instr(lower(CAST(e.element_id AS TEXT)), lower($kw)) > 0 OR
            EXISTS(SELECT 1 FROM parameters ps WHERE ps.model_id=e.model_id AND ps.element_id=e.element_id AND ps.name IN ('Mark','Type Mark','Comments','Room Name','Number','Level') AND instr(lower(ps.display_value), lower($kw)) > 0)
        )";

        var totalMatched = Convert.ToInt64(QueryDb(conn, $"SELECT count(*) c FROM elements e WHERE {searchCond}", sqlParams)[0]["c"] ?? 0L, CultureInfo.InvariantCulture);

        // Category breakdown + Area/Volume rollup for matched items
        var catBreakdown = QueryDb(conn, $@"
            SELECT e.category, count(*) AS cnt
            FROM elements e
            WHERE {searchCond}
            GROUP BY e.category
            ORDER BY cnt DESC
            LIMIT 4", sqlParams);

        var qtoMatch = QueryDb(conn, $@"
            SELECT p.name, sum(CAST(p.raw_value AS REAL)) AS sum_v, max(p.unit_type_id) AS unit
            FROM parameters p
            JOIN elements e ON e.model_id=p.model_id AND e.element_id=e.element_id
            WHERE {searchCond} AND p.name IN ('Area','Volume','Length') AND p.storage_type IN ('Double','Integer')
            GROUP BY p.name", sqlParams);

        int pageSize = 5;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalMatched / (double)pageSize));
        page = Math.Clamp(page, 0, totalPages - 1);

        var rows = QueryDb(conn, $@"
            SELECT e.element_id, e.category, e.family, e.type_name, e.name,
                   (SELECT pl.display_value FROM parameters pl WHERE pl.model_id=e.model_id AND pl.element_id=e.element_id AND pl.name IN ('Level','Base Constraint','Reference Level') LIMIT 1) AS lvl,
                   (SELECT pa.display_value FROM parameters pa WHERE pa.model_id=e.model_id AND pa.element_id=e.element_id AND pa.name IN ('Area','Volume','Length') LIMIT 1) AS metric
            FROM elements e
            WHERE {searchCond}
            ORDER BY e.model_id, e.element_id
            LIMIT {pageSize} OFFSET {page * pageSize}", sqlParams);

        var sb = new StringBuilder();
        sb.AppendLine($"🔍 <b>Search Results for:</b> <code>{H(keyword)}</code>");
        sb.AppendLine(banner);
        sb.AppendLine("────────────────────────");
        sb.AppendLine($"🎯 <b>Matched Instances:</b> <code>{totalMatched:N0}</code>");

        if (catBreakdown.Count > 0)
        {
            var catPills = catBreakdown.Select(c => $"{H(c["category"]?.ToString() ?? "")} ({c["cnt"]})");
            sb.AppendLine($"🧱 <b>Categories:</b> {string.Join(", ", catPills)}");
        }

        if (qtoMatch.Count > 0)
        {
            var qPills = qtoMatch.Select(q =>
            {
                var n = q["name"]?.ToString() ?? "";
                var v = Convert.ToDouble(q["sum_v"] ?? 0.0, CultureInfo.InvariantCulture);
                var u = FormatCleanUnit(n, q["unit"]?.ToString());
                return $"{n}: <code>{v:N2} {H(u)}</code>";
            });
            sb.AppendLine($"📐 <b>Matched QTO Sum:</b> {string.Join(" | ", qPills)}");
        }

        sb.AppendLine("────────────────────────");
        var idButtons = new List<(string, string)>();
        if (rows.Count == 0)
        {
            sb.AppendLine("No elements matched your keyword. Try <code>/cats</code> to browse categories.");
        }
        else
        {
            sb.AppendLine($"<b>Preview (Page {page + 1}/{totalPages}, 5 per page):</b>");
            foreach (var r in rows)
            {
                var eid = r["element_id"]?.ToString() ?? "";
                var cat = r["category"]?.ToString() ?? "";
                var fam = r["family"]?.ToString() ?? "";
                var typ = r["type_name"]?.ToString() ?? r["name"]?.ToString() ?? "";
                var lvl = r["lvl"]?.ToString() ?? "";
                var metric = r["metric"]?.ToString() ?? "";

                var label = string.IsNullOrWhiteSpace(fam) ? typ : $"{fam} : {typ}";
                var extra = (!string.IsNullOrEmpty(lvl) ? $" [{H(lvl)}]" : "") + (!string.IsNullOrEmpty(metric) ? $" • <code>{H(metric)}</code>" : "");
                sb.AppendLine($"• <code>#{eid}</code> [{H(cat)}] <b>{H(Trunc(label, 30))}</b>{extra}");
                if (idButtons.Count < 3) idButtons.Add(($"🔍 #{eid}", $"id:{eid}"));
            }
        }

        var buttons = new List<List<(string, string)>>();
        if (idButtons.Count > 0) buttons.Add(idButtons);

        var safeKw = keyword.Replace(":", " ");
        var nav = new List<(string, string)>();
        if (page > 0) nav.Add(("◀ Prev 5", $"findpage:{safeKw}:{page - 1}"));
        if (page + 1 < totalPages) nav.Add(("Next 5 ▶", $"findpage:{safeKw}:{page + 1}"));
        if (nav.Count > 0) buttons.Add(nav);

        buttons.Add(new()
        {
            ("📥 Download Match .CSV", $"csv:search:{safeKw}"),
            ("⬅ Main Menu", "menu")
        });

        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildElementDetailCard(TelegramUserSession session, string rawId)
    {
        using var conn = OpenSessionDb(session);
        var cleanId = rawId.Trim().TrimStart('#');
        var el = QueryDb(conn, @"
            SELECT e.*, m.path AS model_name
            FROM elements e
            JOIN models m USING(model_id)
            WHERE CAST(e.element_id AS TEXT) = $id OR e.unique_id = $id
            LIMIT 1", new() { ["$id"] = cleanId }).FirstOrDefault();

        if (el == null)
        {
            return ToResult($"❌ Element <code>{H(cleanId)}</code> not found in project <b>{H(session.ActiveProjectName)}</b>.",
                new() { new() { ("⬅ Main Menu", "menu") } });
        }

        var mId = Convert.ToInt32(el["model_id"], CultureInfo.InvariantCulture);
        var elId = Convert.ToInt64(el["element_id"], CultureInfo.InvariantCulture);
        var pRows = QueryDb(conn, @"
            SELECT scope, name, storage_type, raw_value, display_value, unit_type_id
            FROM parameters
            WHERE model_id = $m AND element_id = $e AND has_value = 1 AND trim(display_value) <> ''
            ORDER BY scope, name", new() { ["$m"] = mId, ["$e"] = elId });

        var sb = new StringBuilder();
        sb.AppendLine($"🔎 <b>Element Evidence Card: #{elId}</b>");
        sb.AppendLine($"📦 <b>Model:</b> {H(el["model_name"]?.ToString() ?? "")}");
        sb.AppendLine($"🧱 <b>Category:</b> {H(el["category"]?.ToString() ?? "")} ({H(el["kind"]?.ToString() ?? "")})");
        sb.AppendLine($"🏷 <b>Family / Type:</b> {H(el["family"]?.ToString() ?? "")} : <b>{H(el["type_name"]?.ToString() ?? "")}</b>");
        sb.AppendLine($"🔑 <b>UniqueId:</b> <code>{H(el["unique_id"]?.ToString() ?? "")}</code>");
        sb.AppendLine("────────────────────────");

        // Group 1: Quantities & Dimensions (Numbers)
        var numericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Area", "Surface Area", "Gross Floor Area", "Volume", "Length", "Perimeter",
            "Width", "Height", "Unconnected Height", "Thickness", "Elevation", "Base Offset",
            "Top Offset", "Sill Height", "Head Height", "Cost"
        };

        var quantParams = pRows.Where(p => numericNames.Contains(p["name"]?.ToString() ?? "") || p["storage_type"]?.ToString() == "Double").Take(10).ToList();
        if (quantParams.Count > 0)
        {
            sb.AppendLine("📐 <b>Dimensions & Quantities:</b>");
            foreach (var p in quantParams)
            {
                var n = p["name"]?.ToString() ?? "";
                var v = p["display_value"]?.ToString() ?? "";
                var u = FormatCleanUnit(n, p["unit_type_id"]?.ToString());
                var uSuffix = !string.IsNullOrEmpty(u) && !v.Contains(u, StringComparison.OrdinalIgnoreCase) ? $" {u}" : "";
                sb.AppendLine($"• <b>{H(n)}:</b> <code>{H(v)}{H(uSuffix)}</code>");
            }
            sb.AppendLine("────────────────────────");
        }

        // Group 2: Key Specifications
        var specParams = pRows.Where(p => !quantParams.Contains(p)).Take(12).ToList();
        if (specParams.Count > 0)
        {
            sb.AppendLine("📋 <b>Key Parameters:</b>");
            foreach (var p in specParams)
            {
                sb.AppendLine($"• <b>{H(p["name"]?.ToString() ?? "")}:</b> <code>{H(Trunc(p["display_value"]?.ToString() ?? "", 40))}</code>");
            }
        }

        // Group 3: Check QA/QC Issues on this element
        if (TableExists(conn, "issues"))
        {
            var issRows = QueryDb(conn, "SELECT rule_id, severity, lifecycle_state, message FROM issues WHERE model_id=$m AND element_id=$e", new() { ["$m"] = mId, ["$e"] = elId });
            if (issRows.Count > 0)
            {
                sb.AppendLine("────────────────────────");
                sb.AppendLine($"⚠️ <b>QA/QC Issues ({issRows.Count}):</b>");
                foreach (var iss in issRows.Take(3))
                {
                    sb.AppendLine($"• [{H(iss["severity"]?.ToString() ?? "")} / {H(iss["lifecycle_state"]?.ToString() ?? "")}] <b>{H(iss["rule_id"]?.ToString() ?? "")}</b>: {H(iss["message"]?.ToString() ?? "")}");
                }
            }
        }

        var cat = el["category"]?.ToString() ?? "";
        var buttons = new List<List<(string, string)>>
        {
            new() { ($"🧱 Back to {Trunc(cat, 18)}", $"setcat:{cat}"), ("⬅ Main Menu", "menu") }
        };
        return ToResult(sb.ToString(), buttons);
    }

    // =========================================================================
    // 6. QA/QC ISSUES, DIFFS & PROJECT READINESS STATUS
    // =========================================================================
    private BotExecutionResult BuildProjectStatusCard(TelegramUserSession session)
    {
        using var conn = OpenSessionDb(session);
        var (_, _, banner) = BuildScopeFilter(session, conn);
        var conf = TableExists(conn, "confidence_summary")
            ? QueryDb(conn, "SELECT * FROM confidence_summary LIMIT 1").FirstOrDefault()
            : null;

        var sb = new StringBuilder();
        sb.AppendLine("📈 <b>Project Delivery Readiness & Audit Status</b>");
        sb.AppendLine(banner);
        sb.AppendLine("────────────────────────");

        if (conf != null)
        {
            var app = Convert.ToInt64(conf["applicable_checks"] ?? 0L, CultureInfo.InvariantCulture);
            var eval = Convert.ToInt64(conf["evaluated_checks"] ?? 0L, CultureInfo.InvariantCulture);
            var pass = Convert.ToInt64(conf["passed_checks"] ?? 0L, CultureInfo.InvariantCulture);
            var fail = Convert.ToInt64(conf["failed_checks"] ?? 0L, CultureInfo.InvariantCulture);
            var fix = Convert.ToInt64(conf["fixed_issues"] ?? 0L, CultureInfo.InvariantCulture);
            var nw = Convert.ToInt64(conf["new_issues"] ?? 0L, CultureInfo.InvariantCulture);
            var reg = Convert.ToInt64(conf["regressed_issues"] ?? 0L, CultureInfo.InvariantCulture);
            var add = Convert.ToInt64(conf["added_elements"] ?? 0L, CultureInfo.InvariantCulture);
            var rem = Convert.ToInt64(conf["removed_elements"] ?? 0L, CultureInfo.InvariantCulture);
            var mod = Convert.ToInt64(conf["modified_elements"] ?? 0L, CultureInfo.InvariantCulture);

            var passPct = eval > 0 ? Math.Round(100.0 * pass / eval, 1) : 100.0;
            sb.AppendLine($"🎯 <b>Compliance Pass Rate:</b> <code>{passPct:F1}%</code> ({pass:N0}/{eval:N0} evaluated)");
            sb.AppendLine($"⚠️ <b>Failed Checks:</b> <code>{fail:N0}</code>");
            sb.AppendLine($"🔄 <b>Issue Lifecycle:</b> ✅ Fixed: <code>{fix}</code> | 🆕 New: <code>{nw}</code> | 🔴 Regressed: <code>{reg}</code>");
            sb.AppendLine($"📊 <b>Element Changes:</b> ➕ Added: <code>{add}</code> | ➖ Removed: <code>{rem}</code> | ✏️ Modified: <code>{mod}</code>");
        }

        var buttons = new List<List<(string, string)>>
        {
            new() { ("⚠️ Issues by Team", "issues::0"), ("🔄 Element Diffs", "diff:") },
            new() { ("📐 QTO Rollup", "qto:"), ("⬅ Main Menu", "menu") }
        };
        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildIssuesCard(TelegramUserSession session, string disciplineFilter, int page)
    {
        using var conn = OpenSessionDb(session);
        if (!TableExists(conn, "issues"))
            return ToResult("No QA/QC issues table in this run.", new() { new() { ("⬅ Main Menu", "menu") } });

        var p = new Dictionary<string, object>();
        var terms = new List<string> { "1=1" };
        if (session.ActiveModelId.HasValue) { terms.Add("i.model_id=$mid"); p["$mid"] = session.ActiveModelId.Value; }
        if (!string.IsNullOrWhiteSpace(disciplineFilter)) { terms.Add("upper(i.discipline)=upper($disc)"); p["$disc"] = disciplineFilter.Trim(); }

        var where = string.Join(" AND ", terms);
        var tot = Convert.ToInt64(QueryDb(conn, $"SELECT count(*) c FROM issues i WHERE {where}", p)[0]["c"] ?? 0L, CultureInfo.InvariantCulture);
        var byDisc = QueryDb(conn, "SELECT discipline, count(*) c FROM issues GROUP BY discipline ORDER BY c DESC");

        int pageSize = 5;
        int totalPages = Math.Max(1, (int)Math.Ceiling(tot / (double)pageSize));
        page = Math.Clamp(page, 0, totalPages - 1);

        var rows = QueryDb(conn, $@"
            SELECT i.element_id, i.discipline, i.severity, i.lifecycle_state, i.rule_id, i.category, i.parameter_name, i.actual_value, i.expected_rule
            FROM issues i
            WHERE {where}
            ORDER BY CASE i.severity WHEN 'Critical' THEN 1 WHEN 'Warning' THEN 2 ELSE 3 END, i.issue_id
            LIMIT {pageSize} OFFSET {page * pageSize}", p);

        var sb = new StringBuilder();
        sb.AppendLine($"⚠️ <b>QA/QC Action List {(string.IsNullOrEmpty(disciplineFilter) ? "(All Teams)" : $"({H(disciplineFilter)})")}</b>");
        sb.AppendLine($"Total Matching Issues: <code>{tot:N0}</code>");
        if (byDisc.Count > 0)
        {
            sb.AppendLine("Teams: " + string.Join(" | ", byDisc.Select(d => $"<b>{H(d["discipline"]?.ToString() ?? "")}</b>: <code>{d["c"]}</code>")));
        }
        sb.AppendLine("────────────────────────");

        var idBtns = new List<(string, string)>();
        foreach (var r in rows)
        {
            var eid = r["element_id"]?.ToString() ?? "";
            var sev = r["severity"]?.ToString() ?? "";
            var life = r["lifecycle_state"]?.ToString() ?? "";
            var cat = r["category"]?.ToString() ?? "";
            var pname = r["parameter_name"]?.ToString() ?? "";
            var aval = r["actual_value"]?.ToString() ?? "Empty";
            var exp = r["expected_rule"]?.ToString() ?? "";

            sb.AppendLine($"• <code>#{eid}</code> [{H(sev)}/{H(life)}] <b>{H(cat)}</b> — <code>{H(pname)}</code> = <code>{H(aval)}</code> <i>(Req: {H(exp)})</i>");
            if (idBtns.Count < 3) idBtns.Add(($"🔍 #{eid}", $"id:{eid}"));
        }

        var buttons = new List<List<(string, string)>>
        {
            new() { ("All", "issues::0"), ("AR", "issues:AR:0"), ("STR", "issues:STR:0"), ("MEP", "issues:MEP:0") }
        };
        if (idBtns.Count > 0) buttons.Add(idBtns);

        var nav = new List<(string, string)>();
        if (page > 0) nav.Add(("◀ Prev 5", $"issues:{disciplineFilter}:{page - 1}"));
        if (page + 1 < totalPages) nav.Add(("Next 5 ▶", $"issues:{disciplineFilter}:{page + 1}"));
        if (nav.Count > 0) buttons.Add(nav);

        buttons.Add(new() { ("📥 Download issues.csv", "csv:issues:all"), ("⬅ Main Menu", "menu") });
        return ToResult(sb.ToString(), buttons);
    }

    private BotExecutionResult BuildDiffsCard(TelegramUserSession session, string changeFilter)
    {
        using var conn = OpenSessionDb(session);
        if (!TableExists(conn, "element_diffs"))
            return ToResult("No element_diffs table found.", new() { new() { ("⬅ Main Menu", "menu") } });

        var byType = QueryDb(conn, "SELECT change_type, count(*) c FROM element_diffs GROUP BY change_type");
        var p = new Dictionary<string, object>();
        var where = "1=1";
        if (!string.IsNullOrWhiteSpace(changeFilter))
        {
            where = "change_type = $ct";
            p["$ct"] = changeFilter;
        }

        var rows = QueryDb(conn, $"SELECT * FROM element_diffs WHERE {where} LIMIT 6", p);
        var sb = new StringBuilder();
        sb.AppendLine("🔄 <b>Element-Level Model Diffs (Before → After)</b>");
        if (byType.Count > 0)
        {
            sb.AppendLine(string.Join(" | ", byType.Select(b => $"<b>{H(b["change_type"]?.ToString() ?? "")}:</b> <code>{b["c"]}</code>")));
        }
        sb.AppendLine("────────────────────────");

        if (rows.Count == 0)
        {
            sb.AppendLine("No element changes recorded against the previous version.");
        }
        else
        {
            foreach (var r in rows)
            {
                var ct = r["change_type"]?.ToString() ?? "";
                var eid = r["element_id"]?.ToString() ?? "";
                var cat = r["category"]?.ToString() ?? "";
                var pn = r["parameter_name"]?.ToString() ?? "";
                var oldV = r["old_value"]?.ToString() ?? "";
                var newV = r["new_value"]?.ToString() ?? "";
                sb.AppendLine($"• [{H(ct)}] <code>#{eid}</code> <b>{H(cat)}</b> ({H(pn)}): <code>{H(oldV)}</code> → <code>{H(newV)}</code>");
            }
        }

        var buttons = new List<List<(string, string)>>
        {
            new() { ("All", "diff:"), ("➕ Added", "diff:Added"), ("✏️ Modified", "diff:Modified"), ("➖ Removed", "diff:Removed") },
            new() { ("⬅ Main Menu", "menu") }
        };
        return ToResult(sb.ToString(), buttons);
    }

    // =========================================================================
    // 7. 2D DRAWING SHEETS & CLOUD VECTOR PDF DELIVERY (/sheets, /pdf)
    // =========================================================================
    private async Task<(string Urn, string ModelName, List<(string Name, string Role, string PdfUrn)> Sheets)> FetchActiveModelSheetsAsync(TelegramUserSession session)
    {
        using var conn = OpenSessionDb(session);
        var mId = session.ActiveModelId ?? 1;
        var mRow = QueryDb(conn, "SELECT model_id, path, derivative_urn FROM models WHERE model_id=$m LIMIT 1", new() { ["$m"] = mId }).FirstOrDefault()
                ?? QueryDb(conn, "SELECT model_id, path, derivative_urn FROM models ORDER BY model_id LIMIT 1").FirstOrDefault();

        if (mRow == null) return ("", "", []);
        var modelName = mRow["path"]?.ToString() ?? "Model";
        var urn = mRow.TryGetValue("derivative_urn", out var uObj) ? uObj?.ToString() ?? "" : "";
        if (urn.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)) urn = urn[4..];
        if (string.IsNullOrWhiteSpace(urn)) return ("", modelName, []);

        var token = await getApsAccessTokenAsync();
        var url = $"https://developer.api.autodesk.com/modelderivative/v2/designdata/{Uri.EscapeDataString(urn)}/manifest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await httpClient.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return (urn, modelName, []);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var list = new List<(string Name, string Role, string PdfUrn)>();

        void Walk(JsonElement node)
        {
            var type = node.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var role = node.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
            if (type == "geometry" && (role == "2d" || role == "3d"))
            {
                var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string pdfUrn = "";
                if (node.TryGetProperty("children", out var chArr) && chArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ch in chArr.EnumerateArray())
                    {
                        var cr = ch.TryGetProperty("role", out var crp) ? crp.GetString() ?? "" : "";
                        var cm = ch.TryGetProperty("mime", out var cmp) ? cmp.GetString() ?? "" : "";
                        if (cr == "pdf-page" || cm == "application/pdf")
                        {
                            pdfUrn = ch.TryGetProperty("urn", out var cup) ? cup.GetString() ?? "" : "";
                            break;
                        }
                    }
                }
                list.Add((name, role, pdfUrn));
            }
            if (node.TryGetProperty("children", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                foreach (var c in cArr.EnumerateArray()) Walk(c);
            if (node.TryGetProperty("derivatives", out var dArr) && dArr.ValueKind == JsonValueKind.Array)
                foreach (var d in dArr.EnumerateArray()) Walk(d);
        }

        Walk(doc.RootElement);
        var sorted = list.OrderByDescending(x => x.Role == "2d").ThenBy(x => x.Name).ToList();
        return (urn, modelName, sorted);
    }

    private async Task<BotExecutionResult> BuildSheetsCardAsync(TelegramUserSession session, int page)
    {
        try
        {
            var (urn, modelName, sheets) = await FetchActiveModelSheetsAsync(session);
            var sb = new StringBuilder();
            sb.AppendLine("📐 <b>2D Drawing Sheets & Cloud Vector PDFs</b>");
            sb.AppendLine($"📦 <b>Model:</b> {H(modelName)}");
            sb.AppendLine("────────────────────────");

            if (sheets.Count == 0)
            {
                sb.AppendLine("No published 2D sheets or 3D views found in Autodesk Cloud Manifest for this model.");
                return ToResult(sb.ToString(), new() { new() { ("⬅ Main Menu", "menu") } });
            }

            int pageSize = 6;
            int totalPages = Math.Max(1, (int)Math.Ceiling(sheets.Count / (double)pageSize));
            page = Math.Clamp(page, 0, totalPages - 1);

            sb.AppendLine($"Found <b>{sheets.Count(s => s.Role == "2d")}</b> 2D Sheets & <b>{sheets.Count(s => s.Role == "3d")}</b> 3D Views (Page {page + 1}/{totalPages}):\n");

            var buttons = new List<List<(string, string)>>();
            for (int i = page * pageSize; i < Math.Min(sheets.Count, (page + 1) * pageSize); i++)
            {
                var s = sheets[i];
                var badge = s.Role == "2d" ? "📐 2D" : "🧊 3D";
                var pdfBadge = !string.IsNullOrEmpty(s.PdfUrn) ? " <i>[Vector PDF Ready]</i>" : "";
                sb.AppendLine($"• [{badge}] <b>{H(s.Name)}</b>{pdfBadge}");

                if (!string.IsNullOrEmpty(s.PdfUrn))
                {
                    buttons.Add(new() { ($"📄 Send Vector PDF: {Trunc(s.Name, 24)}", $"sendpdf:{i}") });
                }
            }

            var nav = new List<(string, string)>();
            if (page > 0) nav.Add(("◀ Prev 6", $"sheets:{page - 1}"));
            if (page + 1 < totalPages) nav.Add(("Next 6 ▶", $"sheets:{page + 1}"));
            if (nav.Count > 0) buttons.Add(nav);
            buttons.Add(new() { ("🏗 Switch Model", "models"), ("⬅ Main Menu", "menu") });

            return ToResult(sb.ToString(), buttons);
        }
        catch (Exception ex)
        {
            return ToResult($"⚠️ Could not query Cloud Manifest: {H(ex.Message)}", new() { new() { ("⬅ Main Menu", "menu") } });
        }
    }

    private async Task<BotExecutionResult> DownloadAndSendSheetPdfByNameAsync(TelegramUserSession session, string queryName)
    {
        if (string.IsNullOrWhiteSpace(queryName))
            return await BuildSheetsCardAsync(session, 0);

        var (_, _, sheets) = await FetchActiveModelSheetsAsync(session);
        var idx = sheets.FindIndex(s => s.Name.Contains(queryName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return ToResult($"❌ No 2D Sheet matching <code>{H(queryName)}</code> found. Use <code>/sheets</code> to list all sheets.",
                new() { new() { ("📐 List 2D Sheets", "sheets:0"), ("⬅ Main Menu", "menu") } });

        return await DownloadAndSendSheetPdfAsync(session, idx);
    }

    private async Task<BotExecutionResult> DownloadAndSendSheetPdfAsync(TelegramUserSession session, int sheetIndex)
    {
        var (urn, modelName, sheets) = await FetchActiveModelSheetsAsync(session);
        if (sheetIndex < 0 || sheetIndex >= sheets.Count)
            return ToResult("Invalid sheet index.", new() { new() { ("⬅ Main Menu", "menu") } });

        var sheet = sheets[sheetIndex];
        if (string.IsNullOrEmpty(sheet.PdfUrn))
            return ToResult($"Sheet <b>{H(sheet.Name)}</b> does not have a pre-generated Cloud Vector PDF derivative.",
                new() { new() { ("📐 Back to Sheets", "sheets:0") } });

        var token = await getApsAccessTokenAsync();
        var dlUrl = $"https://developer.api.autodesk.com/modelderivative/v2/designdata/{Uri.EscapeDataString(urn)}/manifest/{Uri.EscapeDataString(sheet.PdfUrn)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, dlUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await httpClient.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            return ToResult($"Failed to download PDF derivative ({(int)resp.StatusCode}).", new() { new() { ("⬅ Main Menu", "menu") } });

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var safeFile = Regex.Replace(sheet.Name, @"[^\w\-. ]+", "_") + ".pdf";
        var caption = $"📄 <b>{H(sheet.Name)}</b>\n📦 Model: {H(modelName)}\n✅ Native Autodesk Cloud Vector PDF ({bytes.Length / 1024.0:N1} KB)";

        return new BotExecutionResult(caption, StripHtml(caption), new() { new() { ("📐 More Sheets", "sheets:0"), ("⬅ Menu", "menu") } }, bytes, safeFile, "application/pdf");
    }

    // =========================================================================
    // 8. ON-DEMAND CSV ATTACHMENT GENERATOR (/csv)
    // =========================================================================
    private BotExecutionResult BuildCsvAttachment(TelegramUserSession session, string mode, string arg)
    {
        using var conn = OpenSessionDb(session);
        var (whereSql, sqlParams, _) = BuildScopeFilter(session, conn, mode == "cat" ? arg : null);

        var sbCsv = new StringBuilder();
        string fileName;

        if (mode == "issues")
        {
            var rows = QueryDb(conn, "SELECT model_id, element_id, discipline, severity, lifecycle_state, rule_id, category, family, type_name, parameter_name, actual_value, expected_rule FROM issues ORDER BY issue_id LIMIT 2500");
            sbCsv.AppendLine("ModelId,ElementId,Discipline,Severity,LifecycleState,RuleId,Category,Family,TypeName,ParameterName,ActualValue,ExpectedRule");
            foreach (var r in rows)
            {
                sbCsv.AppendLine($"{r["model_id"]},{r["element_id"]},{Csv(r["discipline"])},{Csv(r["severity"])},{Csv(r["lifecycle_state"])},{Csv(r["rule_id"])},{Csv(r["category"])},{Csv(r["family"])},{Csv(r["type_name"])},{Csv(r["parameter_name"])},{Csv(r["actual_value"])},{Csv(r["expected_rule"])}");
            }
            fileName = $"RBDE_Issues_{session.ActiveRunName}.csv";
        }
        else
        {
            var extra = "";
            if (mode == "search" && !string.IsNullOrWhiteSpace(arg))
            {
                extra = " AND (instr(lower(e.category),lower($kw))>0 OR instr(lower(e.family),lower($kw))>0 OR instr(lower(e.type_name),lower($kw))>0 OR instr(lower(e.name),lower($kw))>0)";
                sqlParams["$kw"] = arg;
            }

            var rows = QueryDb(conn, $@"
                SELECT e.model_id, e.element_id, e.category, e.family, e.type_name, e.name,
                       (SELECT p1.display_value FROM parameters p1 WHERE p1.model_id=e.model_id AND p1.element_id=e.element_id AND p1.name IN ('Level','Base Constraint','Reference Level') LIMIT 1) AS lvl,
                       (SELECT p2.display_value FROM parameters p2 WHERE p2.model_id=e.model_id AND p2.element_id=e.element_id AND p2.name='Area' LIMIT 1) AS area_val,
                       (SELECT p3.display_value FROM parameters p3 WHERE p3.model_id=e.model_id AND p3.element_id=e.element_id AND p3.name='Volume' LIMIT 1) AS vol_val,
                       (SELECT p4.display_value FROM parameters p4 WHERE p4.model_id=e.model_id AND p4.element_id=e.element_id AND p4.name='Length' LIMIT 1) AS len_val
                FROM elements e
                WHERE {whereSql} AND e.kind='instance'{extra}
                ORDER BY e.model_id, e.element_id
                LIMIT 3000", sqlParams);

            sbCsv.AppendLine("ModelId,ElementId,Category,Family,TypeName,Name,Level,Area,Volume,Length");
            foreach (var r in rows)
            {
                sbCsv.AppendLine($"{r["model_id"]},{r["element_id"]},{Csv(r["category"])},{Csv(r["family"])},{Csv(r["type_name"])},{Csv(r["name"])},{Csv(r["lvl"])},{Csv(r["area_val"])},{Csv(r["vol_val"])},{Csv(r["len_val"])}");
            }
            var tag = string.IsNullOrWhiteSpace(arg) ? "Scope" : Regex.Replace(arg, @"[^\w]+", "_");
            fileName = $"RBDE_QTO_{tag}.csv";
        }

        var bytes = new UTF8Encoding(true).GetBytes(sbCsv.ToString());
        var caption = $"📥 <b>Exported CSV Report:</b> <code>{H(fileName)}</code>\n🏢 Project: {H(session.ActiveProjectName)}";
        return new BotExecutionResult(caption, StripHtml(caption), new() { new() { ("⬅ Main Menu", "menu") } }, bytes, fileName, "text/csv");
    }

    private static string FormatCleanUnit(string paramName, string? rawUnit)
    {
        if (!string.IsNullOrWhiteSpace(rawUnit))
        {
            if (rawUnit.Contains("squareMeters", StringComparison.OrdinalIgnoreCase) || rawUnit == "m²" || rawUnit == "m^2") return "m²";
            if (rawUnit.Contains("cubicMeters", StringComparison.OrdinalIgnoreCase) || rawUnit == "m³" || rawUnit == "m^3") return "m³";
            if (rawUnit.Contains("millimeters", StringComparison.OrdinalIgnoreCase) || rawUnit == "mm") return "mm";
            if (rawUnit.Contains("meters", StringComparison.OrdinalIgnoreCase) || rawUnit == "m") return "m";
            if (rawUnit.Contains("squareFeet", StringComparison.OrdinalIgnoreCase) || rawUnit == "SF") return "SF";
            if (rawUnit.Contains("cubicFeet", StringComparison.OrdinalIgnoreCase) || rawUnit == "CF") return "CF";
            if (!rawUnit.StartsWith("autodesk.unit", StringComparison.OrdinalIgnoreCase)) return rawUnit;
        }
        var n = paramName.ToLowerInvariant();
        if (n.Contains("area")) return "m²";
        if (n.Contains("volume")) return "m³";
        if (n.Contains("length") || n.Contains("perimeter") || n.Contains("height") || n.Contains("width") || n.Contains("thickness") || n.Contains("elevation")) return "m";
        return "";
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Trunc(string? s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..(max - 1)] + "…");
    private static string Csv(object? o) => "\"" + (o?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
    private static string StripHtml(string html) => Regex.Replace(html, "<.*?>", "");

    private static BotExecutionResult ToResult(string html, List<List<(string Label, string CallbackData)>> buttons)
    {
        return new BotExecutionResult(html, StripHtml(html), buttons);
    }
}
