namespace EQLOverlay.Models;

/// <summary>
/// One named-mob respawn timer. Global (stored in respawns.json, not in a
/// loadout) — a mob's respawn time doesn't depend on your class combo.
/// </summary>
public sealed class RespawnEntry
{
    public string Name { get; set; } = "";

    /// <summary>Respawn time in seconds.</summary>
    public double Seconds { get; set; } = 400;

    /// <summary>
    /// Optional death-line regex. Empty = auto-match
    /// "&lt;Name&gt; has been slain by …" / "You have slain &lt;Name&gt;".
    /// </summary>
    public string Pattern { get; set; } = "";

    public bool Enabled { get; set; } = true;
}
