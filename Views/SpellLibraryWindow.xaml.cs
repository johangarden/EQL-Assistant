using System.Windows;
using System.Windows.Controls;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The spell library window, one view (owner ruling): the learned-durations
/// insight table — estimate with its log/db source, sample count, median,
/// IQR and min–max per sampled spell — with a ＋ button per row that adds a
/// ready-made countdown bar trigger. The search box reaches the WHOLE
/// library: matches without samples join the table with the library's
/// duration and honest dashes, so any spell is one search from a trigger.
/// </summary>
public partial class SpellLibraryWindow : Window
{
    private const int MaxSearchRows = 120;

    private readonly SpellLibrary _library;
    private readonly Action<TriggerDefinition> _onAdd;
    private readonly SpellDurations? _durations;

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
        if (DurTableHost is null) return; // during InitializeComponent

        DurTableHost.Children.Clear();
        DurTableHost.RowDefinitions.Clear();
        DurTableHost.ColumnDefinitions.Clear();

        string search = SearchBox?.Text.Trim() ?? "";
        string cls = ClassBox?.SelectedIndex > 0 ? (string)ClassBox.SelectedItem : "";

        bool InClass(string name) =>
            cls.Length == 0
            || (_library.FindByBaseName(SpellDurations.BaseName(name))?.Classes
                .Contains(cls, StringComparison.OrdinalIgnoreCase) ?? false);

        var rows = (_durations?.Insights()
                    ?? (IReadOnlyList<SpellDurations.DurationInsight>)Array.Empty<SpellDurations.DurationInsight>())
            .Where(r => search.Length == 0
                        || r.Spell.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(r => InClass(r.Spell))
            .ToList();

        // The search reaches the whole library: unsampled matches join with
        // the library's duration and honest dashes.
        int unsampled = 0;
        if (search.Length > 0)
        {
            var have = new HashSet<string>(
                rows.Select(r => SpellDurations.BaseKey(r.Spell)), StringComparer.Ordinal);
            foreach (var s in _library.Search(search, "", cls).Take(MaxSearchRows))
            {
                if (!have.Add(SpellDurations.BaseKey(s.Name))) continue;
                double? lib = s.DurationSec > 0 ? s.DurationSec : null;
                rows.Add(new SpellDurations.DurationInsight(
                    s.Name, SpellLibrary.TriggerCategory(s), lib, FromLog: false,
                    0, 0, 0, 0, 0, 0, lib));
                unsampled++;
            }
            rows = rows.OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Spell, StringComparer.OrdinalIgnoreCase).ToList();
        }

        CountText.Text = search.Length > 0
            ? $"{rows.Count} match(es) · {rows.Count - unsampled} with samples"
            : $"{rows.Count} spell(s) with learned durations · {_library.SeenCount} seen in your log";

        if (rows.Count == 0)
        {
            DurTableHost.ColumnDefinitions.Add(new ColumnDefinition());
            DurTableHost.RowDefinitions.Add(new RowDefinition());
            DurTableHost.Children.Add(new TextBlock
            {
                Text = search.Length > 0 || cls.Length > 0
                    ? "No spell matches the filters."
                    : "No samples yet — durations learn from your own cast → landing → fade cycles. Search to reach the whole library.",
                Foreground = DurDimFg,
                FontSize = 12,
                Margin = new Thickness(2, 6, 0, 0),
            });
            return;
        }

        string[] heads = { "SPELL", "ESTIMATE", "", "N", "MEDIAN", "IQR (P25–P75)", "MIN–MAX", "" };
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

            bool sampled = r.N > 0;
            DurTableHost.RowDefinitions.Add(new RowDefinition());
            DurCell(r.Spell, row, 0, DurNameFg, right: false, bold: true);
            DurCell(r.Estimate is { } est ? DurationText.Compact(est) : "—",
                row, 1, DurValFg, right: true);
            DurBadge(r.Estimate is null ? "" : r.FromLog ? "log" : "db", row, 2, r.FromLog);
            DurCell(sampled ? r.N.ToString() : "—", row, 3, sampled ? DurValFg : DurDimFg, right: true);
            DurCell(sampled ? DurationText.Compact(Math.Round(r.Median)) : "—",
                row, 4, sampled ? DurValFg : DurDimFg, right: true);
            DurCell(sampled
                    ? $"{DurationText.Compact(Math.Round(r.P25))} – {DurationText.Compact(Math.Round(r.P75))}"
                    : "—", row, 5, DurDimFg, right: true);
            DurCell(sampled
                    ? $"{DurationText.Compact(Math.Round(r.Min))} – {DurationText.Compact(Math.Round(r.Max))}"
                    : "—", row, 6, DurDimFg, right: true);
            AddButton(r.Spell, row, heads.Length - 1);
            row++;
        }
    }

    /// <summary>The ＋ per row: the same ready-made bar the old browser added
    /// — type, color, duration and spoken fade warning prefilled.</summary>
    private void AddButton(string spellName, int row, int col)
    {
        var host = new Border
        {
            BorderBrush = DurLine,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 2, 2, 2),
        };
        var spell = _library.FindByBaseName(SpellDurations.BaseName(spellName));
        if (spell is not null)
        {
            // A themed pill, never stock chrome (house rule, day one).
            var btn = new Border
            {
                Background = DurBadgeBg,
                BorderBrush = FreezeBrush(0x3A, 0x45, 0x60),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 1, 11, 2),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Countdown bar with the right type, color and duration, plus a spoken fade warning — everything editable on the trigger afterwards",
                Child = new TextBlock
                {
                    Text = "＋ Add",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = DurLogFg,
                },
            };
            btn.MouseEnter += (_, _) => btn.BorderBrush = FreezeBrush(0x5A, 0x6B, 0x8C);
            btn.MouseLeave += (_, _) => btn.BorderBrush = FreezeBrush(0x3A, 0x45, 0x60);
            btn.MouseLeftButtonDown += (_, _) =>
            {
                if (SpellLibrary.BarTrigger(spell, spokenWarning: true) is not { } def) return;
                _onAdd(def);
                Close(); // it lands in the trigger list this window covers
            };
            host.Child = btn;
        }
        Grid.SetRow(host, row);
        Grid.SetColumn(host, col);
        DurTableHost.Children.Add(host);
    }

    // ---- table cells -----------------------------------------------------------

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
                VerticalAlignment = VerticalAlignment.Center,
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
        };
        if (text.Length > 0)
            host.Child = new Border
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
            };
        Grid.SetRow(host, row);
        Grid.SetColumn(host, col);
        DurTableHost.Children.Add(host);
    }
}
