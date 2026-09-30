using System.IO;
using System.Text.Json;

namespace EQLOverlay.Services;

/// <summary>
/// The Tools pages' small remembered choices (30 Sep) — the slot finder's
/// compare combo and slot, the focus planner's Need / Nice / Off marks — one
/// flat string per key in <c>tools-prefs.json</c>, keyed "<page>:<charKey>".
/// A convenience: every read tolerates a missing or broken file.
/// </summary>
public sealed class ToolPrefs
{
    private readonly string? _path;
    private readonly Dictionary<string, string> _all = new(StringComparer.Ordinal);

    public ToolPrefs(string? path)
    {
        _path = path;
        try
        {
            if (path is not null && File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } d)
                foreach (var kv in d) _all[kv.Key] = kv.Value;
        }
        catch { /* preferences are a convenience */ }
    }

    public string Get(string key) => _all.TryGetValue(key, out var v) ? v : "";

    public void Set(string key, string value)
    {
        _all[key] = value;
        try
        {
            if (_path is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
