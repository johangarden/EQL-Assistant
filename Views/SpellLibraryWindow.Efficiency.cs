using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The EFFICIENCY tab (owner, 25 Sep): damage and healing spells ranked per
/// mana — the wiki baseline (rank 0), the same spell AT YOUR RANK (the rank
/// your cast lines carry, scaled by eqlwiki's Spell Upgrades rules), and what
/// your log says you really got (<see cref="SpellYield"/>). A resist filter
/// for a caster whose nukes span schools, and SUSTAINED per second — damage
/// over cast + reuse, so a big nuke on a long reuse can't pose as the best
/// one to chain (owner: "a reuse of 10+ secs might not perform better").
/// </summary>
public partial class SpellLibraryWindow
{
    private string _tab = "durations";
    private SpellYield? _yield;
    private Func<string>? _classesProvider;
    private Func<int>? _levelProvider;

    private bool _effHealing;
    private readonly HashSet<string> _effClasses = new(StringComparer.OrdinalIgnoreCase);
    private int _effBand = -1;    // index into SpellEfficiency.Bands; -1 = not chosen yet (your level's band)
    private int _effTargets = 1;
    private string _effResist = "";
    private string _effSort = "rank";
    private string _effSub = "";
    private List<string> _combo = new();
    private bool _effInit;

    // The tab remembers itself (owner, 25 Sep: "it feels heavy when I open it —
    // All levels and all classes"): library-view.json next to the config.
    private string? _viewPath;
    private sealed class ViewState
    {
        public string Tab { get; set; } = "durations";
        public bool Healing { get; set; }
        public string Sub { get; set; } = "";
        public int Band { get; set; } = -1;
        public int Targets { get; set; } = 1;
        public string Resist { get; set; } = "";
        public string Sort { get; set; } = "rank";
        public List<string> Classes { get; set; } = new();
    }

    private void LoadViewState()
    {
        if (_viewPath is null || !System.IO.File.Exists(_viewPath)) return;
        try
        {
            var v = System.Text.Json.JsonSerializer.Deserialize<ViewState>(System.IO.File.ReadAllText(_viewPath));
            if (v is null) return;
            _tab = v.Tab == "efficiency" ? "efficiency" : "durations";
            _effHealing = v.Healing; _effSub = v.Sub; _effBand = v.Band; _effTargets = Math.Clamp(v.Targets, 1, 5);
            _effResist = v.Resist; _effSort = v.Sort; _savedClasses = v.Classes;
        }
        catch { /* a stale file just means defaults */ }
    }
    private List<string> _savedClasses = new();

    private void SaveViewState()
    {
        if (_viewPath is null) return;
        try
        {
            System.IO.File.WriteAllText(_viewPath, System.Text.Json.JsonSerializer.Serialize(new ViewState
            {
                Tab = _tab, Healing = _effHealing, Sub = _effSub, Band = _effBand, Targets = _effTargets,
                Resist = _effResist, Sort = _effSort, Classes = _effClasses.ToList(),
            }));
        }
        catch { /* best effort */ }
    }

