using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The searchable carry ledger: every non-empty row of the game's inventory
/// dump in file order — Item | Location | Count — one search box for WHAT
/// (item name only; folding the location in makes "ring" match every keyring
/// row) and lane chips for WHERE. Reloads itself when the game rewrites the
/// dump. Read-only by design: the dump is the record, this is the reader.
/// </summary>
public partial class InventoryWindow : Window
{
    private sealed record RowVm(string Name, string Location, string CountText);

    private static readonly Brush SegOnBg = Freeze("#16283E");
    private static readonly Brush SegOnFg = Freeze("#4FC3F7");
    private static readonly Brush SegOffFg = Freeze("#7F93AD");

    private readonly string _eqRoot;
    private readonly string _charName;
    private readonly string _server;
    private FileSystemWatcher? _fsWatcher;
    private readonly DispatcherTimer _reloadDebounce;
    private readonly DispatcherTimer _freshnessTick;

    private List<InventoryStore.CarryRow> _rows = new();
    private string? _lane;            // null = All
    private string? _dumpPath;
    private DateTime _dumpMtime;

    public InventoryWindow(string eqRoot, string charName, string server)
    {
        InitializeComponent();
        _eqRoot = eqRoot;
        _charName = charName;
        _server = server;

        // The game rewrites the file in place; wait for the write to settle.
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); Reload(); };

        _freshnessTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _freshnessTick.Tick += (_, _) => UpdateFreshness();

        Loaded += (_, _) => { Reload(); StartWatching(); _freshnessTick.Start(); };
        Closed += (_, _) =>
        {
            _reloadDebounce.Stop();
            _freshnessTick.Stop();
            _fsWatcher?.Dispose();
            _fsWatcher = null;
        };
    }

    private void StartWatching()
    {
        if (_eqRoot.Length == 0 || !Directory.Exists(_eqRoot)) return;
        try
        {
            _fsWatcher = new FileSystemWatcher(_eqRoot, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            void OnAny(object s, FileSystemEventArgs e)
            {
                if (!e.Name?.EndsWith("-Inventory.txt", StringComparison.OrdinalIgnoreCase) ?? true)
                    return;
                Dispatcher.BeginInvoke(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
            }
            _fsWatcher.Changed += OnAny;
            _fsWatcher.Created += OnAny;
            _fsWatcher.Renamed += (s, e) => OnAny(s, e);
            _fsWatcher.EnableRaisingEvents = true;
        }
        catch { /* watching is a convenience; manual reopen still works */ }
    }

    /// <summary>Find, read and re-render the freshest dump. Public so a test
    /// or the host can force it.</summary>
    public void Reload()
    {
        _dumpPath = InventoryStore.FindDumpFile(_eqRoot, _charName, _server);
        if (_dumpPath is null)
        {
            _rows = new List<InventoryStore.CarryRow>();
            NoDumpPanel.Visibility = Visibility.Visible;
            ResultsList.Visibility = Visibility.Collapsed;
            LanePanel.Children.Clear();
            FreshnessText.Text = "not yet run";
            CountText.Text = "";
            return;
        }

        string text;
        try
        {
            using var fs = new FileStream(_dumpPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
            _dumpMtime = File.GetLastWriteTime(_dumpPath);
        }
        catch
        {
            return; // mid-write; the debounce will bring us back
        }

        var dump = InventoryStore.Parse(text);
        var (rows, lanes) = InventoryStore.CarryAll(dump);
        _rows = rows;

        NoDumpPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;

        // Lane chips: All + every lane that has rows. A vanished lane
        // (different dump) degrades the pick to All rather than filtering
        // to nothing.
        if (_lane is not null && lanes.All(l => l.Id != _lane)) _lane = null;
        LanePanel.Children.Clear();
        AddChip("All", null);
        foreach (var (id, label) in lanes) AddChip(label, id);

        UpdateFreshness();
        ApplyFilters();
    }

    private void AddChip(string label, string? laneId)
    {
        var text = new TextBlock { Text = label, FontSize = 11 };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(0, 0, 5, 0),
            BorderBrush = Freeze("#3A4560"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = text,
            Tag = laneId,
        };
        bool on = _lane == laneId;
        chip.Background = on ? SegOnBg : Brushes.Transparent;
        text.Foreground = on ? SegOnFg : SegOffFg;
        chip.MouseLeftButtonUp += (_, _) =>
        {
            _lane = laneId;
            foreach (Border b in LanePanel.Children)
            {
                bool isOn = Equals(b.Tag, _lane);
                b.Background = isOn ? SegOnBg : Brushes.Transparent;
                ((TextBlock)b.Child).Foreground = isOn ? SegOnFg : SegOffFg;
            }
            ApplyFilters();
        };
        LanePanel.Children.Add(chip);
    }

    private void UpdateFreshness()
    {
        if (_dumpPath is null) return;
        FreshnessText.Text = "updated " + AgoText(DateTime.Now - _dumpMtime) + " ago";
        FreshnessText.ToolTip = _dumpPath;
    }

    private static string AgoText(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalHours >= 48) return $"{(int)age.TotalDays}d";
        if (age.TotalMinutes >= 60) return $"{(int)age.TotalHours}h {age.Minutes}m";
        if (age.TotalSeconds >= 60) return $"{(int)age.TotalMinutes}m";
        return $"{(int)age.TotalSeconds}s";
    }

    private void Filters_Changed(object sender, TextChangedEventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        string q = SearchBox.Text.Trim().ToLowerInvariant();
        var matched = _rows
            .Where(r => (_lane is null || r.Lane == _lane)
                        && (q.Length == 0 || r.SearchKey.Contains(q, StringComparison.Ordinal)))
            .Select(r => new RowVm(r.Name, r.Location, r.Count > 1 ? $"{r.Count}×" : ""))
            .ToList();
        ResultsList.ItemsSource = matched;
        CountText.Text = $"{matched.Count} of {_rows.Count}";
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
