using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The per-mob resist table (owner pick, 8 Sep): per mob, per spell, landed vs
/// resisted from the log's own resist and landing lines — lifted out of Fight
/// history (29 Sep) so the Tools window can show it too. This zone / all
/// zones, a name filter, the notable verdicts on each mob's head line.
/// LIGHT ON OPEN (owner, 30 Sep: "very heavy on load"): a head per mob is
/// cheap and always painted, but a mob's table is built only once its head is
/// open — the newest few open by themselves, a search opens what it finds —
/// and the heads come a page at a time, newest first.
/// </summary>
public sealed class ResistsView : DockPanel
{
    private static Brush F(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static readonly Brush Immune = F(0xFF, 0x5C, 0x5C), Amber = F(0xFF, 0xB7, 0x4D), SchoolFg = F(0xB3, 0x9D, 0xDB);
    private static readonly Brush HeadFg = F(0x5C, 0x6B, 0x82), GroupFg = F(0xE8, 0xC1, 0x5A), ValFg = F(0xC9, 0xD4, 0xE3),
        DimFg = F(0x7F, 0x93, 0xAD), Best = F(0x81, 0xC7, 0x84), Line = F(0x1F, 0x26, 0x37), Surface = F(0x1B, 0x21, 0x30), Edge = F(0x3A, 0x45, 0x60);

    /// <summary>Heads per page, and how many of the newest mobs open on their own.</summary>
    public const int PageSize = 30, AutoOpen = 5;

    private ResistBook? _book;
    private Func<string> _zone = () => "";
    private bool _thisZone = true; // owner, 30 Sep: this zone first — all zones is the long list
    private readonly StackPanel _host = new();
    private readonly StackPanel _zoneHost = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _search = new() { ToolTip = "Filter by mob or spell name" };
    private Segmented? _zoneSeg;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _closed = new(StringComparer.OrdinalIgnoreCase); // auto-opened heads you folded
    private int _pages = 1;
    private readonly System.Windows.Threading.DispatcherTimer _pause = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public ResistsView()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_zoneHost, Dock.Right);
        top.Children.Add(_zoneHost);
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _host });
        // Typing and live lines coalesce into one rebuild a beat later.
        _pause.Tick += (_, _) => { _pause.Stop(); Build(); };
        _search.TextChanged += (_, _) => { _pages = 1; _pause.Stop(); _pause.Start(); };
        Unloaded += (_, _) => { _pause.Stop(); if (_book is not null) _book.Changed -= OnChanged; };
        Loaded += (_, _) => { if (_book is not null) { _book.Changed -= OnChanged; _book.Changed += OnChanged; } Build(); };
    }

    /// <summary>The book and where you are now (for "This zone").</summary>
    public void Init(ResistBook? book, Func<string> currentZone)
    {
        if (_book is not null) _book.Changed -= OnChanged;
        _book = book; _zone = currentZone;
        if (_book is not null && IsLoaded) _book.Changed += OnChanged;
        Build();
    }

    /// <summary>Selftest: the mob heads painted last, and how many of them carry their table.</summary>
    internal int MobCountForTest { get; private set; }
    internal int TableCountForTest { get; private set; }

    private void OnChanged() => Dispatcher.BeginInvoke(() => { _pause.Stop(); _pause.Start(); });

    public void Build()
    {
        _host.Children.Clear();
        MobCountForTest = 0; TableCountForTest = 0;
        if (_book is null) return;
        if (_zoneSeg is null)
        {
            _zoneSeg = new Segmented(new[] { new Segmented.Option("zone", "This zone"), new Segmented.Option("all", "All zones") }, _thisZone ? "zone" : "all", "#E8C15A")
            { Margin = new Thickness(0) };
            _zoneSeg.Changed += id => { _thisZone = id == "zone"; _pages = 1; Build(); };
            _zoneHost.Children.Add(_zoneSeg);
        }
        string here = _zone();
        // This zone until the log has named one — then every zone, and say so.
        string zone = _thisZone && here.Length > 0 ? here : "";
        string search = _search.Text.Trim();
        if (_thisZone && here.Length == 0)
            _host.Children.Add(new TextBlock { Text = "No zone known yet — showing every zone until you zone once.", Foreground = DimFg, FontSize = 11.5, Margin = new Thickness(2, 2, 0, 0) });
        var groups = _book.ByMob(zone, search);
        if (groups.Count == 0)
        {
            _host.Children.Add(new TextBlock
            {
                Text = search.Length > 0 ? "Nothing matches the filter."
                    : zone.Length > 0 ? $"No casts recorded in {zone} yet — pick All zones for the rest."
                    : "No casts recorded yet — Data → Reparse fills this from your whole log.",
                Foreground = DimFg, FontSize = 12, Margin = new Thickness(2, 6, 0, 0),
            });
            return;
        }
        int shown = Math.Min(groups.Count, _pages * PageSize);
        for (int i = 0; i < shown; i++)
        {
            var (mob, level, mobZone, cells) = groups[i];
            MobCountForTest++;
            // Open: the newest few by themselves (unless you folded them), a search's hits, anything you opened.
            bool open = _open.Contains(mob) || (!_closed.Contains(mob) && (i < AutoOpen || search.Length > 0));
            var head = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 3), TextWrapping = TextWrapping.Wrap, Cursor = Cursors.Hand };
            head.Inlines.Add(new Run(open ? "▾  " : "▸  ") { Foreground = DimFg, FontWeight = FontWeights.Normal });
            head.Inlines.Add(new Run(mob.ToUpperInvariant()) { Foreground = GroupFg });
            head.Inlines.Add(new Run((level > 0 ? $"   Lvl {level}" : "") + (mobZone.Length > 0 ? $"   {mobZone}" : "") + $"   {cells.Sum(c => c.N)} casts · {cells.Count} spell{(cells.Count == 1 ? "" : "s")}")
            { Foreground = HeadFg, FontWeight = FontWeights.Normal });
            foreach (var v in _book.Notable(mob))
                head.Inlines.Add(new Run($"   {(v.Severity == "immune" ? "shrugs off" : "resists")} {v.Spell}{(v.School.Length > 0 ? $" ({v.School})" : "")} {v.Rate * 100:0}% · n {v.N}")
                { Foreground = v.Severity == "immune" ? Immune : Amber, FontWeight = FontWeights.Normal });
            if (!open) head.Inlines.Add(new Run("   click to open") { Foreground = Line, FontWeight = FontWeights.Normal });
            string m = mob;
            head.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (open) { _open.Remove(m); _closed.Add(m); } else { _open.Add(m); _closed.Remove(m); }
                Build();
            };
            _host.Children.Add(head);
            if (!open) continue;
            TableCountForTest++;
            _host.Children.Add(Table(cells));
        }
        if (shown < groups.Count)
        {
            int left = groups.Count - shown;
            var more = new Border
            {
                CornerRadius = new CornerRadius(12), BorderBrush = Edge, BorderThickness = new Thickness(1), Background = Surface, Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(2, 14, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = $"Show {Math.Min(PageSize, left)} more mobs · {left} older", Foreground = DimFg, FontSize = 11.5, FontWeight = FontWeights.SemiBold },
            };
            more.MouseLeftButtonDown += (_, e) => { e.Handled = true; _pages++; Build(); };
            _host.Children.Add(more);
        }
    }

    private static Grid Table(IReadOnlyList<ResistBook.Cell> cells)
    {
        var grid = new Grid();
        foreach (double w in new double[] { 0, 80, 72, 78, 84, 110 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        string[] heads = { "SPELL", "SCHOOL", "LANDED", "RESISTED", "RESIST %", "LAST" };
        grid.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < heads.Length; c++) Cell(grid, heads[c], 0, c, HeadFg, size: 9.5, bold: true, right: c is 2 or 3 or 4);
        int row = 1;
        foreach (var cell in cells)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            string sev = ResistBook.Severity(cell.Rate, cell.N);
            Brush rateFg = sev == "immune" ? Immune : sev == "resistant" ? Amber : sev == "fine" ? Best : DimFg;
            Cell(grid, cell.Spell, row, 0, ValFg);
            Cell(grid, cell.School.Length > 0 ? cell.School : "—", row, 1, cell.School.Length > 0 ? SchoolFg : DimFg);
            Cell(grid, cell.Landed.ToString(), row, 2, ValFg, right: true);
            Cell(grid, cell.Resisted.ToString(), row, 3, cell.Resisted > 0 ? ValFg : DimFg, right: true);
            Cell(grid, cell.N < ResistBook.SampleFloor ? $"{cell.Rate * 100:0}% · n {cell.N}" : $"{cell.Rate * 100:0}%", row, 4, rateFg, right: true, bold: sev is "immune" or "resistant");
            Cell(grid, cell.Last == default ? "" : cell.Last.ToString("dd MMM HH:mm"), row, 5, DimFg);
            row++;
        }
        return grid;
    }

    private static void Cell(Grid grid, string text, int row, int col, Brush fg, bool right = false, double size = 12, bool bold = false)
    {
        var border = new Border
        {
            BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(col == 0 ? 2 : 10, 3, 2, 3),
            Child = new TextBlock
            {
                Text = text, FontSize = size, Foreground = fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Grid.SetRow(border, row); Grid.SetColumn(border, col);
        grid.Children.Add(border);
    }
}