    /// <summary>First paint of the tab: your classes lit (the /who combo, else the
    /// loadout name, else what you picked last time), your level's band (else
    /// the top band — never the heavy All).</summary>
    private void InitEff()
    {
        if (_effInit) return;
        _effInit = true;
        string classes = _classesProvider?.Invoke() ?? "";
        _combo = classes.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => SpellEfficiency.AllClasses.Contains(c, StringComparer.OrdinalIgnoreCase)).Select(c => c.ToUpperInvariant()).ToList();
        _effClasses.Clear();
        foreach (var c in _combo.Count > 0 ? _combo : _savedClasses) _effClasses.Add(c);
        if (_effBand < 0)
        {
            int lv = _levelProvider?.Invoke() ?? 0;
            _effBand = lv > 0 ? SpellEfficiency.BandFor(lv) : SpellEfficiency.Bands.Length - 1;
        }
    }

    /// <summary>Render: flip the tab's filters as if clicked.</summary>
    internal void EffSetForTest(bool? healing = null, string? resist = null, string? classes = null)
    {
        if (healing is { } h) _effHealing = h;
        if (resist is not null) _effResist = resist;
        if (classes is not null) { _classesProvider = () => classes; _effInit = false; _effBand = 0; }
        Refresh();
    }

    /// <summary>Selftest / render: the rows painted last, in order.</summary>
    internal List<SpellEfficiency.Row> EffRowsForTest { get; } = new();

    private static readonly Brush EffGold = FreezeBrush(0xE8, 0xC1, 0x5A);
    private static readonly Brush EffGoldBg = FreezeBrush(0x2A, 0x22, 0x10);
    private static readonly Brush EffGoldEdge = FreezeBrush(0x7A, 0x5B, 0x22);
    private static readonly Brush EffText = FreezeBrush(0xE6, 0xEC, 0xF5);
    private static readonly Brush EffEdge = FreezeBrush(0x3A, 0x45, 0x60);
    private static readonly Brush EffSurface = FreezeBrush(0x1B, 0x21, 0x30);
    private static readonly Brush EffFaint = FreezeBrush(0x5C, 0x6B, 0x82);
    private static readonly Brush EffLow = FreezeBrush(0xFF, 0xB0, 0x74);
    private static readonly Brush EffHigh = FreezeBrush(0x81, 0xC7, 0x84);
    private static readonly Brush EffBest = FreezeBrush(0x20, 0x1E, 0x18);

    private void RenderTabs()
    {
        MenuTabs.Render(TabRow, new[]
        {
            new MenuTabs.Item("durations", "Durations", Tip: "Learned buff durations per spell, and ＋ to add a bar trigger"),
            new MenuTabs.Item("efficiency", "Efficiency", Tip: "Damage and healing per mana — the wiki's figure, at your rank, and what your log says you got"),
        }, _tab, ShowTab);
    }

    /// <summary>Switch tabs ("durations" · "efficiency").</summary>
    public void ShowTab(string tab)
    {
        _tab = tab == "efficiency" ? "efficiency" : "durations";
        bool eff = _tab == "efficiency";
        RenderTabs();
        ClassBox.Visibility = eff ? Visibility.Collapsed : Visibility.Visible; // the tab has its own class chips
        SaveViewState();
        DurHint.Visibility = eff ? Visibility.Collapsed : Visibility.Visible;
        EffBar.Visibility = EffVerdicts.Visibility = EffFoot.Visibility = EffTableHost.Visibility = eff ? Visibility.Visible : Visibility.Collapsed;
        DurTableHost.Visibility = eff ? Visibility.Collapsed : Visibility.Visible;
        if (eff && Width < 1080) Width = Math.Min(1080, SystemParameters.WorkArea.Width - 40);
        Refresh();
    }

    // ---- the controls ----------------------------------------------------------

    private void BuildEffBar()
    {
        InitEff();
        EffBar.Children.Clear();

        EffBar.Children.Add(Seg(new[] { ("dmg", "Damage"), ("heal", "Healing") }, _effHealing ? "heal" : "dmg",
            v => { bool h = v == "heal"; if (h != _effHealing) { _effHealing = h; _effSub = ""; if (h) _effResist = ""; } }, big: true));
        var sub = Group("");
        sub.Children.Add(_effHealing
            ? Seg(new[] { ("", "All"), ("direct", "Direct"), ("hot", "HoT"), ("group", "Group") }, _effSub, v => _effSub = v,
                tip: "Direct heals, heals over time, or the ones that land on the whole group")
            : Seg(new[] { ("", "All"), ("dd", "DD"), ("dot", "DoT"), ("ae", "AE") }, _effSub, v => _effSub = v,
                tip: "Direct damage (nukes and instant taps), damage over time (ticking drains too), or area spells"));
        EffBar.Children.Add(sub);

        var lvl = Group("LEVEL");
        lvl.Children.Add(Seg(SpellEfficiency.Bands.Select((b, i) => (i.ToString(), b.Label)), _effBand.ToString(), v => _effBand = int.Parse(v),
            tip: $"The level you get a spell at — EQ Legends caps at {SpellEfficiency.LevelCap}, so spells above it stay out"));
        EffBar.Children.Add(lvl);
        var tg = Group("TARGETS");
        tg.Children.Add(Seg(new[] { ("1", "1"), ("3", "3"), ("5", "5") }, _effTargets.ToString(), v => _effTargets = int.Parse(v),
            tip: "AE and group spells times this many targets — single-target spells don't change"));
        EffBar.Children.Add(tg);
        if (!_effHealing)
        {
            var rs = Group("RESIST");
            rs.Children.Add(Seg(new[] { ("", "All"), ("Magic", "Magic"), ("Fire", "Fire"), ("Cold", "Cold"), ("Poison", "Poison"), ("Disease", "Disease") },
                _effResist, v => _effResist = v, tip: "Only the nukes of one school — a mob that resists fire eats cold"));
            EffBar.Children.Add(rs);
        }

        // Every class as a chip — a multiclass view across any mix (owner, 25 Sep);
        // yours are lit on open, none lit = every class.
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        chips.Children.Add(new TextBlock { Text = "CLASSES", Foreground = EffFaint, FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        foreach (var c in SpellEfficiency.AllClasses)
        {
            string cls = c;
            var chip = Chip(cls, _effClasses.Contains(cls), () => { if (!_effClasses.Remove(cls)) _effClasses.Add(cls); });
            if (_combo.Contains(cls, StringComparer.OrdinalIgnoreCase)) chip.ToolTip = "One of your classes";
            chip.Margin = new Thickness(0, 0, 4, 4);
            chips.Children.Add(chip);
        }
        if (_effClasses.Count == 0)
            chips.Children.Add(new TextBlock { Text = "none picked — every class", Foreground = EffFaint, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4) });
        else if (_combo.Count > 0 && !_effClasses.SetEquals(_combo))
        {
            var mine = Chip("↺ mine", false, () => { _effClasses.Clear(); foreach (var c in _combo) _effClasses.Add(c); });
            mine.ToolTip = "Back to your own classes: " + string.Join("/", _combo);
            mine.Margin = new Thickness(6, 0, 4, 4);
            chips.Children.Add(mine);
        }
        EffBar.Children.Add(chips);
    }

    private static StackPanel Group(string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 6), VerticalAlignment = VerticalAlignment.Center };
        if (label.Length > 0)
            sp.Children.Add(new TextBlock { Text = label, Foreground = EffFaint, FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        return sp;
    }

    /// <summary>A themed segmented switch — never a stock control (house rule).</summary>
    private Border Seg(IEnumerable<(string Value, string Label)> options, string current, Action<string> pick, bool big = false, string? tip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new Border
        {
            BorderBrush = EffEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Background = EffSurface,
            Child = row, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, Margin = big ? new Thickness(0, 0, 16, 6) : new Thickness(0),
        };
        var list = options.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var (value, label) = list[i];
            bool first = i == 0, last = i == list.Count - 1;
            bool on = value == current;
            var b = new Border
            {
                // The ends carry the pill's own rounding (inner radius = outer 12 − the 1 px edge)
                // so a lit end never pokes a square corner out past the outline.
                CornerRadius = new CornerRadius(first ? 11 : 0, last ? 11 : 0, last ? 11 : 0, first ? 11 : 0),
                Background = on ? EffGoldBg : EffSurface, Cursor = Cursors.Hand,
                BorderBrush = EffEdge, BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0),
                Padding = big ? new Thickness(15, 3, 15, 4) : new Thickness(10, 2, 10, 3),
                Child = new TextBlock { Text = label, FontSize = big ? 12.5 : 11.5, FontWeight = FontWeights.SemiBold, Foreground = on ? EffGold : DurDimFg },
            };
            string v = value;
            b.MouseLeftButtonDown += (_, e) => { pick(v); e.Handled = true; Refresh(); };
            row.Children.Add(b);
        }
        return host;
    }

    private Border Chip(string text, bool on, Action toggle)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand,
            BorderBrush = on ? EffGoldEdge : EffEdge, Background = on ? EffGoldBg : EffSurface, Padding = new Thickness(8, 1, 8, 2),
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = on ? EffGold : DurDimFg },
        };
        b.MouseLeftButtonDown += (_, e) => { toggle(); e.Handled = true; Refresh(); };
        return b;
    }

    // ---- the table --------------------------------------------------------------

    private static readonly (string Key, string Head, string Tip)[] EffCols =
    {
        ("name", "SPELL", ""),
        ("lv", "LVL", "The lowest level any of your classes gets it"),
        ("mana", "MANA", "The wiki's mana cost (rank 0) — the log never prints mana"),
        ("total", "WIKI TOTAL", "The wiki's damage or heal for one cast: the instant part plus every tick of the full run; AE / group × targets"),
        ("per", "PER MANA", "Wiki total ÷ wiki mana"),
        ("sec", "PER CAST SEC", "Wiki total ÷ cast time — how fast one cast turns casting into damage"),
        ("sus", "SUSTAINED /S", "Chained: total ÷ (cast + reuse), never faster than a DoT's own run — the honest figure for a long reuse"),
        ("rank", "AT YOUR RANK", "Per mana at the rank you cast it (from your cast lines), by eqlwiki's Spell Upgrades rules"),
        ("yours", "YOURS", "Your real average per cast from the log ÷ the mana at your rank — focus, stance, crits, resists and mobs dying mid-DoT all included"),
    };

    private void RefreshEfficiency()
    {
        BuildEffBar();
        EffTableHost.Children.Clear();
        EffTableHost.RowDefinitions.Clear();
        EffTableHost.ColumnDefinitions.Clear();
        EffRowsForTest.Clear();

        string search = SearchBox?.Text.Trim() ?? "";
        var classes = _effClasses.ToList(); // none = every class
        var lvBand = SpellEfficiency.Bands[Math.Clamp(_effBand, 0, SpellEfficiency.Bands.Length - 1)];
        var rows = SpellEfficiency.Rows(_library, _yield, classes, lvBand.Lo, lvBand.Hi, _effHealing, _effTargets,
            SpellLibrary.EffectAlias(search) ?? search, _effResist, _effSub);
        SaveViewState();
        rows = _effSort switch
        {
            "name" => rows.OrderBy(r => r.Spell.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "lv" => rows.OrderByDescending(r => r.Level).ToList(),
            "mana" => rows.OrderBy(r => r.Spell.Mana).ToList(),
            "total" => rows.OrderByDescending(r => r.Total).ToList(),
            "per" => rows.OrderByDescending(r => r.PerMana).ToList(),
            "sec" => rows.OrderByDescending(r => r.PerCastSec).ToList(),
            "sus" => rows.OrderByDescending(r => r.BestSustained).ToList(),
            "yours" => rows.OrderByDescending(r => r.ObservedPerMana ?? -1).ThenByDescending(r => r.BestPerMana).ToList(),
            _ => rows.OrderByDescending(r => r.BestPerMana).ToList(),
        };
        EffRowsForTest.AddRange(rows);
        string unit = _effHealing ? "healing" : "damage";
        CountText.Text = $"{rows.Count} {unit} spell(s) · {rows.Count(r => r.Observed is not null)} with your own numbers";

        Verdicts(rows, unit);
        EffFoot.Text = "Wiki total = rank 0's damage or heal for one cast, every tick of a DoT or HoT counted"
            + (_effTargets > 1 ? $", AE and group spells ×{_effTargets}" : "")
            + ". At your rank = the rank your cast lines show, scaled by eqlwiki's Spell Upgrades page (direct damage +6 % a rank, over-time +3 % a tick and +5 % duration, heals +3 %; −2 % mana each) — taken as additive steps, and the wiki still calls them tested-ish."
            + $" Yours = your average per cast from the log (every rank pooled; {SpellEfficiency.MinObservedCasts}+ casts), healing counted as landed (overheal left out). Mana always comes from the wiki. Songs, disciplines and procs cost no mana and stay out.";

        if (rows.Count == 0)
        {
            EffTableHost.Children.Add(new TextBlock
            {
                Text = $"No {unit} spell for these filters.",
                Foreground = DurDimFg, FontSize = 12, Margin = new Thickness(2, 6, 0, 0),
            });
            return;
        }

        for (int i = 0; i < EffCols.Length; i++)
            EffTableHost.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, MinWidth = i == 0 ? 230 : 0 });
        EffTableHost.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < EffCols.Length; i++)
        {
            var (key, head, tip) = EffCols[i];
            bool sorted = key == _effSort;
            var tb = new TextBlock
            {
                Text = head + (sorted ? " ▾" : ""), FontSize = 9.5, FontWeight = FontWeights.Bold,
                Foreground = sorted ? EffGold : DurHeadFg, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            };
            var cell = new Border
            {
                BorderBrush = DurLine, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(i == 0 ? 2 : 12, 4, i == 0 ? 12 : 2, 4),
                Child = tb, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = tip.Length > 0 ? tip : null,
            };
            string k = key;
            cell.MouseLeftButtonDown += (_, e) => { _effSort = k; e.Handled = true; Refresh(); };
            Grid.SetColumn(cell, i);
            EffTableHost.Children.Add(cell);
        }

        double maxPer = Math.Max(0.01, rows.Max(r => r.PerMana));
        var best = rows.OrderByDescending(r => r.BestPerMana).First();
        int row = 1;
        foreach (var r in rows)
        {
            EffTableHost.RowDefinitions.Add(new RowDefinition());
            bool isBest = ReferenceEquals(r, best);
            if (isBest)
            {
                var band = new Border { Background = EffBest };
                Grid.SetRow(band, row); Grid.SetColumnSpan(band, EffCols.Length);
                EffTableHost.Children.Add(band);
            }
            var color = FreezeColor(SpellLibrary.EffectColor(r.Spell.Effect));

            // SPELL: name · effect · resist · AE, then classes, ticks and a long reuse.
            var name = new TextBlock { TextWrapping = TextWrapping.NoWrap };
            name.Inlines.Add(new Run(r.Spell.Name) { Foreground = EffText, FontWeight = FontWeights.SemiBold, FontSize = 12 });
            name.Inlines.Add(new Run("  " + r.Spell.Effect.ToUpperInvariant()) { Foreground = color, FontWeight = FontWeights.Bold, FontSize = 9.5 });
            if (r.Spell.Resist.Length > 0 && r.Spell.Resist != "Unresistable")
                name.Inlines.Add(new Run("  " + r.Spell.Resist.ToUpperInvariant() + (r.Spell.ResistMod != 0 ? $" {r.Spell.ResistMod:+0;−0}" : ""))
                { Foreground = DurDimFg, FontWeight = FontWeights.Bold, FontSize = 9.5 });
            if (r.Multi)
                name.Inlines.Add(new Run("  " + (r.Spell.Targets == "group" ? "GROUP" : "AE") + (_effTargets > 1 ? $" ×{_effTargets}" : ""))
                { Foreground = EffGold, FontWeight = FontWeights.Bold, FontSize = 9.5 });
            var sub = new TextBlock { FontSize = 10.5, Foreground = DurHeadFg, Margin = new Thickness(0, 1, 0, 0) };
            sub.Inlines.Add(new Run(r.ClassText));
            if (r.Spell.Ticks > 0) sub.Inlines.Add(new Run($" · {r.Spell.Ticks} ticks"));
            if (r.Spell.CastSec > 0) sub.Inlines.Add(new Run($" · {r.Spell.CastSec:0.##} s cast"));
            if (r.LongReuse) sub.Inlines.Add(new Run($" · reuse {r.Spell.RecastSec:0.#} s") { Foreground = EffLow });
            var spellCell = new StackPanel(); spellCell.Children.Add(name); spellCell.Children.Add(sub);
            Cell(spellCell, row, 0, left: true);

            Cell(Num(r.Level.ToString(), DurDimFg), row, 1);
            Cell(Num($"{r.Spell.Mana:N0}", DurValFg), row, 2);
            Cell(Num($"{r.Total:N0}", DurValFg), row, 3);

            var per = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            per.Children.Add(new Border { Width = Math.Max(2, 64 * r.PerMana / maxPer), Height = 6, CornerRadius = new CornerRadius(3), Background = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            per.Children.Add(new TextBlock { Text = Fmt(r.PerMana), Foreground = EffText, FontWeight = FontWeights.SemiBold, FontSize = 12, MinWidth = 30, TextAlignment = TextAlignment.Right });
            Cell(per, row, 4);

            Cell(Num($"{r.PerCastSec:N0}", DurValFg, "/s"), row, 5);
            Cell(Num($"{r.BestSustained:N0}", r.LongReuse ? EffLow : DurValFg, "/s"), row, 6);

            if (r.Rank is { } rk && r.RankPerMana is { } rp)
                Cell(TwoLine(Fmt(rp), $"{SpellEfficiency.Roman(rk)} · {r.RankTotal:N0} for {r.RankMana:N0} mana", isBest ? EffGold : EffText, DurHeadFg), row, 7);
            else Cell(Num("—", EffFaint), row, 7);

            if (r.ObservedPerMana is { } op && r.Observed is { } ob)
            {
                string note = r.ObservedTargets is { } ot ? $"{ob:N0} a cast · ≈{ot:0.0} targets · {r.Casts:N0} casts"
                    : $"{ob:N0} a cast · {r.ObservedPct * 100:0}% of your rank · {r.Casts:N0} casts";
                var noteFg = r.ObservedPct is { } pct ? pct < 0.8 ? EffLow : pct > 1.2 ? EffHigh : DurHeadFg : DurHeadFg;
                var cell = TwoLine(Fmt(op), note, EffText, noteFg);
                cell.ToolTip = r.ObservedPct is { } p2 && p2 < 0.8 && r.Spell.Ticks > 0
                    ? "Under the full run — mobs often die before a DoT's last tick, and resists count as casts with no damage."
                    : "Your average per cast from the log, over the mana at your rank.";
                Cell(cell, row, 8);
            }
            else Cell(Num("—", EffFaint), row, 8);
            row++;
        }
    }

    private void Verdicts(List<SpellEfficiency.Row> rows, string unit)
    {
        EffVerdicts.Children.Clear();
        if (rows.Count == 0) { EffVerdicts.Visibility = Visibility.Collapsed; return; }
        EffVerdicts.Visibility = Visibility.Visible;
        var mp = rows.OrderByDescending(r => r.BestPerMana).First();
        var su = rows.OrderByDescending(r => r.BestSustained).First();
        var yo = rows.Where(r => r.ObservedPerMana is not null).OrderByDescending(r => r.ObservedPerMana).FirstOrDefault();
        EffVerdicts.Children.Add(Verdict("MOST PER MANA", mp.Spell.Name, $"{Fmt(mp.BestPerMana)} {unit} a mana{(mp.Rank is { } r1 ? $" at {SpellEfficiency.Roman(r1)}" : "")}"));
        EffVerdicts.Children.Add(Verdict("BEST SUSTAINED", su.Spell.Name, $"{su.BestSustained:N0} a second, chained"));
        EffVerdicts.Children.Add(yo is null
            ? Verdict("BEST IN YOUR LOG", "—", $"cast a spell {SpellEfficiency.MinObservedCasts}+ times")
            : Verdict("BEST IN YOUR LOG", yo.Spell.Name, $"{Fmt(yo.ObservedPerMana!.Value)} a mana, real"));
    }

    private static Border Verdict(string label, string name, string text)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Foreground = EffFaint, FontSize = 9.5, FontWeight = FontWeights.Bold });
        var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        line.Inlines.Add(new Run(name) { Foreground = EffText, FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        line.Inlines.Add(new Run("  " + text) { Foreground = DurDimFg, FontSize = 11 });
        sp.Children.Add(line);
        return new Border { Background = EffSurface, BorderBrush = DurLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 7), Margin = new Thickness(0, 0, 8, 0), Child = sp };
    }

    private static TextBlock Num(string text, Brush fg, string suffix = "")
    {
        var tb = new TextBlock { FontSize = 12, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tb.Inlines.Add(new Run(text));
        if (suffix.Length > 0) tb.Inlines.Add(new Run(suffix) { Foreground = EffFaint });
        return tb;
    }

    private static StackPanel TwoLine(string top, string bottom, Brush topFg, Brush bottomFg)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        sp.Children.Add(new TextBlock { Text = top, Foreground = topFg, FontWeight = FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
        sp.Children.Add(new TextBlock { Text = bottom, Foreground = bottomFg, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right });
        return sp;
    }

    private void Cell(UIElement child, int row, int col, bool left = false)
    {
        var b = new Border
        {
            BorderBrush = DurLine, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(left ? 2 : 12, 5, left ? 12 : 2, 5), Child = child,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        if (child is FrameworkElement fe && !left) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(b, row); Grid.SetColumn(b, col);
        EffTableHost.Children.Add(b);
    }

    private static string Fmt(double v) => v >= 10 ? v.ToString("0") : v.ToString("0.0");

    private static Brush FreezeColor(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
