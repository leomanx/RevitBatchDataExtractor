using System;
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
using System.Threading.Tasks;

namespace RBDE.CloudApp;

public sealed record HubItem(string Id, string Name);
public sealed record ProjectItem(string Id, string Name);
public sealed record FolderItem(string Id, string Name);

public sealed class ModelFileItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionId { get; set; } = "";
    public int VersionNumber { get; set; } = 1;
    public string ViewName { get; set; } = "";
    public string DerivativeUrn { get; set; } = "";
    public bool IsSelected { get; set; } = true;
    public string Status { get; set; } = "Ready";
}

public sealed class CategoryOptionItem
{
    public string Name { get; set; } = "";
    public string Discipline { get; set; } = "";
    public bool IsSelected { get; set; } = true;
}

public sealed record ExtractedElement(
    long ElementId,
    int ObjectId,
    string UniqueId,
    string Kind,
    string Category,
    string Family,
    string TypeName,
    long TypeId,
    string Name);

public sealed record ExtractedParam(
    long ElementId,
    string Scope,
    string GroupName,
    string Name,
    string StorageType,
    string RawValue,
    string DisplayValue,
    string Unit);

public static class AecSchemaPresets
{
    public static readonly (string Name, string Discipline)[] StandardCategories =
    [
        // Architecture & Interior
        ("Walls", "Architecture"),
        ("Doors", "Architecture"),
        ("Windows", "Architecture"),
        ("Floors", "Architecture"),
        ("Ceilings", "Architecture"),
        ("Roofs", "Architecture"),
        ("Rooms", "Architecture"),
        ("Areas", "Architecture"),
        ("Curtain Panels", "Architecture"),
        ("Curtain Wall Mullions", "Architecture"),
        ("Curtain Systems", "Architecture"),
        ("Furniture", "Interior"),
        ("Furniture Systems", "Interior"),
        ("Casework", "Interior"),
        ("Specialty Equipment", "Interior"),
        ("Food Service Equipment", "Interior"),
        ("Medical Equipment", "Interior"),
        ("Railings", "Architecture"),
        ("Top Rails", "Architecture"),
        ("Stairs", "Architecture"),
        ("Runs", "Architecture"),
        ("Landings", "Architecture"),
        ("Ramps", "Architecture"),
        ("Columns", "Architecture"),
        ("Generic Models", "Architecture"),
        ("Mass", "Architecture"),
        ("Parking", "Site"),
        ("Site", "Site"),
        ("Topography", "Site"),
        ("Toposolid", "Site"),
        ("Planting", "Site"),

        // Structure
        ("Structural Columns", "Structure"),
        ("Structural Framing", "Structure"),
        ("Structural Foundations", "Structure"),
        ("Structural Connections", "Structure"),
        ("Structural Rebar", "Structure"),
        ("Structural Trusses", "Structure"),
        ("Structural Stiffeners", "Structure"),
        ("Slab Edges", "Structure"),

        // MEP (Mechanical, Electrical, Piping, Fire Protection)
        ("Mechanical Equipment", "MEP"),
        ("Ducts", "MEP"),
        ("Duct Fittings", "MEP"),
        ("Duct Accessories", "MEP"),
        ("Flex Ducts", "MEP"),
        ("Air Terminals", "MEP"),
        ("Pipes", "MEP"),
        ("Pipe Fittings", "MEP"),
        ("Pipe Accessories", "MEP"),
        ("Flex Pipes", "MEP"),
        ("Plumbing Fixtures", "MEP"),
        ("Sprinklers", "MEP"),
        ("Electrical Equipment", "MEP"),
        ("Electrical Fixtures", "MEP"),
        ("Lighting Fixtures", "MEP"),
        ("Lighting Devices", "MEP"),
        ("Cable Trays", "MEP"),
        ("Cable Tray Fittings", "MEP"),
        ("Conduits", "MEP"),
        ("Conduit Fittings", "MEP"),
        ("Fire Alarm Devices", "MEP"),
        ("Communication Devices", "MEP"),
        ("Data Devices", "MEP"),
        ("Security Devices", "MEP"),
        ("Nurse Call Devices", "MEP"),
        ("Telephone Devices", "MEP"),
        ("Spaces", "MEP"),
        ("Zones", "MEP")
    ];

