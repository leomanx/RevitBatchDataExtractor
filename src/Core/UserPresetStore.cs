using System.Text.Json;

namespace BatchExtractor;

public sealed record UserColumnPreset(string Name, List<string> Columns);

// Windows-user scoped, project-independent preferences shared by CloudApp and Dashboard.
public sealed class UserPresetStore
{
    private readonly string path;
    public UserPresetStore(string? pathOverride = null)
    {
        path = pathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMK_BIM", "RBDE", "column-presets.json");
    }
    public IReadOnlyList<UserColumnPreset> Load()
    {
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<UserColumnPreset>>(File.ReadAllText(path)) ?? [];
    }
    public IReadOnlyList<UserColumnPreset> Save(string name, IEnumerable<string> columns)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("Preset name must be 1–80 printable characters.");
        var selected = columns.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count is < 1 or > 35 || selected.Any(x => x.Length > 120 || x.Any(char.IsControl)))
            throw new ArgumentException("Choose 1–35 valid column names.");
        var list = Load().ToList();
        var existing = list.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) list[existing] = new UserColumnPreset(name, selected);
        else { if (list.Count >= 50) throw new InvalidOperationException("Maximum 50 personal presets."); list.Add(new UserColumnPreset(name, selected)); }
        Write(list); return list;
    }
    public IReadOnlyList<UserColumnPreset> Delete(string name)
    {
        var list = Load().Where(x => !x.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToList();
        Write(list); return list;
    }
    private void Write(List<UserColumnPreset> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(list,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temp,path,true);
    }
}
