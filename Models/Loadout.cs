using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EQLOverlay.Models;

/// <summary>
/// A named set of triggers for one class combo. Persisted as its own file in
/// %APPDATA%\EQLOverlay\loadouts\&lt;name&gt;.json.
/// </summary>
public sealed class Loadout
{
    public string Name { get; set; } = "Default";

    public List<TriggerDefinition> Triggers { get; set; } = new();

    /// <summary>Where this loadout was loaded from (not serialized).</summary>
    [JsonIgnore] public string? FilePath { get; set; }
}
