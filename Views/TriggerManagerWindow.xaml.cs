using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;
using EQLOverlay.ViewModels;

namespace EQLOverlay.Views;

public partial class TriggerManagerWindow : Window
{
    private readonly ConfigService _configService;
    private readonly LogBus _bus;
    private readonly AlertService _alerts;
    private readonly Action<string> _onApplied;

    private AppConfig _config;

    // All loadouts held in memory; persisted only on Save.
    private readonly Dictionary<string, ObservableCollection<TriggerEditViewModel>> _byName = new();
    private readonly ObservableCollection<string> _order = new();
    private string _currentName = "Default";
    private bool _initializing = true;

    private readonly ObservableCollection<string> _recent = new();

    private static readonly Regex TimestampPrefix = new(@"^\[.+?\]\s?", RegexOptions.Compiled);
    private static readonly string[] PresetColors =
    {
        "#4FC3F7", "#BA68C8", "#81C784", "#FFB74D", "#E57373", "#F06292",
        "#64B5F6", "#4DB6AC", "#FFD54F", "#A1887F", "#9575CD", "#E53935",
    };

    private readonly SpellDurations? _durations;

    /// <summary>Set by MainWindow: replays the whole log through the retroactive
    /// services and returns a one-line summary for the status bar.</summary>
    public Func<string>? ReparseFullLogRequested { get; set; }

    /// <summary>Set by MainWindow: wipes derived data files, then reparses.</summary>
    public Func<string>? ResetAndRebuildRequested { get; set; }

    /// <summary>Set by MainWindow: reparse a PICKED file (e.g. another PC's log).</summary>
    public Func<string, string>? ReparseOtherRequested { get; set; }

    private void ReparseOther_Click(object sender, RoutedEventArgs e)
    {
        if (ReparseOtherRequested is null)
        {
            Status("Reparse isn't available right now.");
            return;
        }
        var dlg = new OpenFileDialog
        {
            Title = "Pick a log file to merge in",
            Filter = "Log files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { Status(ReparseOtherRequested(dlg.FileName)); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
    }

    private void ResetRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (ResetAndRebuildRequested is null)
        {
            Status("Reset isn't available right now.");
            return;
        }
        if (!ConfirmDialog.Show(this, "Reset data files & rebuild",
                "This WIPES all log-derived data — loot history, raid kills + drops, " +
                "Plane of Sky progress, seen spells and learned durations — and rebuilds " +
                "everything from a full reparse of the current log file.\n\n" +
                "NOT touched: settings, triggers/loadouts, respawns, raid targets, " +
                "★-kept fights and panel positions.\n\n" +
                "Anything the current log no longer contains (a rotated/deleted old log) " +
                "is lost for good. Continue?",
                yesText: "Reset & rebuild", noText: "Cancel"))
            return;

        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { Status(ResetAndRebuildRequested()); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
    }

    private void Reparse_Click(object sender, RoutedEventArgs e)
    {
        if (ReparseFullLogRequested is null)
        {
            Status("Reparse isn't available right now.");
            return;
        }
        if (!ConfirmDialog.Show(this, "Reparse entire log",
                "Replay the whole log file through loot history, raid kills, Sky quests, " +
                "seen spells and duration learning?\n\nEverything dedupes — nothing is " +
                "counted twice. A large log can take a little while.",
                yesText: "Reparse", noText: "Cancel"))
            return;

        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { Status(ReparseFullLogRequested()); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
    }

    public TriggerManagerWindow(ConfigService configService, AppConfig config,
        LogBus bus, AlertService alerts, RaidKills raids, SpellLibrary spellLibrary,
        CombatParser combat, Action<string> onApplied, SpellDurations? durations = null)
    {
        _durations = durations;
        InitializeComponent();
        WindowTheme.ApplyDark(this);

        _configService = configService;
        _config = config;
        _bus = bus;
        _alerts = alerts;
        _raids = raids;
        _spellLibrary = spellLibrary;
        _combat = combat;
        _onApplied = onApplied;

        // Load every loadout into memory.
        _configService.EnsureDefaultLoadout();
        foreach (var lo in _configService.ListLoadouts())
        {
            _byName[lo.Name] = new ObservableCollection<TriggerEditViewModel>(
                lo.Triggers.Select(TriggerEditViewModel.FromDefinition));
            _order.Add(lo.Name);
        }
        if (_order.Count == 0)
        {
            _byName["Default"] = new ObservableCollection<TriggerEditViewModel>();
            _order.Add("Default");
        }

        LoadoutCombo.ItemsSource = _order;
        LoadoutsList.ItemsSource = _order;
        _currentName = _order.Contains(config.ActiveLoadout) ? config.ActiveLoadout : _order[0];

        // Log feed.
        foreach (var line in bus.Snapshot().Reverse()) _recent.Add(line);
        RecentList.ItemsSource = _recent;
        _bus.LineReceived += OnLine;

        UpdateCharInfo();

        BuildSwatches();
        LoadSettingsFields();
        MuteCheck.IsChecked = _alerts.Muted;
        MuteCheck.Click += (_, _) => _alerts.Muted = MuteCheck.IsChecked == true;

        // Global named respawns (repop page), grouped by zone.
        foreach (var r in _configService.LoadRespawns())
            _respawns.Add(RespawnViewModel.FromEntry(r));
        RespawnList.ItemsSource = _respawns;
        var respawnView = (System.Windows.Data.ListCollectionView)
            System.Windows.Data.CollectionViewSource.GetDefaultView(_respawns);
        respawnView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(RespawnViewModel.ZoneGroup)));
        respawnView.CustomSort = RespawnComparer.Instance; // zones A→Z, zoneless last
        respawnView.IsLiveGrouping = true;
        respawnView.IsLiveSorting = true;
        respawnView.LiveGroupingProperties.Add(nameof(RespawnViewModel.ZoneGroup));
        respawnView.LiveSortingProperties.Add(nameof(RespawnViewModel.ZoneGroup));
        respawnView.LiveSortingProperties.Add(nameof(RespawnViewModel.Name));

        // Recent kills + seen skills pickers — refresh while the window is open.
        RefreshRecentDeaths();
        RefreshSeenSkills();
        _deathsTick = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _deathsTick.Tick += (_, _) => { RefreshRecentDeaths(); RefreshSeenSkills(); UpdateCharInfo(); };
        _deathsTick.Start();
        Closed += (_, _) => _deathsTick.Stop();

        _initializing = false;
        LoadoutCombo.SelectedItem = _currentName; // triggers ShowLoadout via SelectionChanged

        Closed += (_, _) => _bus.LineReceived -= OnLine;
    }

