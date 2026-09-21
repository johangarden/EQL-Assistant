using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Charmed pets tab (owner, 21 Sep): the <see cref="CharmBook"/> per
/// mob — level, zone, charms and failed attempts, average and longest
/// hold, breaks, damage per hit, DPS, damage taken, kills — and the recent
/// charms one by one. Repaints on every ledger change.
/// </summary>
public partial class CharmsView : UserControl
{
    private static readonly Brush HeadFg = Freeze("#5C6B82");
    private static readonly Brush ValFg = Freeze("#C9D4E3");
    private static readonly Brush DimFg = Freeze("#7F93AD");
    private static readonly Brush MobFg = Freeze("#E8C15A");
    private static readonly Brush Good = Freeze("#81C784");
    private static readonly Brush Bad = Freeze("#E57373");
    private static readonly Brush Line = Freeze("#1F2637");

    private CharmBook? _book;

    public CharmsView()
    {
        InitializeComponent();
        Unloaded += (_, _) => { if (_book is not null) _book.Changed -= OnChanged; };
    }

    public void Init(CharmBook? book)
    {
        if (_book is not null) _book.Changed -= OnChanged;
        _book = book;
        if (_book is not null) _book.Changed += OnChanged;
        Build();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Build);
    private void Search_Changed(object sender, TextChangedEventArgs e) => Build();

    /// <summary>Mob rows painted last — selftest.</summary>
    public int RowCount { get; private set; }

    public void Build()
    {
        MobsHost.Children.Clear();
        RecentHost.Children.Clear();
        if (_book is null) { SummaryText.Text = ""; RowCount = 0; return; }
        string q = SearchBox.Text.Trim();
        var rows = _book.ByMob(q);
        RowCount = rows.Count;
        var all = _book.Episodes;
        SummaryText.Text = all.Count == 0 ? "no charms yet"
            : $"{all.Select(e => e.Mob).Distinct(StringComparer.OrdinalIgnoreCase).Count()} mobs · {all.Count} charms · best hold {CharmWindow.Clock(all.Max(e => e.HeldSec))} on {all.OrderByDescending(e => e.HeldSec).First().Mob}";

        if (rows.Count == 0)
        {
            MobsHost.Children.Add(new TextBlock
            {
                Text = q.Length > 0 ? "Nothing matches the filter." : "No charms recorded yet — the charm card writes a line here each time a charm ends.",
                Foreground = DimFg, FontSize = 12, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            var grid = NewGrid(new double[] { 0, 40, 130, 56, 62, 70, 70, 56, 66, 50, 62, 44, 92 },
                new[] { "MOB", "LVL", "ZONE", "CHARMS", "RESISTED", "AVG HOLD", "LONGEST", "BROKE", "DMG/HIT", "DPS", "TOOK", "KILLS", "LAST" },
                rightFrom: 3, rightTo: 11);
            int r = 1;
            foreach (var s in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                Cell(grid, s.Mob, r, 0, MobFg, bold: true);
                Cell(grid, s.Level > 0 ? s.Level.ToString() : "—", r, 1, s.Level > 0 ? ValFg : DimFg, right: true);
                Cell(grid, s.Zone.Length > 0 ? s.Zone : "—", r, 2, DimFg);
                Cell(grid, s.Charms.ToString(), r, 3, ValFg, right: true);
                Cell(grid, s.Resisted > 0 ? s.Resisted.ToString() : "—", r, 4, s.Resisted > 0 ? Bad : DimFg, right: true);
                Cell(grid, CharmWindow.Clock(s.AvgHeldSec), r, 5, ValFg, right: true);
                Cell(grid, CharmWindow.Clock(s.LongestSec), r, 6, Good, right: true, bold: true);
                Cell(grid, $"{s.Breaks}/{s.Charms}", r, 7, s.Breaks > 0 ? Bad : DimFg, right: true);
                Cell(grid, s.DamagePerHit > 0 ? s.DamagePerHit.ToString("N0") : "—", r, 8, ValFg, right: true);
                Cell(grid, s.Dps > 0 ? s.Dps.ToString("0") : "—", r, 9, ValFg, right: true);
                Cell(grid, s.Taken > 0 ? s.Taken.ToString("N0") : "—", r, 10, s.Taken > 0 ? ValFg : DimFg, right: true);
                Cell(grid, s.Kills.ToString(), r, 11, s.Kills > 0 ? ValFg : DimFg, right: true);
                Cell(grid, s.Last.ToString("d MMM HH:mm"), r, 12, DimFg);
                r++;
            }
            MobsHost.Children.Add(grid);
        }

        var recent = all
            .Where(e => q.Length == 0 || e.Mob.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || e.Zone.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Spell.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.End).Take(30).ToList();
        if (recent.Count == 0)
        {
            RecentHost.Children.Add(new TextBlock { Text = "—", Foreground = DimFg, FontSize = 12, Margin = new Thickness(2, 4, 0, 0) });
            return;
        }
        var g2 = NewGrid(new double[] { 92, 0, 40, 150, 130, 62, 70, 66, 44 },
            new[] { "WHEN", "MOB", "LVL", "SPELL", "ZONE", "HELD", "ENDED", "DAMAGE", "KILLS" }, rightFrom: 5, rightTo: 5);
        int row = 1;
        foreach (var e in recent)
        {
            g2.RowDefinitions.Add(new RowDefinition());
            Cell(g2, e.Start.ToString("d MMM HH:mm"), row, 0, DimFg);
            Cell(g2, e.Mob, row, 1, ValFg);
            Cell(g2, e.MobLevel > 0 ? e.MobLevel.ToString() : "—", row, 2, e.MobLevel > 0 ? ValFg : DimFg, right: true);
            Cell(g2, e.Rank.Length > 0 ? e.Rank : e.Spell, row, 3, DimFg);
            Cell(g2, e.Zone.Length > 0 ? e.Zone : "—", row, 4, DimFg);
            Cell(g2, CharmWindow.Clock(e.HeldSec), row, 5, ValFg, right: true);
            Cell(g2, e.How, row, 6, e.How == "broke" ? Bad : e.How == "died" ? DimFg : ValFg);
            Cell(g2, e.PetDamage > 0 ? $"{e.PetDamage:N0} · {e.PetHits} hits" : "—", row, 7, e.PetDamage > 0 ? ValFg : DimFg);
            Cell(g2, e.PetKills.ToString(), row, 8, e.PetKills > 0 ? ValFg : DimFg, right: true);
            row++;
        }
        RecentHost.Children.Add(g2);
    }

    private static Grid NewGrid(double[] widths, string[] heads, int rightFrom, int rightTo)
    {
        var grid = new Grid();
        foreach (double w in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < heads.Length; c++)
            Cell(grid, heads[c], 0, c, HeadFg, size: 9.5, bold: true, right: c >= rightFrom && c <= rightTo);
        return grid;
    }

    private static void Cell(Grid grid, string text, int row, int col, Brush fg, double size = 11.5, bool bold = false, bool right = false)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = fg, FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(right ? 6 : 2, 3, right ? 2 : 6, 3),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var host = new Border { Child = tb, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, row == 0 ? 1 : 0) };
        Grid.SetRow(host, row); Grid.SetColumn(host, col);
        grid.Children.Add(host);
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
