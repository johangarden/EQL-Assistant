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
        string SeenBadge, bool CanBar, string GroupLabel);

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

        // The Learned-durations filter swaps the row list for the insights
        // table — same window, two presentations.
        bool learnedView = filter == "learned";
        DurScroll.Visibility = learnedView ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = learnedView ? Visibility.Collapsed : Visibility.Visible;
        if (learnedView) { RefreshDurTable(cls); return; }

        var hits = _library.Search(SearchBox?.Text ?? "", filter, cls);

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
        return new SpellRow(s, s.Name, string.Join("  ·  ", bits),
            _library.IsSeen(s) ? "● seen" : "",
            CanBar: s.Name.Length > 0, // junk landing text falls back to the begin-cast line
            level == 0 ? "NO LEVEL" : $"LEVEL {level}");
    }

    // ---- the learned-durations table (Companion-inspired insights) ------------

    private void RefreshDurTable(string cls)
    {
        DurTableHost.Children.Clear();
        DurTableHost.RowDefinitions.Clear();
        DurTableHost.ColumnDefinitions.Clear();

        string search = SearchBox?.Text.Trim() ?? "";
        var rows = (_durations?.Insights() ?? (IReadOnlyList<SpellDurations.DurationInsight>)
                Array.Empty<SpellDurations.DurationInsight>())
            .Where(r => search.Length == 0
                        || r.Spell.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(r => cls.Length == 0
                        || (_library.FindByBaseName(SpellDurations.BaseName(r.Spell))?.Classes
                            .Contains(cls, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        CountText.Text = $"{rows.Count} spell(s) with learned durations";

        if (rows.Count == 0)
        {
            DurTableHost.ColumnDefinitions.Add(new ColumnDefinition());
            DurTableHost.RowDefinitions.Add(new RowDefinition());
            DurTableHost.Children.Add(new TextBlock
            {
                Text = search.Length > 0 || cls.Length > 0
                    ? "No sampled spell matches the filters."
                    : "No samples yet — durations learn from your own cast → landing → fade cycles.",
                Foreground = DurDimFg,
                FontSize = 12,
                Margin = new Thickness(2, 6, 0, 0),
            });
            return;
        }

        string[] heads = { "SPELL", "ESTIMATE", "", "N", "MEDIAN", "IQR (P25–P75)", "MIN–MAX" };
        for (int i = 0; i < heads.Length; i++)
            DurTableHost.ColumnDefinitions.Add(new ColumnDefinition
            { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        int row = 0;
        DurTableHost.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < heads.Length; i++)
            DurCell(heads[i], row, i, DurHeadFg, right: i > 0, size: 9.5, bold: true);
        row++;

        string lastCat = "";
        foreach (var r in rows)
        {
            if (!r.Category.Equals(lastCat, StringComparison.OrdinalIgnoreCase))
            {
                lastCat = r.Category;
                DurTableHost.RowDefinitions.Add(new RowDefinition());
                var cat = new TextBlock
                {
                    Text = r.Category.ToUpperInvariant(),
                    Foreground = DurCatFg,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(2, 10, 0, 3),
                };
                Grid.SetRow(cat, row);
                Grid.SetColumnSpan(cat, heads.Length);
                DurTableHost.Children.Add(cat);
                row++;
            }

            DurTableHost.RowDefinitions.Add(new RowDefinition());
            DurCell(r.Spell, row, 0, DurNameFg, right: false, bold: true);
            DurCell(r.Estimate is { } est ? DurationText.Compact(est) : "—",
                row, 1, DurValFg, right: true);
            DurBadge(r.FromLog ? "log" : "db", row, 2, r.FromLog);
            DurCell(r.N.ToString(), row, 3, DurValFg, right: true);
            DurCell(DurationText.Compact(Math.Round(r.Median)), row, 4, DurValFg, right: true);
            DurCell($"{DurationText.Compact(Math.Round(r.P25))} – {DurationText.Compact(Math.Round(r.P75))}",
                row, 5, DurDimFg, right: true);
            DurCell($"{DurationText.Compact(Math.Round(r.Min))} – {DurationText.Compact(Math.Round(r.Max))}",
                row, 6, DurDimFg, right: true);
            row++;
        }
    }

    private static readonly System.Windows.Media.Brush DurHeadFg = FreezeBrush(0x5C, 0x6B, 0x82);
    private static readonly System.Windows.Media.Brush DurCatFg = FreezeBrush(0xE8, 0xC1, 0x5A);
    private static readonly System.Windows.Media.Brush DurNameFg = FreezeBrush(0x9F, 0xB4, 0xD0);
    private static readonly System.Windows.Media.Brush DurValFg = FreezeBrush(0xC9, 0xD4, 0xE3);
    private static readonly System.Windows.Media.Brush DurDimFg = FreezeBrush(0x7F, 0x93, 0xAD);
    private static readonly System.Windows.Media.Brush DurLine = FreezeBrush(0x1F, 0x26, 0x37);
    private static readonly System.Windows.Media.Brush DurLogFg = FreezeBrush(0xE8, 0xC1, 0x5A);
    private static readonly System.Windows.Media.Brush DurDbFg = FreezeBrush(0x4F, 0xC3, 0xF7);
    private static readonly System.Windows.Media.Brush DurBadgeBg = FreezeBrush(0x23, 0x2B, 0x40);

    private static System.Windows.Media.Brush FreezeBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void DurCell(string text, int row, int col, System.Windows.Media.Brush fg,
        bool right, double size = 12, bool bold = false)
    {
        var border = new Border
        {
            BorderBrush = DurLine,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(col == 0 ? 2 : 12, 4, col == 0 ? 12 : 2, 3),
            Child = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = fg,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            },
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        DurTableHost.Children.Add(border);
    }

    private void DurBadge(string text, int row, int col, bool log)
    {
        var host = new Border
        {
            BorderBrush = DurLine,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 4, 2, 3),
            Child = new Border
            {
                Background = DurBadgeBg,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(7, 0, 7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = log ? DurLogFg : DurDbFg,
                },
            },
        };
        Grid.SetRow(host, row);
        Grid.SetColumn(host, col);
        DurTableHost.Children.Add(host);
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