    public static readonly Dictionary<string, string[]> ParameterGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Identity"] =
        [
            "Mark", "Type Mark", "Comments", "Type Comments", "Description",
            "Assembly Code", "Assembly Description", "Keynote", "Model", "Manufacturer",
            "Cost", "URL", "OmniClass Number", "OmniClass Title",
            "ClassificationCode", "Classification Description", "Uniclass",
            "IfcGUID", "Type IfcGUID", "IfcExportAs", "Code Name"
        ],
        ["Spatial"] =
        [
            "Level", "Base Constraint", "Reference Level", "Schedule Level",
            "Upper Limit", "Top Constraint", "Base Offset", "Top Offset",
            "Level Offset", "Elevation from Level", "Sill Height", "Head Height",
            "Room Name", "Room Number", "Number", "Name", "Department",
            "Occupancy", "Zone", "Host", "Location Line"
        ],
        ["Quantities"] =
        [
            "Area", "Volume", "Length", "Width", "Overall Width", "Height",
            "Unconnected Height", "Limit Offset", "Thickness", "Depth",
            "Perimeter", "Count", "Slope", "Angle", "Cut Length",
            "Weight", "Exact Weight"
        ],
        ["Materials"] =
        [
            "Material", "Structural Material", "Finish", "Base Finish",
            "Ceiling Finish", "Wall Finish", "Floor Finish", "Frame Material",
            "Panel Material", "Fire Rating", "Acoustic Rating", "Function",
            "Structural", "Structural Usage", "Thermal Mass", "Heat Transfer Coefficient (U)",
            "Thermal Resistance (R)", "Absorptance", "Roughness"
        ],
        ["MEP"] =
        [
            "System Type", "System Name", "System Classification", "System Abbreviation",
            "Service Type", "Size", "Overall Size", "Diameter", "Nominal Diameter",
            "Inside Diameter", "Outside Diameter", "Flow", "Static Pressure",
            "Velocity", "Friction", "Voltage", "Number of Poles", "Apparent Load",
            "True Load", "Power Factor", "Panel", "Circuit Number", "Electrical Data"
        ],
        ["Lifecycle"] =
        [
            "Workset", "Phase Created", "Phase Demolished", "Design Option",
            "Edited by", "Stage", "Status", "Construction Type"
        ]
    };

    public static readonly string[] DefaultPriorityColumns =
    [
        "Level", "Base Constraint", "Type Mark", "Mark", "Comments", "Type Comments",
        "Area", "Volume", "Length", "Width", "Height", "Thickness",
        "Material", "Structural Material", "Fire Rating", "Function",
        "Room Name", "Number", "Department", "System Name", "Size",
        "Assembly Code", "Keynote", "Workset", "Phase Created"
    ];
}

public sealed class ApsClient
{
    private const string BaseUrl = "https://developer.api.autodesk.com";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("RBDE.CloudApp.ApsSecret.v1");
    private static readonly string CredentialStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MMK_BIM", "RBDE", "credentials.dpapi.json");

    private static readonly Regex NumericUnitRegex = new(
        @"^\s*([+-]?\d+(?:\.\d+)?)\s*([a-zA-Z°%²³\^0-9/_-]+)?\s*$",
        RegexOptions.Compiled);

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private string? token;
    private DateTime tokenExpiry = DateTime.MinValue;
    private string? clientSecret;

    public string? ClientId { get; private set; }

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(clientSecret);

    public ApsClient()
    {
        ReloadCredentials();
    }

