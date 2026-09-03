using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Best-in-slot tab: class-combo chips (prefilled from /who), three
/// weighted priorities, the storages to search — and per slot the top
/// candidates you OWN, with WORN / UPGRADE badges, where each sits, and the
/// items the combo can't wear kept visible but unranked. All scoring in
/// <see cref="BisFinder"/>.
/// </summary>
public partial class BisFinderView : UserControl
{
    private ItemStats _stats = null!;
    private ConfigService? _config;
    private string _charKey = "";
    private List<InventoryStore.CarryRow> _rows = new();
    private string _dumpStamp = "";
    private bool _building;

    private readonly List<string> _combo = new(); // insertion order: the 4th pick evicts the 1st
    private string[] _prio = { "AC", "STA", "INT" };
    private readonly HashSet<string> _lanes = new(BisFinder.SearchLanes, StringComparer.Ordinal);

    private static readonly Brush ChipOnBg = Freeze("#16283E");
    private static readonly Brush ChipOnFg = Freeze("#4FC3F7");
    private static readonly Brush ChipOnLine = Freeze("#4FC3F7");
    private static readonly Brush LaneOnFg = Freeze("#E8C15A");
    private static readonly Brush LaneOnLine = Freeze("#5A6B8C");
    private static readonly Brush ChipOffBg = Freeze("#232B40");
    private static readonly Brush ChipOffFg = Freeze("#7F93AD");
    private static readonly Brush ChipOffLine = Freeze("#3A4560");
    private static readonly Brush HeadFg = Freeze("#5C6B82");
    private static readonly Brush SlotFg = Freeze("#E8C15A");
    private static readonly Brush NameFg = Freeze("#C9D4E3");
    private static readonly Brush DimFg = Freeze("#7F93AD");
    private static readonly Brush DimmerFg = Freeze("#5C6B82");
    private static readonly Brush LineBrush = Freeze("#1F2637");
    private static readonly Brush ScoreFg = Freeze("#81C784");
    private static readonly Brush TierFg = Freeze("#4FC3F7");
    private static readonly Brush Stat1Fg = Freeze("#E8C15A");
    private static readonly Brush UpFg = Freeze("#FFA85C");
    private static readonly Brush WornFg = Freeze("#81C784");
    private static readonly Brush ForeignFg = Freeze("#E57373");
    private static readonly Brush BadgeBg = Freeze("#232B40");

    public BisFinderView()
    {
        InitializeComponent();
        foreach (var box in new[] { P1, P2, P3 })
            box.ItemsSource = BisFinder.Priorities.Select(p => p.Label).ToList();
        BuildClassChips();
        BuildLaneChips();
    }

