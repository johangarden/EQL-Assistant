using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLOverlay.Views;

/// <summary>
/// The fight fact sheet (Fight History's bottom section): pill tabs per actor
/// — You / Pet(s) / Others / What hit you / Healing — over ONE column-based
/// table (dps · total · % · hits · hit% · min · avg · max · crit% · /min),
/// every column sortable by clicking its header. Custom-built, never a stock
/// ListView — every control themed from day one.
/// </summary>
public partial class FightSheetView : UserControl
{
    /// <summary>One table row. Nullable cells render as a dim "—": a spell
    /// with no miss-tracking must not pretend a 100% hit rate.</summary>
    public sealed record SheetRow(string Name, double Dps, double Total, double Pct,
        int Hits, double? HitPct, double? Min, double? Avg, double? Max,
        double? CritPct, double? PerMin);

    /// <summary>One tab. Slim = per-source rows (Others, Healing) where only
    /// dps/total/% exist — the log gives no drill-down for other players.</summary>
    public sealed record SheetTab(string Key, string Title, Brush Accent,
        List<SheetRow> Rows, bool Slim = false);

    private sealed record Col(string Header, Func<SheetRow, IComparable?> Sort,
        Func<SheetRow, string?> Text, bool Left = false);

    private static readonly Brush TabOffFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush CardBg = Freeze(Color.FromRgb(0x1B, 0x21, 0x30));
    private static readonly Brush TabOnBg = Freeze(Color.FromRgb(0x23, 0x2B, 0x40));
    private static readonly Brush CardLine = Freeze(Color.FromRgb(0x3A, 0x45, 0x60));
    private static readonly Brush TabOnLine = Freeze(Color.FromRgb(0x5A, 0x6B, 0x8C));
    private static readonly Brush HeadFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush CellFg = Freeze(Color.FromRgb(0xC9, 0xD4, 0xE3));
    private static readonly Brush NameFg = Freeze(Color.FromRgb(0x9F, 0xB4, 0xD0));
    private static readonly Brush DimFg = Freeze(Color.FromRgb(0x51, 0x5E, 0x74));
    private static readonly Brush RowLine = Freeze(Color.FromRgb(0x1F, 0x26, 0x37));

    private static readonly Col[] FullCols =
    {
        new("ABILITY", r => r.Name, r => r.Name, Left: true),
        new("DPS", r => r.Dps, r => FormatDps(r.Dps)),
        new("TOTAL", r => r.Total, r => r.Total.ToString("N0")),
        new("% DMG", r => r.Pct, r => $"{r.Pct:0}%"),
        new("HITS", r => r.Hits, r => r.Hits.ToString("N0")),
        new("HIT %", r => r.HitPct, r => r.HitPct is { } v ? $"{v:0}%" : null),
        new("MIN", r => r.Min, r => r.Min is { } v ? v.ToString("N0") : null),
        new("AVG", r => r.Avg, r => r.Avg is { } v ? v.ToString("N0") : null),
        new("MAX", r => r.Max, r => r.Max is { } v ? v.ToString("N0") : null),
        new("CRIT %", r => r.CritPct, r => r.CritPct is { } v ? $"{v:0}%" : null),
        new("/MIN", r => r.PerMin, r => r.PerMin is { } v ? v.ToString("0.#") : null),
    };

    private static readonly Col[] SlimCols =
    {
        new("SOURCE", r => r.Name, r => r.Name, Left: true),
        new("DPS", r => r.Dps, r => FormatDps(r.Dps)),
        new("TOTAL", r => r.Total, r => r.Total.ToString("N0")),
        new("%", r => r.Pct, r => $"{r.Pct:0}%"),
    };

    private IReadOnlyList<SheetTab> _tabs = Array.Empty<SheetTab>();
    private string _activeKey = "";
    private int _sortCol = 2;      // TOTAL
    private bool _sortAsc;         // biggest first by default

    public FightSheetView()
    {
        InitializeComponent();
    }