    public void ReloadCredentials()
    {
        token = null;
        tokenExpiry = DateTime.MinValue;

        // 1. Check DPAPI encrypted store first
        if (File.Exists(CredentialStorePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(CredentialStorePath));
                ClientId = doc.RootElement.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
                if (doc.RootElement.TryGetProperty("encryptedSecret", out var enc) && !string.IsNullOrWhiteSpace(enc.GetString()))
                {
                    var cipherBytes = Convert.FromBase64String(enc.GetString()!);
                    var plainBytes = ProtectedData.Unprotect(cipherBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
                    clientSecret = Encoding.UTF8.GetString(plainBytes);
                }
            }
            catch
            {
                // Fallback to environment variables if store is unreadable
            }
        }

        // 2. Check environment variables and auto-migrate plaintext User secret into DPAPI store
        var envId = Environment.GetEnvironmentVariable("APS_CLIENT_ID", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("APS_CLIENT_ID", EnvironmentVariableTarget.User);
        var userPlainSecret = Environment.GetEnvironmentVariable("APS_CLIENT_SECRET", EnvironmentVariableTarget.User);
        var procPlainSecret = Environment.GetEnvironmentVariable("APS_CLIENT_SECRET", EnvironmentVariableTarget.Process);

        if (string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(envId))
        {
            ClientId = envId.Trim();
        }

        var activePlainSecret = !string.IsNullOrWhiteSpace(userPlainSecret) ? userPlainSecret : procPlainSecret;
        if (!string.IsNullOrWhiteSpace(activePlainSecret))
        {
            clientSecret = activePlainSecret.Trim();
            if (!string.IsNullOrWhiteSpace(ClientId))
            {
                PersistEncryptedCredentials(ClientId, clientSecret);
            }
        }
    }

    public void SaveCredentials(string newClientId, string? newClientSecretIfChanged)
    {
        ClientId = newClientId.Trim();
        if (!string.IsNullOrWhiteSpace(newClientSecretIfChanged))
        {
            clientSecret = newClientSecretIfChanged.Trim();
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Client Secret is required.");
        }

        token = null;
        tokenExpiry = DateTime.MinValue;

        PersistEncryptedCredentials(ClientId, clientSecret);
    }

    private static void PersistEncryptedCredentials(string id, string secret)
    {
        var dir = Path.GetDirectoryName(CredentialStorePath)!;
        Directory.CreateDirectory(dir);

        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var cipherBytes = ProtectedData.Protect(plainBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        var payload = JsonSerializer.Serialize(new
        {
            clientId = id,
            encryptedSecret = Convert.ToBase64String(cipherBytes),
            protection = "Windows-DPAPI-CurrentUser",
            updatedUtc = DateTime.UtcNow.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(CredentialStorePath, payload, Encoding.UTF8);

        // Scrub any plaintext secret from Windows User Environment Variables
        if (Environment.GetEnvironmentVariable("APS_CLIENT_SECRET", EnvironmentVariableTarget.User) != null)
        {
            Environment.SetEnvironmentVariable("APS_CLIENT_SECRET", null, EnvironmentVariableTarget.User);
        }
        Environment.SetEnvironmentVariable("APS_CLIENT_SECRET", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("APS_CLIENT_ID", id, EnvironmentVariableTarget.User);
    }

    public async Task<string> EnsureTokenAsync()
    {
        if (token != null && DateTime.UtcNow < tokenExpiry) return token;
        if (!HasCredentials)
            throw new InvalidOperationException("APS credentials not configured. Please set Client ID and Client Secret in Settings.");

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{clientSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/authentication/v2/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "data:read viewables:read"
        });

        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Authentication failed (HTTP {(int)resp.StatusCode}): {errBody}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        token = doc.RootElement.GetProperty("access_token").GetString()!;
        var exp = doc.RootElement.GetProperty("expires_in").GetInt32();
        tokenExpiry = DateTime.UtcNow.AddSeconds(exp - 60);
        return token;
    }

    private async Task<JsonDocument> GetJsonAsync(string uri)
    {
        int maxAttempts = 4;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var tok = await EnsureTokenAsync();
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await http.SendAsync(req);

            if (resp.IsSuccessStatusCode)
            {
                return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            }

            bool isTransient = resp.StatusCode == HttpStatusCode.TooManyRequests ||
                               resp.StatusCode == HttpStatusCode.Accepted ||
                               (int)resp.StatusCode >= 500;

            if (isTransient && attempt < maxAttempts)
            {
                var delaySec = resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? Math.Pow(2, attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delaySec, 1, 15)));
                continue;
            }

            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"API request failed (HTTP {(int)resp.StatusCode}): {err}");
        }

        throw new HttpRequestException($"API request exceeded retry attempts: {uri}");
    }