    private ObservableCollection<TriggerEditViewModel> CurrentList => _byName[_currentName];
    private TriggerEditViewModel? Selected => TriggerList.SelectedItem as TriggerEditViewModel;

    // ---- spell library --------------------------------------------------------

    private readonly SpellLibrary _spellLibrary;
    private SpellLibraryWindow? _libraryWindow;

    private void Library_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryWindow is null)
        {
            _libraryWindow = new SpellLibraryWindow(_spellLibrary, AddFromLibrary) { Owner = this };
            _libraryWindow.Closed += (_, _) => _libraryWindow = null;
            _libraryWindow.Show();
        }
        _libraryWindow.Activate();
        _libraryWindow.Focus();
    }

    /// <summary>A library pick lands in the current loadout, selected and ready to tweak.</summary>
    private void AddFromLibrary(TriggerDefinition def)
    {
        // Same spell added twice would collide on id — make it unique.
        if (CurrentList.Any(t => t.Id == def.Id))
            def.Id += "-" + DateTime.Now.Ticks % 10000;

        var vm = TriggerEditViewModel.FromDefinition(def);
        CurrentList.Add(vm);
        TriggerList.SelectedItem = vm;
        TriggerList.ScrollIntoView(vm);
        Status($"Added '{def.Name}' from the library to '{_currentName}' — Save to apply.");
    }

    // ---- global named respawns (repop page) -----------------------------------

    private readonly ObservableCollection<RespawnViewModel> _respawns = new();

    /// <summary>Zoned respawns A→Z by zone then name; zoneless entries sink to the bottom.</summary>
    private sealed class RespawnComparer : System.Collections.IComparer
    {
        public static readonly RespawnComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is not RespawnViewModel a || y is not RespawnViewModel b) return 0;
            bool aNone = string.IsNullOrWhiteSpace(a.Zone);
            bool bNone = string.IsNullOrWhiteSpace(b.Zone);
            if (aNone != bNone) return aNone ? 1 : -1;
            int byZone = string.Compare(a.ZoneGroup, b.ZoneGroup, StringComparison.OrdinalIgnoreCase);
            return byZone != 0 ? byZone : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RespawnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RespawnEditor.DataContext = RespawnList.SelectedItem;
        RespawnEditor.IsEnabled = RespawnList.SelectedItem is not null;
    }

    private void RespawnAdd_Click(object sender, RoutedEventArgs e)
    {
        var vm = new RespawnViewModel
        {
            Name = "New respawn",
            Seconds = 400,
            Zone = _combat.CurrentZone, // where you are is the best guess
        };
        _respawns.Add(vm);
        RespawnList.SelectedItem = vm;
    }

    // ---- recent-kills picker --------------------------------------------------

    private readonly RaidKills _raids;
    private readonly CombatParser _combat;
    private readonly System.Windows.Threading.DispatcherTimer _deathsTick;

    private sealed record DeathItem(string Name, string Zone, string Text)
    {
        public override string ToString() => Text;
    }

    private void RefreshRecentDeaths()
    {
        var items = _raids.RecentDeaths
            .Select(d => new DeathItem(d.Name, d.Zone,
                d.Zone.Length > 0
                    ? $"{d.Name}   ·   {Ago(d.When)}   ·   {d.Zone}"
                    : $"{d.Name}   ·   {Ago(d.When)}"))
            .ToList();

        // Keep the selection stable across refreshes.
        string? selected = (RecentDeathsList.SelectedItem as DeathItem)?.Name;
        RecentDeathsList.ItemsSource = items;
        if (selected is not null)
            RecentDeathsList.SelectedItem = items.FirstOrDefault(i => i.Name == selected);
    }

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        return span.TotalSeconds < 90 ? $"{Math.Max(0, span.TotalSeconds):0}s ago"
            : span.TotalMinutes < 90 ? $"{span.TotalMinutes:0} min ago"
            : $"{when:HH:mm}";
    }

    // ---- seen-skills picker (skill tracker page) --------------------------------

    private sealed record SeenSkillItem(string Name, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Every ability used this session, busiest first — exact log spelling.</summary>
    private void RefreshSeenSkills()
    {
        var selected = (SeenSkillsList.SelectedItem as SeenSkillItem)?.Name;
        var items = _combat.SessionSkills
            .Where(kv => kv.Value.Attempts > 0 || kv.Value.Level > 0)
            .OrderByDescending(kv => kv.Value.Attempts)
            .Take(30)
            .Select(kv =>
            {
                var s = kv.Value;
                string text = s.Attempts > 0
                    ? $"{kv.Key} — {s.Attempts} attempts · {s.HitRate * 100:0}%"
                    : kv.Key;
                if (s.Level > 0) text += $" · skill {s.Level}";
                return new SeenSkillItem(kv.Key, text);
            })
            .ToList();

        if (items.Select(i => i.Text).SequenceEqual(
                SeenSkillsList.Items.Cast<SeenSkillItem>().Select(i => i.Text)))
            return;

        SeenSkillsList.ItemsSource = items;
        if (selected is not null)
            SeenSkillsList.SelectedItem = items.FirstOrDefault(i => i.Name == selected);
    }

    /// <summary>Pick a seen skill → appended to the tracked list with exact spelling.</summary>
    private void SkillFromSeen_Click(object sender, RoutedEventArgs e)
    {
        if (SeenSkillsList.SelectedItem is not SeenSkillItem skill) return;

        var current = SkillListBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (current.Any(s => s.Equals(skill.Name, StringComparison.OrdinalIgnoreCase))) return;

        current.Add(skill.Name);
        SkillListBox.Text = string.Join(", ", current);
        SkillsVisibleCheck.IsChecked = true; // picking a skill implies wanting the section
    }

    /// <summary>Pick a recent kill → it becomes a respawn entry, ready for its time.</summary>
    private void RespawnFromDeath_Click(object sender, RoutedEventArgs e)
    {
        if (RecentDeathsList.SelectedItem is not DeathItem death) return;

        var existing = _respawns.FirstOrDefault(r =>
            r.Name.Equals(death.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RespawnList.SelectedItem = existing; // already tracked — jump to it
            return;
        }

        var vm = new RespawnViewModel { Name = death.Name, Zone = death.Zone, Seconds = 400 };
        _respawns.Add(vm);
        RespawnList.SelectedItem = vm;
        Status($"Added respawn for '{death.Name}' — set its respawn time and Save.");
    }

    private void RespawnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RespawnList.SelectedItem is RespawnViewModel vm)
            _respawns.Remove(vm);
    }

    // ---- loadouts -----------------------------------------------------------

    private void LoadoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (LoadoutCombo.SelectedItem is not string name || !_byName.ContainsKey(name)) return;
        ShowLoadout(name);
    }

    private void ShowLoadout(string name)
    {
        _currentName = name;
        TriggerList.ItemsSource = CurrentList;
        if (CurrentList.Count > 0) TriggerList.SelectedIndex = 0;
        else { DetailsScroller.DataContext = null; DetailsScroller.IsEnabled = false; }

        // The Loadouts page's list is a second face of the same selector.
        if (LoadoutsList is not null && !Equals(LoadoutsList.SelectedItem, name))
            LoadoutsList.SelectedItem = name;
        UpdateLoadoutsInfo();
    }

    private void LoadoutsList_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (LoadoutsList.SelectedItem is string name && name != _currentName)
            LoadoutCombo.SelectedItem = name; // routes through ShowLoadout
    }

    private void UpdateLoadoutsInfo()
    {
        if (LoadoutsInfoText is null) return;
        LoadoutsInfoText.Text =
            $"Editing: {_currentName} ({CurrentList.Count} trigger(s)) · " +
            $"Active in overlay: {_config.ActiveLoadout} — Save makes the edited one active.";
    }

    private void NewLoadout_Click(object sender, RoutedEventArgs e)
    {
        string? name = PromptDialog.Show(this, "New loadout", "Loadout name:", "New Loadout");
        if (name is null) return;
        if (Exists(name)) { WarnExists(name); return; }
        _byName[name] = new ObservableCollection<TriggerEditViewModel>();
        _order.Add(name);
        LoadoutCombo.SelectedItem = name;
        Status($"Created loadout '{name}'.");
    }

    private void RenameLoadout_Click(object sender, RoutedEventArgs e)
    {
        string? name = PromptDialog.Show(this, "Rename loadout", "New name:", _currentName);
        if (name is null || name == _currentName) return;
        if (Exists(name)) { WarnExists(name); return; }

        var list = _byName[_currentName];
        _byName.Remove(_currentName);
        _byName[name] = list;
        int i = _order.IndexOf(_currentName);
        _order[i] = name;
        _currentName = name;
        LoadoutCombo.SelectedItem = name;
        Status($"Renamed to '{name}' (applies on Save).");
    }

    private void DuplicateLoadout_Click(object sender, RoutedEventArgs e)
    {
        string? name = PromptDialog.Show(this, "Duplicate loadout", "New name:", _currentName + " copy");
        if (name is null) return;
        if (Exists(name)) { WarnExists(name); return; }

        var copy = new ObservableCollection<TriggerEditViewModel>(
            CurrentList.Select(vm => TriggerEditViewModel.FromDefinition(vm.ToDefinition())));
        _byName[name] = copy;
        _order.Add(name);
        LoadoutCombo.SelectedItem = name;
        Status($"Duplicated to '{name}'.");
    }

    private void DeleteLoadout_Click(object sender, RoutedEventArgs e)
    {
        if (_order.Count <= 1)
        {
            MessageBox.Show("Keep at least one loadout.", "EQL Assistant",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Delete loadout '{_currentName}'?", "EQL Assistant",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _byName.Remove(_currentName);
        int i = _order.IndexOf(_currentName);
        _order.RemoveAt(i);
        LoadoutCombo.SelectedItem = _order[Math.Min(i, _order.Count - 1)];
        Status("Loadout deleted (applies on Save).");
    }

    private bool Exists(string name) =>
        _order.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private void WarnExists(string name) =>
        MessageBox.Show($"A loadout named '{name}' already exists.", "EQL Assistant",
            MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---- live feed ----------------------------------------------------------

    private void OnLine(string line) => Dispatcher.BeginInvoke(() =>
    {
        if (PauseFeedCheck.IsChecked == true) return;
        _recent.Insert(0, line);
        while (_recent.Count > 300) _recent.RemoveAt(_recent.Count - 1);
    });

    // ---- selection ----------------------------------------------------------

    private void TriggerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailsScroller.DataContext = Selected;
        DetailsScroller.IsEnabled = Selected != null;
        UpdateLearnedHint();
    }

    /// <summary>Title bar carries version + the auto-detected character (+ pet).</summary>
    private void UpdateCharInfo()
    {
        string title = $"EQL Assistant — Manager · v{UpdateService.CurrentVersion.ToString(3)}";
        string self = _combat.SelfName;
        if (!string.IsNullOrEmpty(self) && self != "You") title += $" · Character: {self}";
        string pet = _config.Overlay.PetName;
        if (!string.IsNullOrWhiteSpace(pet)) title += $" · Pet: {pet}";
        if (Title != title) Title = title;
    }

    /// <summary>"Learned so far: 9m53s (9 samples)" under the duration field.</summary>
    private void UpdateLearnedHint()
    {
        if (DurationLearnedText is null) return;
        var learned = Selected is { } s ? _durations?.LearnedMaxSeconds(s.Name) : null;
        if (learned is double sec && Selected is not null)
        {
            DurationLearnedText.Text =
                $"Learned so far: {DurationText.Compact(sec)} ({_durations!.SampleCount(Selected.Name)} sample(s) from your log).";
            DurationLearnedText.Visibility = Visibility.Visible;
        }
        else
        {
            DurationLearnedText.Visibility = Visibility.Collapsed;
        }
    }

    // ---- trigger list buttons ----------------------------------------------

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var t = new TriggerEditViewModel { Name = "New Trigger", Category = "Buffs", Color = "#4FC3F7", DurationSeconds = 60 };
        CurrentList.Add(t);
        TriggerList.SelectedItem = t;
        TriggerList.ScrollIntoView(t);
    }


    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        int i = CurrentList.IndexOf(Selected);
        CurrentList.Remove(Selected);
        if (CurrentList.Count > 0)
            TriggerList.SelectedIndex = Math.Min(i, CurrentList.Count - 1);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int i = TriggerList.SelectedIndex;
        if (i > 0) { CurrentList.Move(i, i - 1); TriggerList.SelectedIndex = i - 1; }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int i = TriggerList.SelectedIndex;
        if (i >= 0 && i < CurrentList.Count - 1) { CurrentList.Move(i, i + 1); TriggerList.SelectedIndex = i + 1; }
    }

    // ---- pattern capture / test --------------------------------------------

    private void UseAsStart_Click(object sender, RoutedEventArgs e) => CaptureInto(isStart: true);
    private void UseAsEnd_Click(object sender, RoutedEventArgs e) => CaptureInto(isStart: false);

    private void CaptureInto(bool isStart)
    {
        if (Selected is null) { Status("Select a trigger first."); return; }
        if (RecentList.SelectedItem is not string line) { Status("Select a log line first."); return; }

        string body = TimestampPrefix.Replace(line, "");
        string pattern = Regex.Escape(body);
        if (isStart) Selected.StartPattern = pattern; else Selected.EndPattern = pattern;
        Status(isStart ? "Filled start pattern." : "Filled end pattern.");
    }

    private void ClearFeed_Click(object sender, RoutedEventArgs e) => _recent.Clear();

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        string line = string.IsNullOrWhiteSpace(TestLineBox.Text)
            ? RecentList.SelectedItem as string ?? ""
            : TestLineBox.Text;
        if (string.IsNullOrWhiteSpace(line)) { TestResult.Text = "Enter or select a line to test."; return; }

        string body = TimestampPrefix.Replace(line, "");
        var hits = new List<string>();
        foreach (var t in CurrentList)
        {
            if (TryMatch(t.StartPattern, body)) hits.Add($"{t.Name} (start)");
            if (!string.IsNullOrWhiteSpace(t.EndPattern) && TryMatch(t.EndPattern, body))
                hits.Add($"{t.Name} (end)");
        }
        TestResult.Text = hits.Count == 0
            ? "No triggers in this loadout match the line."
            : "Matches: " + string.Join(", ", hits);
    }

    private static bool TryMatch(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try { return Regex.IsMatch(input, pattern); } catch { return false; }
    }

    // ---- alert previews -----------------------------------------------------

    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (_alerts.Muted) { Status("Unmute to preview."); return; }
        _alerts.Speak(Selected.AlertSpeak);
    }

    private void PlaySound_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (_alerts.Muted) { Status("Unmute to preview."); return; }
        _alerts.Fire(null, Selected.AlertSound);
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        var dlg = new OpenFileDialog { Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) Selected.AlertSound = dlg.FileName;
    }

    /// <summary>Jump to a sidebar page by its title ("Repop timer", "General", …).</summary>
    public void SelectPage(string title)
    {
        foreach (var item in NavList.Items.OfType<System.Windows.Controls.ListBoxItem>())
            if (string.Equals(item.Content as string, title, StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
                return;
            }
    }

    // ---- sidebar navigation ---------------------------------------------------

    private void NavList_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FlashPage is null) return; // still initializing
        var byTitle = new Dictionary<string, System.Windows.FrameworkElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["Triggers"] = TriggersPage,
            ["Loadouts"] = LoadoutsPage,
            ["Bars & matrices"] = BarsPage,
            ["Repop timer"] = TimerPage,
            ["DPS & Skills"] = MeterPage,
            ["Combat text"] = SctPage,
            ["Flash alerts"] = FlashPage,
            ["Death recap"] = DeathPage,
            ["Respawns"] = RespawnsPage,
            ["General"] = GeneralPage,
            ["Data"] = DataPage,
            ["Shortcuts"] = ShortcutsPage,
        };
        if (NavList.SelectedItem is not System.Windows.Controls.ListBoxItem item
            || !byTitle.TryGetValue(item.Content as string ?? "", out var page))
            return;
        foreach (var p in byTitle.Values) p.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }

    // ---- contextual details form ---------------------------------------------

    private void PanelCombo_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => ApplyPanelVisibility();

    /// <summary>
    /// Show only the fields that mean something for the selected "Show in"
    /// type — a repop trigger has no category, color, or rebuff reminder.
    /// </summary>
    private void ApplyPanelVisibility()
    {
        if (SpeakSoundGroup is null) return; // still initializing

        string p = PanelCombo.SelectedValue as string ?? Panels.Bars;
        bool bars = p == Panels.Bars;
        bool matrix = p is Panels.SelfBuffs or Panels.TargetDebuffs;
        bool timer = p == Panels.TimerAuto;
        bool flash = p == Panels.Flash;

        static Visibility V(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        CategoryGroup.Visibility = V(bars);
        DurationGroup.Visibility = V(bars || matrix || timer);
        ColorGroup.Visibility = V(bars || flash);
        EndGroup.Visibility = V(bars || matrix);
        BarsChecksGroup.Visibility = V(bars);
        WarnGroup.Visibility = V(bars || matrix);
        SpeakSoundGroup.Visibility = V(bars || matrix);

        DurationLabel.Text = timer ? "Respawn time" : "Duration";
        // Auto-learn is a spell-duration concept — respawn timers don't learn.
        DurationAutoCheck.Visibility = V(bars || matrix);
        DurationAutoHint.Visibility = V(bars || matrix);

        // Reordering only matters for matrix triggers (cells lay out in list
        // order); bars always sort themselves by time left.
        MoveUpBtn.Visibility = V(matrix);
        MoveDownBtn.Visibility = V(matrix);
        if (timer) DurationLearnedText.Visibility = Visibility.Collapsed;
        else UpdateLearnedHint();
        StartLabel.Text = timer ? "Death line (regex — starts the repop timer)"
            : flash ? "Pattern (regex — fires the flash)"
            : matrix ? "Start pattern (regex — turns the cell green)"
            : "Start pattern (regex — starts the bar)";
        FlashLabel.Text = flash ? "Flash text (empty = the trigger's name)"
            : "Flash this text on screen when it matches (optional)";
        PanelHint.Text = timer
            ? "When the pattern matches (e.g. a named mob death), the circular repop watch starts with this respawn time. Also listed in the watch's ☰ preset menu."
            : matrix ? "A red/green cell: green with seconds left while active, red when missing."
            : flash ? "Big screen-center text in the trigger's colour that fades out."
            : "A depleting countdown bar in the bars panel, grouped by category.";
    }

    // ---- settings tab -------------------------------------------------------

    private void LoadSettingsFields()
    {
        LogDirBox.Text = _config.Log.Directory;
        FilePatternBox.Text = _config.Log.FilePattern;

        WidthBox.Text = _config.Overlay.Width.ToString(CultureInfo.InvariantCulture);
        BarHeightBox.Text = _config.Overlay.BarHeight.ToString(CultureInfo.InvariantCulture);
        FontSizeBox.Text = _config.Overlay.FontSize.ToString(CultureInfo.InvariantCulture);
        WarnBox.Text = _config.Overlay.WarnSeconds.ToString(CultureInfo.InvariantCulture);
        RemindBox.Text = _config.Overlay.RemindIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        OpacityBox.Text = _config.Overlay.Opacity.ToString(CultureInfo.InvariantCulture);
        MatrixColumnsBox.Text = _config.Overlay.MatrixColumns.ToString(CultureInfo.InvariantCulture);
        ShowHeadersCheck.IsChecked = _config.Overlay.ShowCategoryHeaders;
        StartLockedCheck.IsChecked = _config.Overlay.StartLocked;
        DeathRecapCheck.IsChecked = _config.Overlay.DeathRecapAuto;
        StartWithWindowsCheck.IsChecked = IsAutoStartEnabled();
        TimerVisibleCheck.IsChecked = _config.Overlay.TimerVisible;
        MeterVisibleCheck.IsChecked = _config.Overlay.MeterVisible;
        SkillsVisibleCheck.IsChecked = _config.Overlay.SkillTrackerVisible;
        SkillListBox.Text = string.Join(", ", _config.Overlay.SkillTrackerSkills);
        SctVisibleCheck.IsChecked = _config.Overlay.SctVisible;
        SctProgressCheck.IsChecked = _config.Overlay.SctProgress;
        FlashVisibleCheck.IsChecked = _config.Overlay.FlashVisible;
        PetNameBox.Text = _config.Overlay.PetName;
        FlashFontBox.Text = _config.Overlay.FlashFontSize.ToString(CultureInfo.InvariantCulture);
        FlashWidthBox.Text = _config.Overlay.FlashWidth.ToString(CultureInfo.InvariantCulture);

        SctIncomingCheck.IsChecked = _config.Overlay.SctIncoming;
        SctOutgoingCheck.IsChecked = _config.Overlay.SctOutgoing;
        SctHealsCheck.IsChecked = _config.Overlay.SctHeals;
        SctHealsInCheck.IsChecked = _config.Overlay.SctHealsIn;
        SctPetInCheck.IsChecked = _config.Overlay.SctPetIncoming;
        SctPetOutCheck.IsChecked = _config.Overlay.SctPetOutgoing;
        SctFontBox.Text = _config.Overlay.SctFontSize.ToString(CultureInfo.InvariantCulture);
        SctBigHitBox.Text = _config.Overlay.SctBigHit.ToString(CultureInfo.InvariantCulture);
        SctLaneWidthBox.Text = _config.Overlay.SctLaneWidth.ToString(CultureInfo.InvariantCulture);
        SctLaneHeightBox.Text = _config.Overlay.SctLaneHeight.ToString(CultureInfo.InvariantCulture);

        BarsAnchorBox.SelectedValue = (_configService.LoadPlacement("main")?.Anchor ?? Anchor.TopLeft).ToString();
        SelfAnchorBox.SelectedValue = (_configService.LoadPlacement("selfMatrix")?.Anchor ?? Anchor.TopLeft).ToString();
        TargetAnchorBox.SelectedValue = (_configService.LoadPlacement("targetDebuffs")?.Anchor ?? Anchor.TopLeft).ToString();
        TimerAnchorBox.SelectedValue = (_configService.LoadPlacement("timer")?.Anchor ?? Anchor.TopRight).ToString();
        MeterAnchorBox.SelectedValue = (_configService.LoadPlacement("meter")?.Anchor ?? Anchor.TopRight).ToString();
        FlashAnchorBox.SelectedValue = (_configService.LoadPlacement("flash")?.Anchor ?? Anchor.TopLeft).ToString();
    }

    private void ApplyAnchor(string panel, System.Windows.Controls.ComboBox combo)
    {
        if (combo.SelectedValue is string s && Enum.TryParse<Anchor>(s, out var a))
            _configService.SetPanelAnchor(panel, a);
    }

    private void BrowseLogDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your log folder" };
        if (!string.IsNullOrWhiteSpace(LogDirBox.Text)) dlg.InitialDirectory = LogDirBox.Text;
        if (dlg.ShowDialog(this) == true) LogDirBox.Text = dlg.FolderName;
    }

    // ---- save ---------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Validate every trigger in every loadout.
        var loadouts = new List<Loadout>();
        var errors = new List<string>();
        foreach (var name in _order)
        {
            var defs = new List<TriggerDefinition>();
            foreach (var vm in _byName[name])
            {
                var d = vm.ToDefinition();
                try { ConfigService.CompileOne(d); }
                catch (ArgumentException ex) { errors.Add($"  • [{name}] {d.Name}: {ex.Message}"); }
                defs.Add(d);
            }
            loadouts.Add(new Loadout { Name = name, Triggers = defs });
        }

        if (errors.Count > 0)
        {
            MessageBox.Show("Fix these invalid regex patterns before saving:\n\n" +
                string.Join("\n", errors), "Invalid pattern",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cfg = new AppConfig
        {
            CharacterName = _config.CharacterName, // auto-detected; hand-editable in config.json only
            ActiveLoadout = _currentName,
            Log =
            {
                Directory = LogDirBox.Text.Trim(),
                FilePattern = string.IsNullOrWhiteSpace(FilePatternBox.Text) ? "eqlog_*.txt" : FilePatternBox.Text.Trim(),
                PollIntervalMs = _config.Log.PollIntervalMs,
            },
            Overlay =
            {
                Left = _config.Overlay.Left,
                Top = _config.Overlay.Top,
                Locked = _config.Overlay.Locked,
                Width = ParseOr(WidthBox.Text, _config.Overlay.Width),
                BarHeight = ParseOr(BarHeightBox.Text, _config.Overlay.BarHeight),
                FontSize = ParseOr(FontSizeBox.Text, _config.Overlay.FontSize),
                WarnSeconds = ParseOr(WarnBox.Text, _config.Overlay.WarnSeconds),
                RemindIntervalSeconds = ParseOr(RemindBox.Text, _config.Overlay.RemindIntervalSeconds),
                Opacity = Math.Clamp(ParseOr(OpacityBox.Text, _config.Overlay.Opacity), 0.1, 1.0),
                MatrixColumns = Math.Max(1, (int)ParseOr(MatrixColumnsBox.Text, _config.Overlay.MatrixColumns)),
                ShowCategoryHeaders = ShowHeadersCheck.IsChecked == true,
                StartLocked = StartLockedCheck.IsChecked == true,
                Muted = MuteCheck.IsChecked == true,
                DeathRecapAuto = DeathRecapCheck.IsChecked == true,
                ToolbarVisible = _config.Overlay.ToolbarVisible, // tray-toggled — carried through
                BarsVisible = _config.Overlay.BarsVisible,       // tray-toggled — carried through
                TimerSeconds = _config.Overlay.TimerSeconds, // not edited here — carried through
                TimerVisible = TimerVisibleCheck.IsChecked == true,
                MeterVisible = MeterVisibleCheck.IsChecked == true,
                SkillTrackerVisible = SkillsVisibleCheck.IsChecked == true,
                SkillTrackerSkills = SkillListBox.Text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                PetName = PetNameBox.Text.Trim(),
                FlashVisible = FlashVisibleCheck.IsChecked == true,
                FlashFontSize = Math.Clamp(ParseOr(FlashFontBox.Text, _config.Overlay.FlashFontSize), 10, 200),
                FlashWidth = Math.Clamp(ParseOr(FlashWidthBox.Text, _config.Overlay.FlashWidth), 200, 3000),
                SctVisible = SctVisibleCheck.IsChecked == true,
                SctIncoming = SctIncomingCheck.IsChecked == true,
                SctOutgoing = SctOutgoingCheck.IsChecked == true,
                SctHeals = SctHealsCheck.IsChecked == true,
                SctHealsIn = SctHealsInCheck.IsChecked == true,
                SctPetIncoming = SctPetInCheck.IsChecked == true,
                SctPetOutgoing = SctPetOutCheck.IsChecked == true,
                SctProgress = SctProgressCheck.IsChecked == true,
                SctFontSize = Math.Clamp(ParseOr(SctFontBox.Text, _config.Overlay.SctFontSize), 10, 72),
                SctBigHit = Math.Max(0, ParseOr(SctBigHitBox.Text, _config.Overlay.SctBigHit)),
                SctLaneWidth = Math.Clamp(ParseOr(SctLaneWidthBox.Text, _config.Overlay.SctLaneWidth), 80, 800),
                SctLaneHeight = Math.Clamp(ParseOr(SctLaneHeightBox.Text, _config.Overlay.SctLaneHeight), 100, 1500),
            },
        };

        try
        {
            foreach (var lo in loadouts) _configService.SaveLoadout(lo);
            _configService.SyncDeleteLoadouts(_order);
            _configService.SaveSettings(cfg);
            _configService.SaveRespawns(_respawns.Select(r => r.ToEntry())
                .Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList());
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't write files:\n" + ex.Message, "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ApplyAutoStart(StartWithWindowsCheck.IsChecked == true);

        // Persist panel anchors (offsets are preserved) before the overlay re-applies.
        ApplyAnchor("main", BarsAnchorBox);
        ApplyAnchor("selfMatrix", SelfAnchorBox);
        ApplyAnchor("targetDebuffs", TargetAnchorBox);
        ApplyAnchor("timer", TimerAnchorBox);
        ApplyAnchor("meter", MeterAnchorBox);
        ApplyAnchor("flash", FlashAnchorBox);

        _config = cfg;
        _onApplied(_currentName);
        Status($"Saved {loadouts.Count} loadout(s). Active: {_currentName}.");
    }

    // ---- start with Windows (HKCU Run key) -----------------------------------

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "EQL_Assistant";

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        catch { return false; }
    }

    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enable && Environment.ProcessPath is { } exe)
                key.SetValue(RunValueName, $"\"{exe}\"");
            else if (!enable)
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Start-with-Windows setting failed: " + ex.Message);
        }
    }

    private static double ParseOr(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Status(string msg) => StatusText.Text = msg;

    // ---- color swatches -----------------------------------------------------

    private void BuildSwatches()
    {
        foreach (var hex in PresetColors)
        {
            var btn = new Button
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 3, 3),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Tag = hex,
                ToolTip = hex,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, _) => { if (Selected != null) Selected.Color = hex; };
            SwatchPanel.Children.Add(btn);
        }
    }
}
