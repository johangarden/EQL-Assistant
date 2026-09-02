using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>Browser for the persisted loot history (search + kind filter).</summary>
public partial class LootWindow : Window
{
    private const int MaxRows = 400;

    private readonly LootTracker _loot;

    private static readonly Brush UpgradeFg = Freeze(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush SoldFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush KeptFg = Freeze(Color.FromRgb(0x8F, 0xA6, 0xC4));

    public sealed record RowVm(string Item, string Detail, string ValueText, Brush ValueBrush);

    public LootWindow(LootTracker loot)
    {
        InitializeComponent();
        DialogPlacement.Persist(this, "loot");
        WindowTheme.ApplyDark(this);
        _loot = loot;
        _loot.Changed += OnLootChanged;
        Closed += (_, _) => _loot.Changed -= OnLootChanged;
        BuildGradePills();
        BuildTimePills();
        StylePills();
        Refresh();
    }

    private void OnLootChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Filters_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (ResultsList is null) return; // fired during InitializeComponent
        if (_view == "motes") { RefreshMotes(); return; }

        string search = SearchBox.Text.Trim();
        string kindTag = (KindBox.SelectedValue as string) ?? "";

        var rows = new List<RowVm>();
        int matches = 0;
        foreach (var e in _loot.Entries)
        {
            if (kindTag.Length > 0 && e.Kind.ToString() != kindTag) continue;
            if (search.Length > 0
                && !e.Item.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !e.Mob.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !e.Zone.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            matches++;
            if (rows.Count < MaxRows) rows.Add(ToRow(e));
        }
        ResultsList.ItemsSource = rows;

        string vendored = LootTracker.FormatCoins(_loot.TotalVendorCopper);
        SummaryText.Text = $"{_loot.UpgradeCount} upgrades · vendored {vendored}";
        Title = matches > MaxRows
            ? $"EQL Assistant — Loot History ({matches} matches, showing {MaxRows})"
            : "EQL Assistant — Loot History";
    }

    private static RowVm ToRow(LootTracker.LootEntry e)
    {
        string when = e.When.Date == DateTime.Today
            ? e.When.ToString("HH:mm")
            : e.When.ToString("dd MMM HH:mm");
        string zone = e.Zone.Length > 0 ? $" · {e.Zone}" : "";
        string detail = $"{when} · from {e.Mob}{zone}";

        string item = e.Count > 1 ? $"{e.Count}× {e.Item}" : e.Item;
        return e.Kind switch
        {
            LootTracker.LootKind.Upgrade => new RowVm(item, detail, $"→ {e.Result}", UpgradeFg),
            LootTracker.LootKind.Sold => new RowVm(item, detail, $"+{LootTracker.FormatCoins(e.Copper)}", SoldFg),
            LootTracker.LootKind.Currency => new RowVm(item, detail, "currency", KeptFg),
            _ => new RowVm(item, detail, "kept", KeptFg),
        };
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---- the Mote farming view -------------------------------------------------

    private string _view = "drops";
    private int _gradeIx = -1; // the grade lens; -1 = all grades
    private readonly HashSet<string> _moteOpen = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Brush PillOnBg = Freeze(Color.FromRgb(0x23, 0x2B, 0x40));
    private static readonly Brush PillOnLine = Freeze(Color.FromRgb(0x5A, 0x6B, 0x8C));
    private static readonly Brush PillOnFg = Freeze(Color.FromRgb(0xE8, 0xC1, 0x5A));
    private static readonly Brush PillOffBg = Freeze(Color.FromRgb(0x1B, 0x21, 0x30));
    private static readonly Brush PillOffLine = Freeze(Color.FromRgb(0x3A, 0x45, 0x60));
    private static readonly Brush PillOffFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush MoteHeadFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush MoteZoneFg = Freeze(Color.FromRgb(0xC9, 0xD4, 0xE3));
    private static readonly Brush MoteDimFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush MoteDimmerFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush MoteLine = Freeze(Color.FromRgb(0x1F, 0x26, 0x37));
    private static readonly Brush MoteRateFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush MoteTierFg = Freeze(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Brush MoteGoldFg = Freeze(Color.FromRgb(0xE8, 0xC1, 0x5A));

    // One color per grade, weakest → strongest (matches MoteFarm.Grades).
    private static readonly Brush[] GradeFg =
    {
        Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E)), Freeze(Color.FromRgb(0xA5, 0xD6, 0xA7)),
        Freeze(Color.FromRgb(0x81, 0xD4, 0xFA)), Freeze(Color.FromRgb(0xCE, 0x93, 0xD8)),
        Freeze(Color.FromRgb(0xFF, 0xCC, 0x80)), Freeze(Color.FromRgb(0xF4, 0x8F, 0xB1)),
        Freeze(Color.FromRgb(0xFF, 0xF1, 0x76)),
    };

    private void View_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string v || v == _view) return;
        _view = v;
        StylePills();
        Refresh();
    }

    private void StylePills()
    {
        bool motes = _view == "motes";
        foreach (var (pill, on) in new[] { (DropPill, !motes), (MotePill, motes) })
        {
            pill.Background = on ? PillOnBg : PillOffBg;
            pill.BorderBrush = on ? PillOnLine : PillOffLine;
            if (pill.Child is TextBlock tb) tb.Foreground = on ? PillOnFg : PillOffFg;
        }
        FiltersRow.Visibility = motes ? Visibility.Collapsed : Visibility.Visible;
        GradePills.Visibility = motes ? Visibility.Visible : Visibility.Collapsed;
        TimePills.Visibility = motes ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = motes ? Visibility.Collapsed : Visibility.Visible;
        MoteScroll.Visibility = motes ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = motes
            ? "Where an hour of farming actually pays, mined from this ledger: mote drops clustered into stints (≤15 min between drops, first→last drop on the clock, so AFK time never inflates a rate). A rate only prints at ≥30 min farmed AND ≥8 motes, and the 'best farm' crown needs 45+ min on the clock — one lucky window never outranks a proven grind; T3 and T4 of the same zone are separate farms. Fold a row out for the mobs that paid and the individual stints."
            : "Every item looted, from the log: upgrades applied to your gear, items kept in your bags, and auto-vendored drops with what they sold for.";
    }

    /// <summary>The grade lens: All + one pill per grade, in ladder colors.</summary>
    private void BuildGradePills()
    {
        GradePills.Children.Clear();
        for (int i = -1; i < MoteFarm.Grades.Length; i++)
        {
            int ix = i;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 2, 11, 3),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock
                {
                    Text = ix < 0 ? "All grades" : MoteFarm.Grades[ix],
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            pill.MouseLeftButtonDown += (_, _) =>
            {
                _gradeIx = ix;
                RefreshMotes();
            };
            GradePills.Children.Add(pill);
        }
    }

    // The strictness dial (owner request, 2 Sep): only zones with at least
    // this much clock stand as farms; the rest demote to a collapsed hint
    // row. Default 30m — the same bar a rate needs anyway.
    private static readonly (double Min, string Label)[] TimeOptions =
        { (0, "Any time"), (30, "30m+"), (60, "1h+"), (90, "1h30m+") };
    private double _minFarmed = 30;
    private bool _thinOpen;

    private void BuildTimePills()
    {
        TimePills.Children.Clear();
        TimePills.Children.Add(new TextBlock
        {
            Text = "Farmed at least:",
            FontSize = 11,
            Foreground = MoteDimFg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 4),
        });
        foreach (var (min, label) in TimeOptions)
        {
            double m = min;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 2, 11, 3),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
            pill.MouseLeftButtonDown += (_, _) =>
            {
                _minFarmed = m;
                RefreshMotes();
            };
            TimePills.Children.Add(pill);
        }
    }

    private void StyleTimePills()
    {
        for (int i = 1; i < TimePills.Children.Count; i++) // 0 = the label
        {
            if (TimePills.Children[i] is not Border pill) continue;
            bool on = Math.Abs(TimeOptions[i - 1].Min - _minFarmed) < 0.1;
            pill.Background = on ? PillOnBg : PillOffBg;
            pill.BorderBrush = on ? PillOnLine : PillOffLine;
            if (pill.Child is TextBlock tb) tb.Foreground = on ? PillOnFg : PillOffFg;
        }
    }

    private void StyleGradePills()
    {
        for (int i = 0; i < GradePills.Children.Count; i++)
        {
            if (GradePills.Children[i] is not Border pill) continue;
            int ix = i - 1;
            bool on = ix == _gradeIx;
            pill.Background = on ? PillOnBg : PillOffBg;
            pill.BorderBrush = on ? PillOnLine : PillOffLine;
            if (pill.Child is TextBlock tb)
                tb.Foreground = on
                    ? (ix < 0 ? PillOnFg : GradeFg[ix])
                    : PillOffFg;
        }
    }

    private void RefreshMotes()
    {
        StyleGradePills();
        StyleTimePills();
        MoteVerdicts.Children.Clear();
        MoteTableHost.Children.Clear();
        MoteTableHost.RowDefinitions.Clear();
        MoteTableHost.ColumnDefinitions.Clear();

        var all = MoteFarm.Build(_loot.Entries);
        var (farms, thin) = MoteFarm.SplitByFarmed(all, _minFarmed);
        var rows = MoteFarm.Ranked(farms, _gradeIx);
        SummaryText.Text = $"{all.Sum(r => r.Total)} mote(s) across {all.Count} zone(s)";

        foreach (var (text, caveat) in MoteFarm.Verdicts(farms, _gradeIx))
        {
            var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new System.Windows.Documents.Run(caveat ? "◇ " : "◆ ")
            { Foreground = caveat ? MoteDimmerFg : MoteGoldFg, FontSize = 9 });
            line.Inlines.Add(new System.Windows.Documents.Run(text)
            { Foreground = caveat ? MoteDimFg : MoteZoneFg });
            MoteVerdicts.Children.Add(line);
        }

        if (rows.Count == 0 && thin.Count == 0)
        {
            MoteVerdicts.Children.Add(new TextBlock
            {
                Text = _gradeIx >= 0
                    ? $"No {MoteFarm.Grades[_gradeIx]} motes in the ledger yet."
                    : "No motes in the ledger yet — they land here as you loot them.",
                Foreground = MoteDimFg, FontSize = 12,
            });
            return;
        }

        // ZONE · total · per-grade columns · farmed · rate · last
        int gradeCols = MoteFarm.Grades.Length - 1; // Infinite hides until it ever drops
        bool anyInfinite = all.Any(r => r.ByGrade[^1] > 0);
        if (anyInfinite) gradeCols++;

        MoteTableHost.ColumnDefinitions.Add(new ColumnDefinition
        { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < gradeCols + 4; i++)
            MoteTableHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;
        MoteTableHost.RowDefinitions.Add(new RowDefinition());
        MCell("ZONE", row, 0, MoteHeadFg, right: false, size: 9.5, bold: true);
        string[] shortGrade = { "INF", "MIN", "LES", "POT", "GRT", "MAJ", "∞" };
        for (int i = 0; i < gradeCols; i++)
            MCell(shortGrade[i], row, 1 + i, MoteHeadFg, right: true, size: 9.5, bold: true);
        MCell("FARMED", row, 1 + gradeCols, MoteHeadFg, right: true, size: 9.5, bold: true);
        MCell("MOTES/H", row, 2 + gradeCols, MoteHeadFg, right: true, size: 9.5, bold: true);
        MCell("LAST", row, 3 + gradeCols, MoteHeadFg, right: true, size: 9.5, bold: true);
        row++;

        void RenderZoneRow(MoteFarm.ZoneRow r, bool hint)
        {
            bool open = _moteOpen.Contains(r.Zone);
            MoteTableHost.RowDefinitions.Add(new RowDefinition());

            // Zone cell doubles as the fold toggle.
            var zoneText = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            zoneText.Inlines.Add(new System.Windows.Documents.Run(open ? "▾ " : "▸ ")
            { Foreground = MoteGoldFg, FontSize = 10 });
            zoneText.Inlines.Add(new System.Windows.Documents.Run(r.Zone)
            { Foreground = hint ? MoteDimFg : MoteZoneFg, FontWeight = FontWeights.SemiBold });
            var zoneCell = new Border
            {
                BorderBrush = MoteLine,
                BorderThickness = new Thickness(0, 0, 0, open ? 0 : 1),
                Padding = new Thickness(2, 4, 2, 3),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = zoneText,
            };
            var zone = r.Zone;
            zoneCell.MouseLeftButtonDown += (_, _) =>
            {
                if (!_moteOpen.Add(zone)) _moteOpen.Remove(zone);
                RefreshMotes();
            };
            Grid.SetRow(zoneCell, row);
            Grid.SetColumn(zoneCell, 0);
            MoteTableHost.Children.Add(zoneCell);

            for (int i = 0; i < gradeCols; i++)
                MCell(r.ByGrade[i] > 0 ? r.ByGrade[i].ToString() : "—", row, 1 + i,
                    r.ByGrade[i] > 0 && !hint
                        ? (i == _gradeIx || _gradeIx < 0 ? GradeFg[i] : MoteDimFg)
                        : MoteDimmerFg,
                    right: true, noLine: open);
            MCell(MoteFarm.FormatMinutes(r.Minutes), row, 1 + gradeCols, MoteDimFg,
                right: true, noLine: open);
            double? rate = r.RateFor(_gradeIx);
            MCell(rate is { } rv ? $"{rv:0}/h" : "small sample", row, 2 + gradeCols,
                rate is not null && !hint ? MoteRateFg : MoteDimmerFg, right: true,
                size: rate is not null ? 12.5 : 10.5, bold: rate is not null && !hint, noLine: open);
            MCell(r.Last.Date == DateTime.Today ? r.Last.ToString("HH:mm") : r.Last.ToString("dd MMM"),
                row, 3 + gradeCols, MoteDimmerFg, right: true, noLine: open);
            row++;

            if (open)
            {
                MoteTableHost.RowDefinitions.Add(new RowDefinition());
                var detail = new StackPanel { Margin = new Thickness(20, 1, 0, 6) };
                detail.Children.Add(DetailLine("Who pays here:  ",
                    string.Join(" · ", r.Droppers.Take(8).Select(d =>
                        d.Count > 1 ? $"{d.Mob} ×{d.Count}" : d.Mob))
                    + (r.Droppers.Count > 8 ? $" · +{r.Droppers.Count - 8} more" : "")));
                detail.Children.Add(DetailLine("Stints:  ",
                    string.Join("   ·   ", r.Stints.Take(5).Select(s =>
                        $"{s.Start:dd MMM HH:mm} — {MoteFarm.FormatMinutes(Math.Max(1, s.Minutes))} · {s.Total} mote(s)"))
                    + (r.Stints.Count > 5 ? $"   ·   +{r.Stints.Count - 5} more" : "")));
                var host = new Border
                {
                    BorderBrush = MoteLine,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = detail,
                };
                Grid.SetRow(host, row);
                Grid.SetColumnSpan(host, 4 + gradeCols);
                MoteTableHost.Children.Add(host);
                row++;
            }
        }

        foreach (var r in rows) RenderZoneRow(r, hint: false);

        // Zones under the dial collapse into one dim line — hints live
        // below the farms, never ranked beside them (owner ruling, 2 Sep).
        if (thin.Count > 0)
        {
            MoteTableHost.RowDefinitions.Add(new RowDefinition());
            var thinText = new TextBlock { FontSize = 11 };
            thinText.Inlines.Add(new System.Windows.Documents.Run(_thinOpen ? "▾ " : "▸ ")
            { Foreground = MoteGoldFg, FontSize = 10 });
            thinText.Inlines.Add(new System.Windows.Documents.Run(
                $"{thin.Count} zone(s) under {MoteFarm.FormatMinutes(_minFarmed)} farmed — hints, not farms")
            { Foreground = MoteDimFg });
            var thinToggle = new Border
            {
                Padding = new Thickness(2, 8, 2, 4),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = thinText,
            };
            thinToggle.MouseLeftButtonDown += (_, _) =>
            {
                _thinOpen = !_thinOpen;
                RefreshMotes();
            };
            Grid.SetRow(thinToggle, row);
            Grid.SetColumnSpan(thinToggle, 4 + gradeCols);
            MoteTableHost.Children.Add(thinToggle);
            row++;

            if (_thinOpen)
                foreach (var r in thin.OrderByDescending(t => t.Total))
                    RenderZoneRow(r, hint: true);
        }
    }

    private TextBlock DetailLine(string label, string text)
    {
        var tb = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        tb.Inlines.Add(new System.Windows.Documents.Run(label)
        { Foreground = MoteZoneFg, FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = MoteDimFg });
        return tb;
    }

    private void MCell(string text, int row, int col, Brush fg, bool right,
        double size = 12, bool bold = false, bool noLine = false)
    {
        var border = new Border
        {
            BorderBrush = MoteLine,
            BorderThickness = new Thickness(0, 0, 0, noLine ? 0 : 1),
            Padding = new Thickness(col == 0 ? 2 : 9, 4, 2, 3),
            Child = new TextBlock
            {
                Text = text, FontSize = size, Foreground = fg,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        MoteTableHost.Children.Add(border);
    }
}