    public async Task<List<HubItem>> GetHubsAsync()
    {
        var list = new List<HubItem>();
        using var hubsDoc = await GetJsonAsync($"{BaseUrl}/project/v1/hubs");
        if (hubsDoc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var h in data.EnumerateArray())
            {
                list.Add(new HubItem(
                    h.GetProperty("id").GetString()!,
                    h.GetProperty("attributes").GetProperty("name").GetString()!
                ));
            }
        }
        return list;
    }

    public async Task<List<ProjectItem>> GetProjectsAsync(string hubId)
    {
        var list = new List<ProjectItem>();
        using var projDoc = await GetJsonAsync($"{BaseUrl}/project/v1/hubs/{hubId}/projects");
        if (projDoc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var p in data.EnumerateArray())
            {
                list.Add(new ProjectItem(
                    p.GetProperty("id").GetString()!,
                    p.GetProperty("attributes").GetProperty("name").GetString()!
                ));
            }
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    public async Task<List<FolderItem>> GetFoldersAsync(string hubId, string projectId)
    {
        var list = new List<FolderItem>();
        using var topDoc = await GetJsonAsync($"{BaseUrl}/project/v1/hubs/{hubId}/projects/{projectId}/topFolders");
        if (!topDoc.RootElement.TryGetProperty("data", out var topData)) return list;

        var pf = topData.EnumerateArray()
            .FirstOrDefault(f => f.GetProperty("attributes").GetProperty("displayName").GetString() == "Project Files");

        if (pf.ValueKind == JsonValueKind.Undefined) return list;
        var pfId = pf.GetProperty("id").GetString()!;
        list.Add(new FolderItem(pfId, "Project Files (Root)"));

        await AppendSubfoldersRecursiveAsync(projectId, pfId, "", list, depth: 0, maxDepth: 2);
        return list;
    }

    private async Task AppendSubfoldersRecursiveAsync(
        string projectId,
        string folderId,
        string prefix,
        List<FolderItem> list,
        int depth,
        int maxDepth)
    {
        if (depth >= maxDepth) return;
        using var subDoc = await GetJsonAsync($"{BaseUrl}/data/v1/projects/{projectId}/folders/{folderId}/contents");
        if (!subDoc.RootElement.TryGetProperty("data", out var subData)) return;

        foreach (var item in subData.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "folders")
            {
                var subId = item.GetProperty("id").GetString()!;
                var name = item.GetProperty("attributes").GetProperty("displayName").GetString()!;
                var displayPath = string.IsNullOrEmpty(prefix) ? name : $"{prefix} / {name}";
                list.Add(new FolderItem(subId, displayPath));
                await AppendSubfoldersRecursiveAsync(projectId, subId, displayPath, list, depth + 1, maxDepth);
            }
        }
    }

    public async Task<List<ModelFileItem>> GetRvtModelsAsync(string projectId, string folderId)
    {
        var list = new List<ModelFileItem>();
        using var doc = await GetJsonAsync($"{BaseUrl}/data/v1/projects/{projectId}/folders/{folderId}/contents");
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;

        foreach (var item in data.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "items") continue;
            var name = item.GetProperty("attributes").GetProperty("displayName").GetString()!;
            if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) continue;

            var tipVersionId = item.GetProperty("relationships").GetProperty("tip").GetProperty("data").GetProperty("id").GetString()!;
            int vNum = 1;
            var vIdx = tipVersionId.IndexOf("version=", StringComparison.OrdinalIgnoreCase);
            if (vIdx >= 0 && int.TryParse(tipVersionId.AsSpan(vIdx + 8), out var parsedV))
                vNum = parsedV;

            list.Add(new ModelFileItem
            {
                Id = item.GetProperty("id").GetString()!,
                Name = $"{name} (v{vNum})",
                VersionId = tipVersionId,
                VersionNumber = vNum,
                IsSelected = true
            });
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    public async Task<(List<string> categories, List<string> parameterNames)> DiscoverModelSchemaAsync(
        string projectId,
        ModelFileItem model)
    {
        var escVersion = Uri.EscapeDataString(model.VersionId);
        using var vDoc = await GetJsonAsync($"{BaseUrl}/data/v1/projects/{projectId}/versions/{escVersion}");
        var urn = vDoc.RootElement.GetProperty("data").GetProperty("relationships").GetProperty("derivatives").GetProperty("data").GetProperty("id").GetString()!;

        using var metaDoc = await GetJsonAsync($"{BaseUrl}/modelderivative/v2/designdata/{urn}/metadata");
        var metaList = metaDoc.RootElement.GetProperty("data").GetProperty("metadata").EnumerateArray().ToList();
        var v3d = SelectBestView(metaList);
        var guid = v3d.GetProperty("guid").GetString()!;

        var treeTask = GetJsonAsync($"{BaseUrl}/modelderivative/v2/designdata/{urn}/metadata/{guid}");
        var propsTask = GetJsonAsync($"{BaseUrl}/modelderivative/v2/designdata/{urn}/metadata/{guid}/properties?forceget=true");
        await Task.WhenAll(treeTask, propsTask);

        using var treeDoc = await treeTask;
        using var propsDoc = await propsTask;

        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootModel = treeDoc.RootElement.GetProperty("data").GetProperty("objects")[0];
        if (rootModel.TryGetProperty("objects", out var catNodes))
        {
            foreach (var catNode in catNodes.EnumerateArray())
            {
                var catName = catNode.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(catName)) categories.Add(catName);
            }
        }

        var paramCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (propsDoc.RootElement.GetProperty("data").TryGetProperty("collection", out var col))
        {
            foreach (var item in col.EnumerateArray())
            {
                if (!item.TryGetProperty("properties", out var propGroups)) continue;
                foreach (var pGroup in propGroups.EnumerateObject())
                {
                    if (pGroup.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var p in pGroup.Value.EnumerateObject())
                    {
                        var val = p.Value.ToString();
                        if (string.IsNullOrWhiteSpace(val)) continue;
                        paramCounts[p.Name] = paramCounts.GetValueOrDefault(p.Name) + 1;
                    }
                }
            }
        }

        var sortedParams = paramCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => kv.Key)
            .ToList();

        return (categories.OrderBy(c => c).ToList(), sortedParams);
    }

    private static JsonElement SelectBestView(List<JsonElement> metaList)
    {
        var qaView = metaList.FirstOrDefault(m => m.GetProperty("role").GetString() == "3d" &&
            m.GetProperty("name").GetString()!.Contains("RBDE_QA_3D", StringComparison.OrdinalIgnoreCase));
        if (qaView.ValueKind != JsonValueKind.Undefined) return qaView;

        var v3d = metaList.FirstOrDefault(m => m.GetProperty("role").GetString() == "3d" &&
            (m.GetProperty("name").GetString()!.Contains("New Construction", StringComparison.OrdinalIgnoreCase) ||
             m.GetProperty("name").GetString()!.Contains("{3D}", StringComparison.OrdinalIgnoreCase)));
        if (v3d.ValueKind == JsonValueKind.Undefined)
            v3d = metaList.FirstOrDefault(m => m.GetProperty("role").GetString() == "3d");
        if (v3d.ValueKind == JsonValueKind.Undefined)
            v3d = metaList.First();
        return v3d;
    }

    public async Task<(List<ExtractedElement> elements, List<ExtractedParam> parameters)> ExtractModelAsync(
        string projectId,
        ModelFileItem model,
        bool extractAllCategories,
        HashSet<string> targetCategories,
        bool extractAllParameters,
        HashSet<string> targetParams,
        Action<string> log)
    {
        var elements = new List<ExtractedElement>();
        var parameters = new List<ExtractedParam>();

        // 1. Resolve derivatives URN
        var escVersion = Uri.EscapeDataString(model.VersionId);
        using var vDoc = await GetJsonAsync($"{BaseUrl}/data/v1/projects/{projectId}/versions/{escVersion}");
        var urn = vDoc.RootElement.GetProperty("data").GetProperty("relationships").GetProperty("derivatives").GetProperty("data").GetProperty("id").GetString()!;
        model.DerivativeUrn = urn;

        // 2. Select 3D View
        using var metaDoc = await GetJsonAsync($"{BaseUrl}/modelderivative/v2/designdata/{urn}/metadata");
        var metaList = metaDoc.RootElement.GetProperty("data").GetProperty("metadata").EnumerateArray().ToList();
        var v3d = SelectBestView(metaList);

        var guid = v3d.GetProperty("guid").GetString()!;
        var viewName = v3d.GetProperty("name").GetString() ?? "{3D}";
        model.ViewName = viewName;
        log($"[{model.Name}] Querying metadata view: '{viewName}' (v{model.VersionNumber})...");

        // 3. Fetch Tree & Properties in parallel
        var treeTask = GetJsonAsync($"{BaseUrl}/modelderivative/v2/designdata/{urn}/metadata/{guid}");
        var propsTask = GetJsonAsync($"{BaseUrl}/modelderivative/v2/designdata/{urn}/metadata/{guid}/properties?forceget=true");

        await Task.WhenAll(treeTask, propsTask);
        using var treeDoc = await treeTask;
        using var propsDoc = await propsTask;

        // 4. Build Category -> Family -> Type -> Instance Map
        var objMeta = new Dictionary<int, (string cat, string fam, string typ, string kind, int parentTypeObjId)>();
        var rootModel = treeDoc.RootElement.GetProperty("data").GetProperty("objects")[0];

        if (rootModel.TryGetProperty("objects", out var catNodes))
        {
            foreach (var catNode in catNodes.EnumerateArray())
            {
                var catName = catNode.GetProperty("name").GetString()!;
                if (!extractAllCategories && !targetCategories.Contains(catName)) continue;

                if (!catNode.TryGetProperty("objects", out var famNodes)) continue;
                foreach (var famNode in famNodes.EnumerateArray())
                {
                    var famName = famNode.GetProperty("name").GetString()!;
                    if (!famNode.TryGetProperty("objects", out var typeNodes)) continue;

                    foreach (var typeNode in typeNodes.EnumerateArray())
                    {
                        var typeName = typeNode.GetProperty("name").GetString()!;
                        var typeObjId = typeNode.GetProperty("objectid").GetInt32();
                        objMeta[typeObjId] = (catName, famName, typeName, "type", typeObjId);

                        if (!typeNode.TryGetProperty("objects", out var instNodes)) continue;
                        foreach (var instNode in instNodes.EnumerateArray())
                        {
                            var instObjId = instNode.GetProperty("objectid").GetInt32();
                            objMeta[instObjId] = (catName, famName, typeName, "instance", typeObjId);
                        }
                    }
                }
            }
        }

        // 5. First pass on Properties: collect Type node properties & Revit IDs so instances inherit Type parameters
        var objToRevitId = new Dictionary<int, long>();
        var typeObjParams = new Dictionary<int, List<(string groupName, string name, string storageType, string rawVal, string displayVal, string unit)>>();

        if (propsDoc.RootElement.GetProperty("data").TryGetProperty("collection", out var col))
        {
            foreach (var item in col.EnumerateArray())
            {
                var id = item.GetProperty("objectid").GetInt32();
                if (!objMeta.TryGetValue(id, out var meta)) continue;

                var rawName = item.GetProperty("name").GetString()!;
                long revitId = ParseRevitElementId(rawName, id);
                objToRevitId[id] = revitId;

                if (meta.kind == "type" && item.TryGetProperty("properties", out var tPropGroups))
                {
                    var list = new List<(string, string, string, string, string, string)>();
                    foreach (var pGroup in tPropGroups.EnumerateObject())
                    {
                        if (pGroup.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var p in pGroup.Value.EnumerateObject())
                        {
                            if (!extractAllParameters && !targetParams.Contains(p.Name)) continue;
                            var val = p.Value.ToString();
                            if (string.IsNullOrWhiteSpace(val)) continue;

                            var (storageType, rawVal, unit) = NormalizeValue(p.Value, val);
                            list.Add((pGroup.Name, p.Name, storageType, rawVal, val, unit));
                        }
                    }
                    typeObjParams[id] = list;
                }
            }

            // 6. Second pass: emit Elements and both Instance + inherited Type Parameters
            foreach (var item in col.EnumerateArray())
            {
                var id = item.GetProperty("objectid").GetInt32();
                if (!objMeta.TryGetValue(id, out var meta)) continue;

                var rawName = item.GetProperty("name").GetString()!;
                var extId = item.TryGetProperty("externalId", out var eid) ? eid.GetString() ?? "" : "";
                var revitId = objToRevitId.GetValueOrDefault(id, id);
                var parentTypeRevitId = objToRevitId.GetValueOrDefault(meta.parentTypeObjId, -1L);

                elements.Add(new ExtractedElement(
                    revitId,
                    id,
                    extId,
                    meta.kind,
                    meta.cat,
                    meta.fam,
                    meta.typ,
                    meta.kind == "instance" ? parentTypeRevitId : revitId,
                    rawName));

                var addedParamsForElement = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (item.TryGetProperty("properties", out var propGroups))
                {
                    foreach (var pGroup in propGroups.EnumerateObject())
                    {
                        if (pGroup.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var p in pGroup.Value.EnumerateObject())
                        {
                            if (!extractAllParameters && !targetParams.Contains(p.Name)) continue;
                            var val = p.Value.ToString();
                            if (string.IsNullOrWhiteSpace(val)) continue;

                            var scope = meta.kind == "type" || p.Name.StartsWith("Type ", StringComparison.OrdinalIgnoreCase)
                                ? "type"
                                : "instance";

                            var (storageType, rawVal, unit) = NormalizeValue(p.Value, val);
                            parameters.Add(new ExtractedParam(revitId, scope, pGroup.Name, p.Name, storageType, rawVal, val, unit));
                            addedParamsForElement.Add(p.Name);
                        }
                    }
                }

                // Propagate Type parameters onto Instances if not already present on the Instance
                if (meta.kind == "instance" && typeObjParams.TryGetValue(meta.parentTypeObjId, out var inheritedList))
                {
                    foreach (var tp in inheritedList)
                    {
                        if (addedParamsForElement.Contains(tp.name)) continue;
                        parameters.Add(new ExtractedParam(revitId, "type", tp.groupName, tp.name, tp.storageType, tp.rawVal, tp.displayVal, tp.unit));
                        addedParamsForElement.Add(tp.name);
                    }
                }
            }
        }

        log($"[{model.Name}] Extracted {elements.Count:N0} elements and {parameters.Count:N0} parameter records.");
        return (elements, parameters);
    }

    private static long ParseRevitElementId(string rawName, int fallbackObjectId)
    {
        var b1 = rawName.LastIndexOf('[');
        var b2 = rawName.LastIndexOf(']');
        if (b1 >= 0 && b2 > b1)
        {
            if (long.TryParse(rawName.AsSpan(b1 + 1, b2 - b1 - 1), out var lid))
                return lid;
        }
        return fallbackObjectId;
    }

    private static (string storageType, string rawValue, string unit) NormalizeValue(JsonElement jsonVal, string displayVal)
    {
        if (jsonVal.ValueKind == JsonValueKind.Number)
        {
            if (jsonVal.TryGetInt64(out var lVal))
                return ("Integer", lVal.ToString(CultureInfo.InvariantCulture), "");
            if (jsonVal.TryGetDouble(out var dVal))
                return ("Double", dVal.ToString("G", CultureInfo.InvariantCulture), "");
        }
        else if (jsonVal.ValueKind == JsonValueKind.True || jsonVal.ValueKind == JsonValueKind.False)
        {
            return ("Integer", jsonVal.GetBoolean() ? "1" : "0", "");
        }

        var match = NumericUnitRegex.Match(displayVal);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNum))
        {
            var unit = match.Groups[2].Success ? match.Groups[2].Value : "";
            var isInt = !match.Groups[1].Value.Contains('.') && string.IsNullOrEmpty(unit);
            return (isInt ? "Integer" : "Double", parsedNum.ToString("G", CultureInfo.InvariantCulture), unit);
        }

        return ("String", displayVal, "");
    }
}
