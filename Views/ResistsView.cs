using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The per-mob resist table (owner pick, 8 Sep): per mob, per spell, landed vs
/// resisted from the log's own resist and landing lines — lifted out of Fight
/// history (29 Sep) so the Tools window can show it too. This zone / all
/// zones, a name filter, the notable verdicts on each mob's head line.
/// </summary>
public sealed class ResistsView : DockPanel
{
    private static Brush F(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static readonly Brush Immune = F(0xFF, 0x5C, 0x5C), Amber = F(0xFF, 0xB7, 0x4D), SchoolFg = F(0xB3, 0x9D, 0xDB);
    private static readonly Brush HeadFg = F(0x5C, 0x6B, 0x82), GroupFg = F(0xE8, 0xC1, 0x5A), ValFg = F(0xC9, 0xD4, 0xE3),
        DimFg = F(0x7F, 0x93, 0xAD), Best = F(0x81, 0xC7, 0x84), Line = F(0x1F, 0x26, 0x37);

    private ResistBook? _book;
    private Func<string> _zone = () => "";
    private bool _thisZone;
    private readonly StackPanel _host = new();
    private readonly StackPanel _zoneHost = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _search = new() { ToolTip = "Filter by mob or spell name" };
    private Segmented? _zoneSeg;

    public ResistsView()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_zoneHost, Dock.Right);
        top.Children.Add(_zoneHost);
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _host });
        _search.TextChanged += (_, _) => Build();
        Unloaded += (_, _) => { if (_book is not null) _book.Changed -= OnChanged; };
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

    /// <summary>Selftest: the mob heads painted last.</summary>
    internal int MobCountForTest { get; private set; }

    private void OnChanged() => Dispatcher.BeginInvoke(Build);

    public void Build()
    {
        _host.Children.Clear();
        MobCountForTest = 0;
        if (_book is null) return;
        if (_zoneSeg is null)
        {
            _zoneSeg = new Segmented(new[] { new Segmented.Option("zone", "This zone"), new Segmented.Option("all", "All zones") }, _thisZone ? "zone" : "all", "#E8C15A")
            { Margin = new Thickness(0) };
            _zoneSeg.Changed += id => { _thisZone = id == "zone"; Build(); };
            _zoneHost.Children.Add(_zoneSeg);
        }
        string here = _zone();
        string zone = _thisZone ? here : "";
        var groups = _book.ByMob(zone, _search.Text.Trim());
        if (groups.Count == 0)
        {
            _host.Children.Add(new TextBlock
            {
                Text = _thisZone && here.Length == 0 ? "No zone known yet — zone once, or pick All zones."
                    : _search.Text.Trim().Length > 0 ? "Nothing matches the filter."
                    : "No casts recorded yet — Data → Reparse fills this from your whole log.",
                Foreground = DimFg, FontSize = 12, Margin = new Thickness(2, 6, 0, 0),
            });
            return;
        }
        foreach (var (mob, level, mobZone, cells) in groups)
        {
            MobCountForTest++;
            var head = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 3), TextWrapping = TextWrapping.Wrap };
            head.Inlines.Add(new Run(mob.ToUpperInvariant()) { Foreground = GroupFg });
            head.Inlines.Add(new Run((level > 0 ? $"   Lvl {level}" : "") + (mobZone.Length > 0 ? $"   {mobZone}" : "") + $"   {cells.Sum(c => c.N)} casts")
            { Foreground = HeadFg, FontWeight = FontWeights.Normal });
            foreach (var v in _book.Notable(mob))
                head.Inlines.Add(new Run($"   {(v.Severity == "immune" ? "shrugs off" : "resists")} {v.Spell}{(v.School.Length > 0 ? $" ({v.School})" : "")} {v.Rate * 100:0}% · n {v.N}")
                { Foreground = v.Severity == "immune" ? Immune : Amber, FontWeight = FontWeights.Normal });
            _host.Children.Add(head);

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
            _host.Children.Add(grid);
        }
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