    public void Show(string title, bool showTitle, IReadOnlyList<SheetTab> tabs)
    {
        TitleText.Text = title;
        TitleText.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
        _tabs = tabs.Where(t => t.Rows.Count > 0).ToList();
        if (_tabs.All(t => t.Key != _activeKey))
            _activeKey = _tabs.Count > 0 ? _tabs[0].Key : "";
        BuildTabs();
        BuildTable();
    }

    private void BuildTabs()
    {
        TabsPanel.Children.Clear();
        foreach (var tab in _tabs)
        {
            bool on = tab.Key == _activeKey;
            var pill = new Border
            {
                Background = on ? TabOnBg : CardBg,
                BorderBrush = on ? TabOnLine : CardLine,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(13, 3, 13, 4),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = tab.Title,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = on ? tab.Accent : TabOffFg,
                },
            };
            string key = tab.Key;
            pill.MouseLeftButtonDown += (_, _) =>
            {
                if (_activeKey == key) return;
                _activeKey = key;
                _sortCol = 2;      // fresh tab, biggest first again
                _sortAsc = false;
                BuildTabs();
                BuildTable();
            };
            TabsPanel.Children.Add(pill);
        }
    }

    private void BuildTable()
    {
        TableHost.Children.Clear();
        TableHost.RowDefinitions.Clear();
        TableHost.ColumnDefinitions.Clear();
        var tab = _tabs.FirstOrDefault(t => t.Key == _activeKey);
        if (tab is null) return;

        var cols = tab.Slim ? SlimCols : FullCols;
        if (_sortCol >= cols.Length) { _sortCol = 2; _sortAsc = false; }

        foreach (var c in cols)
            TableHost.ColumnDefinitions.Add(new ColumnDefinition
            { Width = c.Left ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        // Header row — click to sort, click again to flip. Nulls sink.
        TableHost.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < cols.Length; i++)
        {
            int col = i;
            string arrow = _sortCol == i ? (_sortAsc ? " ▲" : " ▼") : "";
            var head = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                BorderBrush = RowLine,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(cols[i].Left ? 2 : 10, 5, cols[i].Left ? 10 : 2, 4),
                Child = new TextBlock
                {
                    Text = cols[i].Header + arrow,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _sortCol == i ? tab.Accent : HeadFg,
                    HorizontalAlignment = cols[i].Left
                        ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                },
            };
            head.MouseLeftButtonDown += (_, _) =>
            {
                if (_sortCol == col) _sortAsc = !_sortAsc;
                else { _sortCol = col; _sortAsc = col == 0; } // names A→Z, numbers big-first
                BuildTable();
            };
            Grid.SetRow(head, 0);
            Grid.SetColumn(head, i);
            TableHost.Children.Add(head);
        }

        var sortSel = cols[_sortCol].Sort;
        var rows = (_sortAsc
                ? tab.Rows.OrderBy(r => sortSel(r) is null).ThenBy(r => sortSel(r))
                : tab.Rows.OrderBy(r => sortSel(r) is null).ThenByDescending(r => sortSel(r)))
            .ToList();

        for (int rI = 0; rI < rows.Count; rI++)
        {
            TableHost.RowDefinitions.Add(new RowDefinition());
            for (int cI = 0; cI < cols.Length; cI++)
            {
                string? text = cols[cI].Text(rows[rI]);
                var cell = new Border
                {
                    BorderBrush = RowLine,
                    BorderThickness = new Thickness(0, 0, 0, rI == rows.Count - 1 ? 0 : 1),
                    Padding = new Thickness(cols[cI].Left ? 2 : 10, 3, cols[cI].Left ? 10 : 2, 3),
                    Child = new TextBlock
                    {
                        Text = text ?? "—",
                        FontSize = 12,
                        FontWeight = cols[cI].Left ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = text is null ? DimFg : cols[cI].Left ? NameFg : CellFg,
                        HorizontalAlignment = cols[cI].Left
                            ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                Grid.SetRow(cell, rI + 1);
                Grid.SetColumn(cell, cI);
                TableHost.Children.Add(cell);
            }
        }
    }

    private static string FormatDps(double v) =>
        v >= 100 ? v.ToString("N0") : v.ToString("0.0");

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
