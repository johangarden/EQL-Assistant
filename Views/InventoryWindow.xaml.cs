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
/// dump in file order — Item | Location | Count — split over three tabs
/// (Items / Exaltations / Focus effects, i.e. the keyring table), one search
/// box for WHAT (item name only; folding the location in makes "ring" match
/// every keyring row) and lane chips for WHERE, rebuilt per tab. A gold
/// coverage line names every storage the dump left unsaid. Reloads itself
/// when the game rewrites the dump. Read-only by design: the dump is the
/// record, this is the reader.
/// </summary>
public partial class InventoryWindow : Window
{
    private sealed record RowVm(string Name, string Location, string? LocationTip, string CountText);

    private static readonly (string Id, string Label)[] Tabs =
    {
        ("items", "Items"),
        ("exalt", "Exaltations"),
        ("focus", "Focus effects"),
    };

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
    private InventoryStore.Dump? _dump;
    private string _tab = "items";
    private string? _lane;            // null = All
    private string? _dumpPath;
    private DateTime _dumpMtime;
    private DateTime? _parsedAt;      // set on watcher-triggered reloads only

    public InventoryWindow(string eqRoot, string charName, string server)
    {
        InitializeComponent();
        _eqRoot = eqRoot;
        _charName = charName;
        _server = server;

        // The game rewrites the file in place; wait for the write to settle.
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); Reload(fromWatch: true); };

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
    public void Reload(bool fromWatch = false)
    {
        _dumpPath = InventoryStore.FindDumpFile(_eqRoot, _charName, _server);
        if (_dumpPath is null)
        {
            _rows = new List<InventoryStore.CarryRow>();
            _dump = null;
            NoDumpPanel.Visibility = Visibility.Visible;
            ResultsList.Visibility = Visibility.Collapsed;
            TabPanel.Children.Clear();
            LanePanel.Children.Clear();
            ParsedText.Visibility = Visibility.Collapsed;
            WarnText.Visibility = Visibility.Collapsed;
            EmptyTabText.Visibility = Visibility.Collapsed;
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

        _dump = InventoryStore.Parse(text);
        (_rows, _) = InventoryStore.CarryAll(_dump);
        if (fromWatch) _parsedAt = DateTime.Now;

        NoDumpPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;

        BuildTabs();
        BuildLaneChips();
        UpdateCoverage();
        UpdateFreshness();
        ApplyFilters();
    }

    private void BuildTabs()
    {
        TabPanel.Children.Clear();
        foreach (var (id, label) in Tabs)
        {
            int n = _rows.Count(r => InventoryStore.TabOf(r) == id);
            AddPill(TabPanel, n > 0 ? $"{label}  {n}" : label, id, id == _tab, picked =>
            {
                _tab = picked!;
                RepaintPills(TabPanel, _tab);
                _lane = null; // a lane picked on one tab means nothing on another
                BuildLaneChips();
                ApplyFilters();
            });
        }
    }

    /// <summary>The gold coverage story: what this dump does NOT speak for.
    /// A missing storage is "the dump does not say", never "empty".</summary>
    private void UpdateCoverage()
    {
        if (_dump is null) return;
        var missing = InventoryStore.MissingStorages(_dump);
        if (missing.Count == 0)
        {
            WarnText.Visibility = Visibility.Collapsed;
        }
        else
        {
            WarnText.Text = "⚠  Not in this dump: " + string.Join(" · ", missing)
                + ".  The game only writes a storage while its window is open — open them in game, then re-type /outputfile inventory.";
            WarnText.Visibility = Visibility.Visible;
        }
    }

    private void BuildLaneChips()
    {
        var tabRows = _rows.Where(r => InventoryStore.TabOf(r) == _tab).ToList();
        var lanes = _dump is null
            ? new List<(string Id, string Label)>()
            : InventoryStore.LanesOf(tabRows, _dump);

        // A vanished lane (tab switch, different dump) degrades the pick to
        // All rather than filtering to nothing.
        if (_lane is not null && lanes.All(l => l.Id != _lane)) _lane = null;

        LanePanel.Children.Clear();
        // One lane needs no chooser (the Focus tab is all keyring).
        LanePanel.Visibility = lanes.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (lanes.Count < 2) return;

        void Pick(string? picked)
        {
            _lane = picked;
            RepaintPills(LanePanel, _lane);
            ApplyFilters();
        }
        AddPill(LanePanel, "All", null, _lane is null, Pick);
        foreach (var (id, label) in lanes) AddPill(LanePanel, label, id, _lane == id, Pick);
    }

    private void AddPill(WrapPanel panel, string label, string? id, bool on, Action<string?> onPick)
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
            Tag = id,
        };
        chip.Background = on ? SegOnBg : Brushes.Transparent;
        text.Foreground = on ? SegOnFg : SegOffFg;
        chip.MouseLeftButtonUp += (_, _) => onPick(id);
        panel.Children.Add(chip);
    }

    private static void RepaintPills(WrapPanel panel, string? activeId)
    {
        foreach (Border b in panel.Children)
        {
            bool isOn = Equals(b.Tag, activeId);
            b.Background = isOn ? SegOnBg : Brushes.Transparent;
            ((TextBlock)b.Child).Foreground = isOn ? SegOnFg : SegOffFg;
        }
    }

    private void UpdateFreshness()
    {
        if (_dumpPath is null) return;
        var age = DateTime.Now - _dumpMtime;
        FreshnessText.Text = "updated " + AgoText(age) + " ago";
        FreshnessText.ToolTip = _dumpPath;
        // A day-old dump answers yesterday's questions — say so in gold.
        FreshnessText.Foreground = age > TimeSpan.FromHours(24)
            ? (Brush)FindResource("Brush.Gold")
            : (Brush)FindResource("Brush.TextHint");

        // The watcher just caught a rewrite: celebrate briefly, then let the
        // ordinary freshness line carry it.
        bool justParsed = _parsedAt is not null && DateTime.Now - _parsedAt < TimeSpan.FromSeconds(90);
        ParsedText.Text = justParsed ? $"✓  Parsed {Path.GetFileName(_dumpPath)} just now." : "";
        ParsedText.Visibility = justParsed ? Visibility.Visible : Visibility.Collapsed;
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
        var tabRows = _rows.Where(r => InventoryStore.TabOf(r) == _tab).ToList();
        var matched = tabRows
            .Where(r => (_lane is null || r.Lane == _lane)
                        && (q.Length == 0 || r.SearchKey.Contains(q, StringComparison.Ordinal)))
            .Select(MakeRowVm)
            .ToList();
        ResultsList.ItemsSource = matched;
        CountText.Text = $"{matched.Count} of {tabRows.Count}";

        // "Table present but empty" and "table never written" are different
        // claims — the first is evidence of nothing owned, the second is
        // silence.
        bool keyringDumped = _dump?.Covered.Contains("keyring") == true;
        EmptyTabText.Text = _tab switch
        {
            "exalt" => "No exaltation sockets in this dump.",
            "focus" when keyringDumped => "No focus effects — the dump's key ring table is there, just empty.",
            "focus" => "No key ring table in this dump — the game didn't write one.",
            _ => "Nothing in this dump.",
        };
        EmptyTabText.Visibility = tabRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>An exaltation's interesting "where" is the item wearing it,
    /// not the socket path — the verbatim spelling stays on the tooltip.</summary>
    private RowVm MakeRowVm(InventoryStore.CarryRow r)
    {
        string count = r.Count > 1 ? $"{r.Count}×" : "";
        if (_tab == "exalt" && r.Host.Length > 0)
            return new RowVm(r.Name, "in " + r.Host, r.Location, count);
        return new RowVm(r.Name, r.Location,
            r.Host.Length > 0 ? "inside " + r.Host : null, count);
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
