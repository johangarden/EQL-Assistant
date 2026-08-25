using EQLOverlay.Models;

namespace EQLOverlay.ViewModels;

/// <summary>Editable wrapper around a <see cref="RespawnEntry"/> for the Manager's repop page.</summary>
public sealed class RespawnViewModel : ViewModelBase
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(Display)); }
    }

    // Legacy passthrough only — typed times are retired; loading migrates
    // them into the first gap sample, so this is 0 everywhere post-load.
    private double _seconds;
    public double Seconds
    {
        get => _seconds;
        set { if (SetField(ref _seconds, value)) OnPropertyChanged(nameof(Display)); }
    }

    private string _zone = "";
    public string Zone
    {
        get => _zone;
        set { if (SetField(ref _zone, value)) OnPropertyChanged(nameof(ZoneGroup)); }
    }

    /// <summary>Group header for the Manager list (empty zones bucket together).</summary>
    public string ZoneGroup => string.IsNullOrWhiteSpace(Zone) ? "No zone set" : Zone.Trim();

    private string _pattern = "";
    public string Pattern { get => _pattern; set => SetField(ref _pattern, value); }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { if (SetField(ref _enabled, value)) OnPropertyChanged(nameof(Display)); }
    }

    public string Display => $"{(Enabled ? "" : "○ ")}{Name}  ·  "
        + (LearnedSeconds is { } l ? $"≤ {Services.DurationText.Compact(l)}" : "learning");

    /// <summary>The selected mob's one-line story: the estimate and where it
    /// came from (the editor's read-only header).</summary>
    public string EffectiveText => LearnedSeconds is { } l
        ? $"estimate ≤ {Services.DurationText.Compact(l)} — the shortest observed gap"
        : "learning — the first kill cycle becomes the estimate";

    // ---- the learner's evidence (death → next-appearance gaps) ----

    /// <summary>Observed gaps, newest first — carried through so a Manager
    /// save never throws away what the learner measured.</summary>
    public List<RespawnGap> Gaps { get; set; } = new();

    public double? LearnedSeconds => Gaps.Count > 0 ? Gaps.Min(g => g.Seconds) : null;

    public bool HasGaps => Gaps.Count > 0;

    /// <summary>The evidence list: every gap is an upper bound ("≤"), the
    /// MINIMUM is the estimate — and the line that holds it says so.</summary>
    public string GapsText
    {
        get
        {
            if (Gaps.Count == 0) return "";
            double min = Gaps.Min(g => g.Seconds);
            bool marked = false;
            return string.Join("\n", Gaps.Select(g =>
            {
                string line = $"≤ {Services.DurationText.Compact(g.Seconds)}   ·   {g.When:d MMM HH:mm}";
                if (!marked && g.Seconds == min)
                {
                    marked = true;
                    line += "   ◄ the estimate (shortest)";
                }
                return line;
            }));
        }
    }

    public void ClearGaps()
    {
        Gaps.Clear();
        OnPropertyChanged(nameof(GapsText));
        OnPropertyChanged(nameof(HasGaps));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(EffectiveText));
    }

    public static RespawnViewModel FromEntry(RespawnEntry e) => new()
    {
        Name = e.Name,
        Zone = e.Zone,
        Seconds = e.Seconds,
        Pattern = e.Pattern,
        Enabled = e.Enabled,
        Gaps = e.Gaps.Select(g => new RespawnGap { Seconds = g.Seconds, When = g.When }).ToList(),
    };

    public RespawnEntry ToEntry() => new()
    {
        Name = Name.Trim(),
        Zone = Zone.Trim(),
        Seconds = Seconds,
        Pattern = Pattern.Trim(),
        Enabled = Enabled,
        Gaps = Gaps.Select(g => new RespawnGap { Seconds = g.Seconds, When = g.When }).ToList(),
    };
}
