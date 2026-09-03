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
    // Armor and weapons score on different questions — each view keeps its
    // own priorities; _prio points at whichever is active.
    private string[] _prioArmor = { "AC", "STA", "INT" };
    private string[] _prioWeapon = { "DMG_DLY", "STR", "STA" };
    private string[] _prio;
    private bool _weapons; // false = Armor view
    private BisFinder.WeaponStyle _style = BisFinder.WeaponStyle.DualWield;
    private BisFinder.RangeMode _range = BisFinder.RangeMode.Dps;
    private readonly Dictionary<string, bool> _fold = new(StringComparer.Ordinal); // explicit toggles

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
        _prio = _prioArmor;
        BuildClassChips();
        BuildViewPills();
        SyncPrioBoxes();
    }

    /// <summary>The priorities a view can pick from — DMG/DLY only means
    /// something for weapons.</summary>
    private (string Key, string Label)[] _options = Array.Empty<(string, string)>();

    private (string Key, string Label)[] OptionsFor(bool weapons) =>
        BisFinder.Priorities.Where(p => weapons || p.Key != "DMG_DLY").ToArray();

    // ---- Armor | Weapons, and the fighting style --------------------------------

    private static readonly (BisFinder.WeaponStyle Style, string Label)[] Styles =
    {
        (BisFinder.WeaponStyle.DualWield, "Dual wield"),
        (BisFinder.WeaponStyle.ShieldAndOne, "1H + Shield"),
        (BisFinder.WeaponStyle.TwoHanded, "Two-handed"),
    };

    private void BuildViewPills()
    {
        ViewPills.Children.Clear();
        foreach (var (tag, label) in new[] { ("armor", "Armor"), ("weapons", "Weapons") })
        {
            var pill = Chip(label, tag);
            pill.Margin = new Thickness(0, 0, 6, 4);
            pill.Padding = new Thickness(13, 3, 13, 4);
            bool weapons = tag == "weapons";
            pill.MouseLeftButtonDown += (_, _) =>
            {
                if (_weapons == weapons) return;
                _weapons = weapons;
                _prio = _weapons ? _prioWeapon : _prioArmor;
                SyncPrioBoxes();
                Refresh();
            };
            ViewPills.Children.Add(pill);
        }
        var gap = new TextBlock { Text = "·", Foreground = DimmerFg, Margin = new Thickness(6, 3, 12, 0) };
        ViewPills.Children.Add(gap);
        foreach (var (style, label) in Styles)
        {
            var pill = Chip(label, "style:" + style);
            var s = style;
            pill.MouseLeftButtonDown += (_, _) =>
            {
                _style = s;
                SavePrefs();
                Refresh();
            };
            ViewPills.Children.Add(pill);
        }
        // What the range slot is for: a bow build or a stat brooch.
        var gap2 = new TextBlock { Text = "· Range:", Foreground = DimmerFg, FontSize = 11, Margin = new Thickness(6, 3, 8, 0) };
        ViewPills.Children.Add(gap2);
        foreach (var (mode, label) in new[] { (BisFinder.RangeMode.Dps, "DPS"), (BisFinder.RangeMode.Stat, "Stat") })
        {
            var pill = Chip(label, "range:" + mode);
            var m = mode;
            pill.MouseLeftButtonDown += (_, _) =>
            {
                _range = m;
                SavePrefs();
                Refresh();
            };
            ViewPills.Children.Add(pill);
        }
    }

    private void StyleViewPills()
    {
        foreach (var child in ViewPills.Children)
        {
            if (child is not Border pill || pill.Tag is not string tag) continue;
            if (tag == "armor") Paint(pill, !_weapons, LaneOnFg, LaneOnLine);
            else if (tag == "weapons") Paint(pill, _weapons, LaneOnFg, LaneOnLine);
            else if (tag.StartsWith("range:", StringComparison.Ordinal))
            {
                pill.Visibility = _weapons ? Visibility.Visible : Visibility.Collapsed;
                Paint(pill, tag == "range:" + _range, ChipOnFg, ChipOnLine);
            }
            else
            {
                pill.Visibility = _weapons ? Visibility.Visible : Visibility.Collapsed;
                Paint(pill, tag == "style:" + _style, ChipOnFg, ChipOnLine);
            }
        }
        foreach (var child in ViewPills.Children)
            if (child is TextBlock gap) gap.Visibility = _weapons ? Visibility.Visible : Visibility.Collapsed;
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
        // "AC/STA/INT;DMG_DLY/STR/STA;DualWield" — armor set, weapon set, style.
        var parts = prio.Split(';');
        if (parts.Length > 0 && parts[0].Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: 3 } a)
            _prioArmor = a;
        if (parts.Length > 1 && parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: 3 } w)
            _prioWeapon = w;
        if (parts.Length > 2 && Enum.TryParse(parts[2], out BisFinder.WeaponStyle st)) _style = st;
        if (parts.Length > 3 && Enum.TryParse(parts[3], out BisFinder.RangeMode rm)) _range = rm;
        _prio = _weapons ? _prioWeapon : _prioArmor;
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
        _options = OptionsFor(_weapons);
        var labels = _options.Select(p => p.Label).ToList();
        var boxes = new[] { P1, P2, P3 };
        for (int i = 0; i < 3; i++)
        {
            boxes[i].ItemsSource = labels;
            int ix = Array.FindIndex(_options, p => p.Key == _prio[i]);
            if (ix < 0) { ix = 0; _prio[i] = _options[0].Key; } // a weapon-only key in armor view
            boxes[i].SelectedIndex = ix;
        }
        _building = false;
    }

    private void Prio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        var boxes = new[] { P1, P2, P3 };
        for (int i = 0; i < 3; i++)
            if (boxes[i].SelectedIndex >= 0 && boxes[i].SelectedIndex < _options.Length)
                _prio[i] = _options[boxes[i].SelectedIndex].Key;
        SavePrefs();
        Refresh();
    }

    private void SavePrefs() =>
        _config?.SaveBisPrefs(_charKey, string.Join("/", _combo),
            string.Join("/", _prioArmor) + ";" + string.Join("/", _prioWeapon) + ";" + _style + ";" + _range);

    // ---- the board -----------------------------------------------------------------

    private void Refresh()
    {
        if (_stats is null || Board is null) return;

        foreach (var child in ClassChips.Children)
            if (child is Border c && c.Tag is string cls)
                Paint(c, _combo.Contains(cls), ChipOnFg, ChipOnLine);
        ClassHint.Text = _combo.Count == 0
            ? "No combo picked — every wearable item counts. Run /who in game to prefill yours."
            : $"Ranking for {string.Join("/", _combo)} — an item counts when ANY of these classes may wear it."
              + (_dumpStamp.Length > 0 ? $"  Snapshot {_dumpStamp}." : "");

        StyleViewPills();
        var all = BisFinder.Build(_rows, _stats, _combo, _prio);
        var result = _weapons ? BisFinder.WeaponView(all, _style, _range) : BisFinder.ArmorView(all);
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

        // Owner rulings (2 Sep): only what the combo can wear; two-slot
        // slots split into SLOT 1 / SLOT 2, each its pick + ONE alternative;
        // single slots the pick + two alternatives. Worn items always show,
        // even when they fell below the alternatives — you should see what
        // you're wearing against the winner.
        // Slot headers fold (Johan, 2 Sep): a slot you already wear the best
        // of collapses to "HEAD  BiS"; a slot with an upgrade in storage opens
        // with "1 UPGRADE". Any header toggles on click.
        void SlotHeader(string key, string label, bool open, bool upgrade)
        {
            Board.RowDefinitions.Add(new RowDefinition());
            var head = new TextBlock
            {
                FontSize = 10.5, FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, open ? 12 : 7, 0, open ? 3 : 2),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            head.Inlines.Add(new Run(open ? "▾ " : "▸ ") { Foreground = SlotFg, FontSize = 9.5 });
            head.Inlines.Add(new Run(label.ToUpperInvariant()) { Foreground = open ? SlotFg : DimFg });
            Badge(head, upgrade ? "UPGRADE" : "BiS", upgrade ? UpFg : WornFg);
            head.MouseLeftButtonDown += (_, _) =>
            {
                _fold[key] = !open;
                Refresh();
            };
            Grid.SetRow(head, row);
            Grid.SetColumnSpan(head, 8);
            Board.Children.Add(head);
            row++;
        }

        void Divider()
        {
            Board.RowDefinitions.Add(new RowDefinition());
            var line = new Border
            {
                Height = 1, Background = Freeze("#2C3650"), Margin = new Thickness(0, 14, 0, 4),
            };
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, 8);
            Board.Children.Add(line);
            row++;
        }

        void RenderRow(BisFinder.Candidate c, bool pick, bool upgrade)
        {
            Board.RowDefinitions.Add(new RowDefinition());
            var name = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Inlines.Add(new Run(c.BaseName) { Foreground = pick ? NameFg : DimFg, FontWeight = FontWeights.SemiBold });
            if (c.Tier > 0) name.Inlines.Add(new Run($" +{c.Tier}") { Foreground = TierFg });
            if (c.Copies > 1) name.Inlines.Add(new Run($" ×{c.Copies}") { Foreground = DimmerFg });
            if (c.Worn) Badge(name, "WORN", WornFg);
            if (upgrade) Badge(name, "UPGRADE", UpFg);
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

        // Every slot becomes one foldable entry — paired slots become TWO
        // (EAR 1, EAR 2 …), each with its own verdict. Upgrades list first,
        // then a divider, then the slots you already wear the best of.
        var entries = new List<(string Key, string Label, bool Upgrade, Action Render)>();
        foreach (var slot in result.Slots)
        {
            if (slot.Ranked.Count == 0) continue;
            var upgrades = slot.Upgrades.ToHashSet();
            var picks = slot.Picks.ToList();

            if (slot.Count == 1)
            {
                entries.Add((slot.Key, slot.Label, upgrades.Count > 0, () =>
                {
                    var shown = slot.Ranked.Take(3)
                        .Concat(slot.Ranked.Skip(3).Where(c => c.Worn));
                    foreach (var c in shown)
                        RenderRow(c, picks.Contains(c), upgrades.Contains(c));
                }));
                continue;
            }

            // Two slots, one earring per ear (Johan, 2 Sep): a worn pick keeps
            // its slot; a storage pick takes the slot of the WEAKEST worn item
            // it displaces, which shows right under it as what it replaces.
            // Then one alternative from storage per slot.
            var wornPicks = picks.Where(c => c.Worn).ToList();
            var freePicks = picks.Where(c => !c.Worn).ToList();
            var displaced = slot.Ranked.Where(c => c.Worn && !picks.Contains(c))
                .OrderBy(c => c.Score).ToList(); // weakest goes first
            var alts = slot.Ranked.Where(c => !c.Worn && !picks.Contains(c)).ToList();
            int dispIx = 0;

            var subSlots = new List<(BisFinder.Candidate? Pick, BisFinder.Candidate? Replaces)>();
            foreach (var w in wornPicks) subSlots.Add((w, null));
            foreach (var f in freePicks)
                subSlots.Add((f, dispIx < displaced.Count ? displaced[dispIx++] : null));
            while (subSlots.Count < slot.Count) subSlots.Add((null, null));

            for (int s = 0; s < subSlots.Count; s++)
            {
                var (pick, replaces) = subSlots[s];
                var alt = s < alts.Count ? alts[s] : null;
                bool upgrade = pick is not null && upgrades.Contains(pick);
                entries.Add(($"{slot.Key}{s + 1}", $"{slot.Label} {s + 1}", upgrade, () =>
                {
                    if (pick is not null) RenderRow(pick, true, upgrade);
                    if (replaces is not null) RenderRow(replaces, false, false);
                    if (alt is not null) RenderRow(alt, false, false);
                }));
            }
        }

        void Emit((string Key, string Label, bool Upgrade, Action Render) e)
        {
            // Weapons: three slots, room for all — open even when BiS (owner ruling).
            bool open = _fold.TryGetValue(e.Key, out bool o) ? o : (e.Upgrade || _weapons);
            SlotHeader(e.Key, e.Label, open, e.Upgrade);
            if (open) e.Render();
        }

        // Weapons keep the hands' order - Primary, Secondary, Range, Ammo -
        // no matter what's an upgrade (owner ruling). Armor sorts upgrades
        // to the top, a divider, then the slots you already wear the best of.
        if (_weapons)
        {
            foreach (var e in entries) Emit(e);
            return;
        }
        var withUpgrades = entries.Where(e => e.Upgrade).ToList();
        var solved = entries.Where(e => !e.Upgrade).ToList();
        foreach (var e in withUpgrades) Emit(e);
        if (withUpgrades.Count > 0 && solved.Count > 0) Divider();
        foreach (var e in solved) Emit(e);
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