    public void Init(ItemStats stats, ConfigService? config, string charKey)
    {
        _stats = stats;
        _config = config;
        _charKey = charKey;
        var (classes, prio) = _config?.LoadBisPrefs(_charKey) ?? ("", "");
        if (classes.Length > 0)
        {
            _combo.Clear();
            _combo.AddRange(classes.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3));
        }
        if (prio.Length > 0)
        {
            var p = prio.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 3) _prio = p;
        }
        SyncPrioBoxes();
    }

    /// <summary>New dump rows (and the /who combo to prefill when nothing
    /// was chosen yet).</summary>
    public void Update(List<InventoryStore.CarryRow> rows, string whoClasses, string dumpStamp)
    {
        _rows = rows;
        _dumpStamp = dumpStamp;
        if (_combo.Count == 0 && whoClasses.Length > 0)
            _combo.AddRange(whoClasses.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3));
        Refresh();
    }

    // ---- controls ---------------------------------------------------------------

    private void BuildClassChips()
    {
        ClassChips.Children.Clear();
        foreach (var cls in BisFinder.AllClasses)
        {
            var chip = Chip(cls, cls);
            chip.MouseLeftButtonDown += (_, _) =>
            {
                if (!_combo.Remove(cls))
                {
                    _combo.Add(cls);
                    while (_combo.Count > 3) _combo.RemoveAt(0);
                }
                SavePrefs();
                Refresh();
            };
            ClassChips.Children.Add(chip);
        }
    }

    private void BuildLaneChips()
    {
        LaneChips.Children.Clear();
        foreach (var lane in BisFinder.SearchLanes)
        {
            var chip = Chip(InventoryStore.LaneLabels.GetValueOrDefault(lane, lane), lane);
            chip.MouseLeftButtonDown += (_, _) =>
            {
                if (!_lanes.Add(lane)) _lanes.Remove(lane);
                Refresh();
            };
            LaneChips.Children.Add(chip);
        }
    }

    private static Border Chip(string text, string tag) => new()
    {
        Tag = tag,
        CornerRadius = new CornerRadius(9),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(9, 2, 9, 3),
        Margin = new Thickness(0, 0, 5, 5),
        Cursor = System.Windows.Input.Cursors.Hand,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold },
    };

    private static void Paint(Border chip, bool on, Brush onFg, Brush onLine)
    {
        chip.Background = on ? ChipOnBg : ChipOffBg;
        chip.BorderBrush = on ? onLine : ChipOffLine;
        if (chip.Child is TextBlock tb) tb.Foreground = on ? onFg : ChipOffFg;
    }

    private void SyncPrioBoxes()
    {
        _building = true;
        var boxes = new[] { P1, P2, P3 };
        for (int i = 0; i < 3; i++)
        {
            int ix = Array.FindIndex(BisFinder.Priorities, p => p.Key == _prio[i]);
            boxes[i].SelectedIndex = ix < 0 ? 0 : ix;
        }
        _building = false;
    }

    private void Prio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        var boxes = new[] { P1, P2, P3 };
        for (int i = 0; i < 3; i++)
            if (boxes[i].SelectedIndex >= 0)
                _prio[i] = BisFinder.Priorities[boxes[i].SelectedIndex].Key;
        SavePrefs();
        Refresh();
    }

    private void SavePrefs() =>
        _config?.SaveBisPrefs(_charKey, string.Join("/", _combo), string.Join("/", _prio));

    // ---- the board -----------------------------------------------------------------

    private void Refresh()
    {
        if (_stats is null || Board is null) return;

        foreach (var child in ClassChips.Children)
            if (child is Border c && c.Tag is string cls)
                Paint(c, _combo.Contains(cls), ChipOnFg, ChipOnLine);
        foreach (var child in LaneChips.Children)
            if (child is Border c && c.Tag is string lane)
                Paint(c, _lanes.Contains(lane), LaneOnFg, LaneOnLine);
        ClassHint.Text = _combo.Count == 0
            ? "No combo picked — every wearable item counts. Run /who in game to prefill yours."
            : $"Ranking for {string.Join("/", _combo)} — an item counts when ANY of these classes may wear it."
              + (_dumpStamp.Length > 0 ? $"  Snapshot {_dumpStamp}." : "");

        var result = BisFinder.Build(_rows, _stats, _combo, _prio, _lanes);
        BuildVerdicts(result);
        BuildBoard(result);
    }

    private void BuildVerdicts(BisFinder.Result result)
    {
        Verdicts.Children.Clear();
        string PrioText() => string.Join(" → ", _prio.Select(Label));

        var upgrades = result.Slots
            .SelectMany(s => s.Upgrades.Select(u => (Slot: s, Pick: u)))
            .ToList();
        if (upgrades.Count > 0)
        {
            var slotNames = upgrades.Select(u => u.Slot.Label).Distinct().ToList();
            Verdict($"{upgrades.Count} upgrade(s) sitting in storage under {PrioText()}: "
                    + string.Join(", ", slotNames) + ".", caveat: false);
            // The biggest jump: pick score minus the weakest worn score in its slot.
            var best = upgrades
                .Select(u =>
                {
                    double worn = u.Slot.Ranked.Where(c => c.Worn).Select(c => c.Score)
                        .DefaultIfEmpty(0).Min();
                    return (u.Slot, u.Pick, Gain: u.Pick.Score - worn);
                })
                .OrderByDescending(x => x.Gain).First();
            var wornItem = best.Slot.Ranked.Where(c => c.Worn).OrderBy(c => c.Score).FirstOrDefault();
            Verdict($"Biggest jump: {best.Pick.Name} ({best.Pick.Location}) "
                    + (wornItem is null
                        ? $"fills your empty {best.Slot.Label} slot — score {best.Pick.Score:0}."
                        : $"beats your worn {wornItem.Name} by {best.Gain:0} points in {best.Slot.Label}."),
                caveat: false);
        }
        else if (result.Considered > 0)
        {
            Verdict($"Nothing in storage beats what you wear under {PrioText()} — you're wearing your best.", caveat: false);
        }
        else
        {
            Verdict("No wearable items in the searched storages (or no dump yet).", caveat: true);
        }

        if (result.Unknown.Count > 0)
            Verdict($"{result.Unknown.Count} item(s) skipped — no wiki entry yet, stats unknown, not ranked: "
                    + string.Join(", ", result.Unknown.Take(4))
                    + (result.Unknown.Count > 4 ? ", …" : "") + ".", caveat: true);
    }

    private void Verdict(string text, bool caveat)
    {
        var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        line.Inlines.Add(new Run(caveat ? "◇ " : "◆ ") { Foreground = caveat ? DimmerFg : SlotFg, FontSize = 9 });
        line.Inlines.Add(new Run(text) { Foreground = caveat ? DimFg : NameFg });
        Verdicts.Children.Add(line);
    }

    private static string Label(string key) =>
        Array.Find(BisFinder.Priorities, p => p.Key == key).Label is { Length: > 0 } l ? l : key;

    private void BuildBoard(BisFinder.Result result)
    {
        Board.Children.Clear();
        Board.RowDefinitions.Clear();
        Board.ColumnDefinitions.Clear();

        // ITEM · SCORE · p1 · p2 · p3 · OTHER · WHERE · CLASSES
        Board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++)
            Board.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;
        Board.RowDefinitions.Add(new RowDefinition());
        Cell("ITEM", row, 0, HeadFg, right: false, size: 9.5, bold: true);
        Cell("SCORE", row, 1, HeadFg, right: true, size: 9.5, bold: true);
        for (int i = 0; i < 3; i++)
            Cell(Label(_prio[i]).ToUpperInvariant(), row, 2 + i, HeadFg, right: true, size: 9.5, bold: true);
        Cell("OTHER", row, 5, HeadFg, right: false, size: 9.5, bold: true);
        Cell("WHERE", row, 6, HeadFg, right: false, size: 9.5, bold: true);
        Cell("CLASSES", row, 7, HeadFg, right: false, size: 9.5, bold: true);
        row++;

        foreach (var slot in result.Slots)
        {
            if (slot.Ranked.Count == 0 && slot.Foreign.Count == 0) continue;

            Board.RowDefinitions.Add(new RowDefinition());
            var head = new TextBlock
            {
                Text = slot.Label.ToUpperInvariant() + (slot.Count > 1 ? " · TWO SLOTS" : ""),
                Foreground = SlotFg, FontSize = 10.5, FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 12, 0, 3),
            };
            Grid.SetRow(head, row);
            Grid.SetColumnSpan(head, 8);
            Board.Children.Add(head);
            row++;

            var upgrades = slot.Upgrades.ToHashSet();
            // Top 3 by score, plus anything worn that fell outside them — you
            // should always see what you're wearing against the winner.
            var shown = slot.Ranked.Take(3)
                .Concat(slot.Ranked.Skip(3).Where(c => c.Worn))
                .ToList();
            foreach (var c in shown)
            {
                Board.RowDefinitions.Add(new RowDefinition());
                bool pick = slot.Picks.Contains(c);
                var name = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                name.Inlines.Add(new Run(c.BaseName) { Foreground = pick ? NameFg : DimFg, FontWeight = FontWeights.SemiBold });
                if (c.Tier > 0) name.Inlines.Add(new Run($" +{c.Tier}") { Foreground = TierFg });
                if (c.Copies > 1) name.Inlines.Add(new Run($" ×{c.Copies}") { Foreground = DimmerFg });
                if (c.Worn) Badge(name, "WORN", WornFg);
                if (upgrades.Contains(c)) Badge(name, "UPGRADE", UpFg);
                if (c.ClassesUnknown) Badge(name, "CLASSES UNKNOWN", DimmerFg);
                CellHost(name, row, 0);
                Cell($"{c.Score:0}", row, 1, pick ? ScoreFg : DimFg, right: true, size: 13, bold: pick);
                for (int i = 0; i < 3; i++)
                {
                    int v = c.Stats.GetValueOrDefault(_prio[i]);
                    Cell(v != 0 ? StatText(_prio[i], v) : "—", row, 2 + i,
                        v != 0 ? (i == 0 ? Stat1Fg : NameFg) : DimmerFg, right: true);
                }
                Cell(OtherText(c), row, 5, DimFg, right: false, size: 11);
                Cell(c.Location, row, 6, DimFg, right: false, size: 11);
                Cell(c.Rec.Classes.Length > 0 ? c.Rec.Classes : "—", row, 7, DimmerFg, right: false, size: 10.5);
                row++;
            }

            // The best item the combo CAN'T wear — visible, unranked, so the
            // bank's robe isn't forgotten when the combo changes.
            foreach (var f in slot.Foreign.Take(1))
            {
                Board.RowDefinitions.Add(new RowDefinition());
                var name = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.7 };
                name.Inlines.Add(new Run(f.BaseName) { Foreground = DimFg, FontWeight = FontWeights.SemiBold });
                if (f.Tier > 0) name.Inlines.Add(new Run($" +{f.Tier}") { Foreground = TierFg });
                if (f.Worn) Badge(name, "WORN", WornFg);
                CellHost(name, row, 0);
                Cell("—", row, 1, DimmerFg, right: true);
                for (int i = 0; i < 3; i++)
                {
                    int v = f.Stats.GetValueOrDefault(_prio[i]);
                    Cell(v != 0 ? StatText(_prio[i], v) : "—", row, 2 + i, DimmerFg, right: true);
                }
                Cell(OtherText(f), row, 5, DimmerFg, right: false, size: 11);
                Cell(f.Location, row, 6, DimmerFg, right: false, size: 11);
                Cell(f.Rec.Classes + " — not this combo", row, 7, ForeignFg, right: false, size: 10.5);
                row++;
            }
        }
    }

    private static string StatText(string key, int v) => key switch
    {
        "AC" or "DMG_DLY" => v.ToString(),
        "HASTE" => v + "%",
        _ => (v > 0 ? "+" : "") + v,
    };

    /// <summary>Everything the item grants that isn't a priority column —
    /// the three biggest, weapons leading with DMG/DLY, 2H called out.</summary>
    private string OtherText(BisFinder.Candidate c)
    {
        var parts = new List<string>();
        if (c.Stats.TryGetValue("DMG", out int dmg) && c.Stats.TryGetValue("DELAY", out int dly))
            parts.Add($"{dmg}/{dly}");
        if (c.TwoHanded) parts.Add("2H");
        var skip = new HashSet<string>(_prio, StringComparer.Ordinal)
            { "DMG", "DELAY", "DMG_DLY", "RESISTS" };
        parts.AddRange(c.Stats
            .Where(kv => !skip.Contains(kv.Key) && kv.Value != 0)
            .OrderByDescending(kv => Math.Abs(kv.Value))
            .Take(3)
            .Select(kv => $"{ItemStats.StatLabel(kv.Key.Replace('_', ' '))} {StatText(kv.Key, kv.Value)}"));
        if (c.Rec.Effects.Length > 0)
            parts.Add(c.Rec.Effects.Length > 28 ? c.Rec.Effects[..28] + "…" : c.Rec.Effects);
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private static void Badge(TextBlock host, string text, Brush fg)
    {
        host.Inlines.Add(new Run("  "));
        host.Inlines.Add(new InlineUIContainer(new Border
        {
            Background = BadgeBg, CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 0, 6, 1),
            Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = fg },
        }) { BaselineAlignment = BaselineAlignment.Center });
    }

    private void CellHost(UIElement child, int row, int col)
    {
        var border = new Border
        {
            BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 4, 8, 3), Child = child,
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        Board.Children.Add(border);
    }

    private void Cell(string text, int row, int col, Brush fg, bool right,
        double size = 12, bool bold = false)
    {
        CellHost(new TextBlock
        {
            Text = text, FontSize = size, Foreground = fg,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(col == 0 ? 0 : 8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = col == 5 ? 260 : 400,
        }, row, col);
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
