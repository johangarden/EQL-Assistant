using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Browse the prebaked spell library and add ready-made triggers with one
/// click: every add is a bar with a default spoken fade warning (voice can be
/// toggled off on the trigger afterwards). Rows are grouped by level —
/// the filtered class's level when one is picked, else the lowest class
/// level — alphabetical inside each level. Owned by the Manager; additions
/// land in the current loadout.
/// </summary>
public partial class SpellLibraryWindow : Window
{
    private const int MaxRows = 250;

    private readonly SpellLibrary _library;
    private readonly Action<TriggerDefinition> _onAdd;
    private readonly SpellDurations? _durations;

    public sealed record SpellRow(SpellLibrary.Spell Spell, string Name, string Detail,
        string SeenBadge, bool CanBar, string GroupLabel, string LearnedText = "")
    {
        public Visibility LearnedVisibility =>
            LearnedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly Regex ClassLevelRx = new(
        @"(?<c>[A-Z]{2,3}) (?<l>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SpellLibraryWindow(SpellLibrary library, Action<TriggerDefinition> onAdd,
        SpellDurations? durations = null)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _library = library;
        _onAdd = onAdd;
        _durations = durations;

        ClassBox.ItemsSource = new[]
        {
            "All classes", "WAR", "CLR", "PAL", "RNG", "SHD", "DRU", "MNK", "BRD",
            "ROG", "SHM", "NEC", "WIZ", "MAG", "ENC", "BST", "BER",
        };
        ClassBox.SelectedIndex = 0;

        Refresh();
    }

    private void Filters_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (ResultsList is null) return; // during InitializeComponent

        string filter = FilterBox?.SelectedValue as string ?? "";
        string cls = ClassBox?.SelectedIndex > 0 ? (string)ClassBox.SelectedItem : "";
        // "Learned durations" is the learner's filter, not the library's.
        string libFilter = filter == "learned" ? "" : filter;
        var hits = _library.Search(SearchBox?.Text ?? "", libFilter, cls);
        if (filter == "learned")
            hits = hits.Where(s => _durations?.SampleCount(s.Name) > 0).ToList();

        var rows = hits
            .Select(s => (Spell: s, Level: LevelOf(s, cls)))
            .OrderBy(x => x.Level == 0 ? int.MaxValue : x.Level) // level-less last
            .ThenBy(x => x.Spell.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRows)
            .Select(x => ToRow(x.Spell, x.Level))
            .ToList();

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SpellRow.GroupLabel)));
        ResultsList.ItemsSource = view;

        CountText.Text = hits.Count > MaxRows
            ? $"{hits.Count} matches (showing {MaxRows}) · {_library.SeenCount} seen in your log"
            : $"{hits.Count} of {_library.Spells.Count} spells · {_library.SeenCount} seen in your log";
    }

    /// <summary>The level a spell sorts under: the filtered class's own level
    /// when a class is picked, else the lowest level any class gets it at.
    /// 0 = the classes string carries no numbers.</summary>
    private static int LevelOf(SpellLibrary.Spell s, string cls)
    {
        int best = 0;
        foreach (Match m in ClassLevelRx.Matches(s.Classes))
        {
            int lvl = int.Parse(m.Groups["l"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            if (cls.Length > 0)
            {
                if (m.Groups["c"].Value == cls) return lvl;
            }
            else if (best == 0 || lvl < best)
            {
                best = lvl;
            }
        }
        return cls.Length > 0 ? 0 : best;
    }

    private SpellRow ToRow(SpellLibrary.Spell s, int level)
    {
        var bits = new List<string>();
        if (s.Classes.Length > 0) bits.Add(s.Classes);
        if (s.DurationSec > 0) bits.Add(FormatDuration(s.DurationSec));
        bits.Add(s.Illusion ? "Illusion" : s.Type.Length > 0 ? s.Type : s.Bucket);

        // The learner's story rides the row (Companion-inspired): the estimate
        // the bars use with its source, then the observed spread — a wide IQR
        // usually means polluted cycles.
        string learned = "";
        if (_durations?.InsightFor(s.Name) is { } ins)
            learned = $"learned {(ins.Estimate is { } est ? DurationText.Compact(est) : "—")}"
                + $" ({(ins.FromLog ? "log" : "db")}) · {ins.N} sample{(ins.N == 1 ? "" : "s")}"
                + $" · median {DurationText.Compact(ins.Median)}"
                + $" · IQR {DurationText.Compact(ins.P25)}–{DurationText.Compact(ins.P75)}"
                + $" · {DurationText.Compact(ins.Min)}–{DurationText.Compact(ins.Max)}";

        return new SpellRow(s, s.Name, string.Join("  ·  ", bits),
            _library.IsSeen(s) ? "● seen" : "",
            CanBar: s.Name.Length > 0, // junk landing text falls back to the begin-cast line
            level == 0 ? "NO LEVEL" : $"LEVEL {level}",
            learned);
    }

    private static string FormatDuration(double sec) =>
        sec >= 3600 ? $"{sec / 3600:0.#}h" : sec >= 60 ? $"{sec / 60:0} min" : $"{sec:0}s";

    /// <summary>The one add: a bar with the default spoken fade warning. The
    /// window closes with it — it covers the trigger list, so staying open
    /// hides where the pick just landed; re-open per trigger instead.</summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpellRow row) return;
        var def = SpellLibrary.BarTrigger(row.Spell, spokenWarning: true);
        if (def is null) return;
        _onAdd(def);
        Close();
    }
}
