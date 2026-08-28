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
        RefreshMergedLogs();
    }

    // ---- stored merge copies (Data page) --------------------------------------

    private sealed record MergedLogItem(string Path)
    {
        public override string ToString()
        {
            var fi = new System.IO.FileInfo(Path);
            return $"{fi.Name}   ·   {fi.Length / 1048576.0:0.0} MB   ·   {fi.LastWriteTime:dd MMM yyyy HH:mm}";
        }
    }

    private void RefreshMergedLogs()
    {
        if (MergedLogsList is null) return;
        var items = _configService.ListMergedLogs().Select(f => new MergedLogItem(f)).ToList();
        MergedLogsList.ItemsSource = items;
        MergedLogsList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MergedLogsEmptyText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        RemoveMergedBtn.IsEnabled = items.Count > 0;
    }

    private void RemoveMerged_Click(object sender, RoutedEventArgs e)
    {
        if (MergedLogsList.SelectedItem is not MergedLogItem item) { Status("Select a file first."); return; }
        try
        {
            System.IO.File.Delete(item.Path);
            Status($"Removed {System.IO.Path.GetFileName(item.Path)} — the next Reset & rebuild won't include it.");
        }
        catch (Exception ex) { Status("Couldn't remove: " + ex.Message); }
        RefreshMergedLogs();
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
                "everything from a full reparse of the current log file PLUS every " +
                "stored additional log file.\n\n" +
                "NOT touched: settings, triggers/loadouts, respawns, raid targets, " +
                "★-kept fights and panel positions.\n\n" +
                "Anything that is in neither the current log nor a stored file " +
                "(a rotated/deleted old log that was never merged in) is lost for good. Continue?",
                yesText: "Reset & rebuild", noText: "Cancel"))
            return;

        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { Status(ResetAndRebuildRequested()); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        UpdateDurationUx();
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
        UpdateDurationUx(); // learned-duration hint may have new samples now
    }

    public TriggerManagerWindow(ConfigService configService, AppConfig config,
        LogBus bus, AlertService alerts, RaidKills raids, SpellLibrary spellLibrary,
        CombatParser combat, Action<string> onApplied, SpellDurations? durations = null,
        Func<string>? logStatus = null)
    {
        _durations = durations;
        _logStatus = logStatus;
        InitializeComponent();
        DialogPlacement.Persist(this, "manager");
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
            // Library triggers created before 2.9 only knew Buffs/Debuffs —
            // re-derive their type (HoTs/DoTs) from the spell data.
            spellLibrary.HealLibraryTriggers(lo.Triggers);

            // Stable type-sort so the grouped list shows each type once, in a
            // fixed order (bars types, then matrices, repops, flashes) —
            // manual matrix ordering survives inside its group.
            _byName[lo.Name] = new ObservableCollection<TriggerEditViewModel>(
                lo.Triggers.Select(TriggerEditViewModel.FromDefinition)
                    .OrderBy(vm => vm.GroupRank));
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

        PopulateSoundPresets();
        RefreshMergedLogs();
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
        _deathsTick.Tick += (_, _) =>
        {
            RefreshRecentDeaths();
            RefreshSeenSkills();
            UpdateCharInfo();
            UpdateDurationUx(); // live play mints samples while the editor is open
            UpdateDirtyCta();   // Save lights up when edits appear
        };
        _deathsTick.Start();
        Closed += (_, _) => _deathsTick.Stop();

        _initializing = false;
        LoadoutCombo.SelectedItem = _currentName; // triggers ShowLoadout via SelectionChanged

        Closed += (_, _) => _bus.LineReceived -= OnLine;

        // Unsaved-changes guard: the clean state is what the UI would save
        // RIGHT NOW; closing with a different answer asks first.
        _cleanFingerprint = Fingerprint();
        Closing += OnClosingConfirm;
        UpdateDirtyCta(clean: true);
    }

    /// <summary>The CTA follows the state: Save wears the primary style while
    /// changes are unsaved, Close while there is nothing to save. Re-checked
    /// on the same 2s tick that refreshes the recent-deaths list.</summary>
    private void UpdateDirtyCta(bool? clean = null)
    {
        bool isClean = clean ?? Fingerprint() == _cleanFingerprint;
        var primary = (Style)FindResource("PrimaryBtn");
        var normal = (Style)FindResource(typeof(System.Windows.Controls.Button));
        SaveBtn.Style = isClean ? normal : primary;
        CloseBtn.Style = isClean ? primary : normal;
    }

    // ---- discard-changes guard -----------------------------------------------

    private string _cleanFingerprint = "";

    /// <summary>Everything Save would write, serialized — comparing this at
    /// close against the last-saved snapshot IS the dirty check (no per-field
    /// tracking to forget).</summary>
    private string Fingerprint()
    {
        try
        {
            var doc = new
            {
                Loadouts = BuildLoadouts(null),
                Config = BuildConfigFromUi(),
                Respawns = _respawns.Select(r => r.ToEntry()).ToList(),
                Anchors = new[]
                    {
                        BarsAnchorBox, EnemyDotsAnchorBox, RemindersAnchorBox, SelfAnchorBox,
                        TargetAnchorBox, TimerAnchorBox, MeterAnchorBox, FlashAnchorBox,
                    }
                    .Select(b => b.SelectedValue as string ?? "").ToList(),
                AutoStart = StartWithWindowsCheck.IsChecked == true,
            };
            return System.Text.Json.JsonSerializer.Serialize(doc);
        }
        catch
        {
            return Guid.NewGuid().ToString(); // un-buildable UI state = dirty
        }
    }

    private void OnClosingConfirm(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Fingerprint() == _cleanFingerprint) return;
        if (!ConfirmDialog.Show(this, "Discard changes?",
                "You have unsaved changes in this window.\n\n"
                + "Close and discard them? (Save & apply keeps them.)",
                "Discard", "Keep editing"))
            e.Cancel = true;
    }

    private ObservableCollection<TriggerEditViewModel> CurrentList => _byName[_currentName];
    private TriggerEditViewModel? Selected => TriggerList.SelectedItem as TriggerEditViewModel;

    // ---- spell library --------------------------------------------------------

    private readonly SpellLibrary _spellLibrary;
    private readonly Func<string>? _logStatus;
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

    private RespawnViewModel? SelectedRespawn => RespawnList.SelectedItem as RespawnViewModel;

    private void RespawnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RespawnEditor.DataContext = RespawnList.SelectedItem;
        RespawnEditor.IsEnabled = RespawnList.SelectedItem is not null;
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

        var vm = new RespawnViewModel { Name = death.Name, Zone = death.Zone, Seconds = 0 };
        _respawns.Add(vm);
        RespawnList.SelectedItem = vm;
        Status($"Added respawn for '{death.Name}' — Save, and its time will be learned from your kills (or type one).");
    }

    /// <summary>"Forget learned times" — clears the gap evidence; the next
    /// kill cycle starts measuring fresh.</summary>
    private void RespawnForgetGaps_Click(object sender, RoutedEventArgs e) =>
        SelectedRespawn?.ClearGaps();

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
        // Grouped by type with live shaping — changing a trigger's panel or
        // category moves it to the right section immediately.
        var view = new System.Windows.Data.ListCollectionView(CurrentList) { IsLiveGrouping = true };
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(
            nameof(TriggerEditViewModel.GroupLabel)));
        view.LiveGroupingProperties.Add(nameof(TriggerEditViewModel.GroupLabel));
        TriggerList.ItemsSource = view;
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
        // PanelCombo only raises SelectionChanged when the panel actually
        // differs — the library/manual gating must re-evaluate every time.
        ApplyPanelVisibility();
        UpdateDurationUx();
        UpdateAnchorUx();
        UpdateLiveLogUx();
        UpdateSoundUx();
    }

    // ---- notification sound picker -------------------------------------------
    // Windows ships ~40 notification wavs in <windir>\Media on every install —
    // a ready sound library with nothing to bundle or copy between machines.

    private sealed record SoundPreset(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    private bool _soundUxLoading;

    private static List<SoundPreset> LoadSoundPresets()
    {
        var items = new List<SoundPreset> { new("(no sound)", "") };
        try
        {
            string media = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            items.AddRange(System.IO.Directory.GetFiles(media, "*.wav")
                .Select(f => new SoundPreset(System.IO.Path.GetFileNameWithoutExtension(f), f))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch { /* no Media folder — the combo just offers (no sound) */ }
        return items;
    }

    private void PopulateSoundPresets()
    {
        // Separate lists per combo — two Selectors sharing one IEnumerable
        // share its default collection view, which would sync their selections.
        WarnSoundBox.ItemsSource = LoadSoundPresets();
        FadedSoundBox.ItemsSource = LoadSoundPresets();
        RespawnWarnSoundBox.ItemsSource = LoadSoundPresets();
        RespawnSpawnSoundBox.ItemsSource = LoadSoundPresets();
        InterruptSoundBox.ItemsSource = LoadSoundPresets();
        ResistSoundBox.ItemsSource = LoadSoundPresets();
    }

    private void WarnSound_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_soundUxLoading || Selected is null) return;
        if (WarnSoundBox.SelectedItem is SoundPreset p) Selected.WarnSound = p.Path;
    }

    private void FadedSound_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_soundUxLoading || Selected is null) return;
        if (FadedSoundBox.SelectedItem is SoundPreset p) Selected.FadedSound = p.Path;
    }

    /// <summary>Sync both pickers to the selected trigger: a preset shows by
    /// name, an unknown path leaves the combo blank.</summary>
    private void UpdateSoundUx()
    {
        _soundUxLoading = true;
        SyncSoundCombo(WarnSoundBox, Selected?.WarnSound);
        SyncSoundCombo(FadedSoundBox, Selected?.FadedSound);
        _soundUxLoading = false;
    }

    private static void SyncSoundCombo(System.Windows.Controls.ComboBox box, string? path)
    {
        if (box?.ItemsSource is not List<SoundPreset> items) return;
        box.SelectedItem = items.FirstOrDefault(p =>
            p.Path.Equals(path ?? "", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The live-log capture panel is manual-trigger tooling — library
    /// adds arrive with correct patterns, so it hides for them (and when
    /// nothing is selected).</summary>
    private void UpdateLiveLogUx()
    {
        if (LiveLogGroup is null || LiveLogRow is null) return;
        bool show = Selected is { IsLibrary: false };
        LiveLogGroup.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LiveLogRow.Height = new GridLength(show ? 210 : 0);
    }

    /// <summary>Title bar carries the face + version + character (+ pet).</summary>
    private void UpdateCharInfo()
    {
        string face = _mode == "triggers" ? "Triggers" : "Settings";
        string title = $"EQL Assistant — {face} · v{UpdateService.CurrentVersion.ToString(3)}";
        string self = _combat.SelfName;
        if (!string.IsNullOrEmpty(self) && self != "You") title += $" · Character: {self}";
        string pet = _config.Overlay.PetName;
        if (!string.IsNullOrWhiteSpace(pet)) title += $" · Pet: {pet}";
        if (Title != title) Title = title;
    }

    private void DurationAuto_Changed(object sender, RoutedEventArgs e) => UpdateDurationUx();

    private bool _anchorUxLoading;

    private void CastAnchor_Changed(object sender, RoutedEventArgs e)
    {
        if (_anchorUxLoading || Selected is null) return;
        Selected.CastAnchored = CastAnchorCheck.IsChecked == true; // explicit from here on
        UpdateAnchorUx();
    }

    /// <summary>The cast-anchor checkbox shows the EFFECTIVE state: an untouched
    /// trigger is on auto — EQL is solo-first, so EVERY library trigger anchors
    /// itself (a groupmate's buff landing on you starts nothing); untick to opt
    /// into group play. Clicking stores an explicit choice.</summary>
    private void UpdateAnchorUx()
    {
        if (CastAnchorCheck is null || CastAnchorHint is null) return;
        if (Selected is null) { CastAnchorHint.Text = ""; return; }

        bool shared = _spellLibrary.IsSharedLanding(Selected.StartPattern);
        bool effective = Selected.CastAnchored
            ?? Selected.Id.StartsWith("lib-", StringComparison.Ordinal);

        _anchorUxLoading = true;
        CastAnchorCheck.IsChecked = effective;
        _anchorUxLoading = false;

        string castLine = $"\"You begin casting {Selected.Name}.\"";
        string sharedNote = shared
            ? " Several spells print this exact landing text (all hastes say the same line), so unticked the bar is a coin flip."
            : "";
        CastAnchorHint.Text = Selected.CastAnchored is null
            ? effective
                ? $"Auto: ON — solo-first: the bar only starts within 15s of your own {castLine} (Quick Buff bursts count). Untick if someone else casts this on you in a group.{sharedNote}"
                : $"Off — manually created triggers fire on any match. Tick to require your own {castLine} first.{sharedNote}"
            : effective
                ? $"On — the bar only starts within 15s of your own {castLine}"
                : $"Off — any matching line starts the bar, including someone else's cast landing on you.{sharedNote}";
    }

    private void Permanent_Changed(object sender, RoutedEventArgs e) => UpdateDurationUx();

    /// <summary>Auto-learn owns the duration: the field is disabled and the
    /// currently-learned value shows beside it. Manual re-enables the field.
    /// A PERMANENT buff owns it harder: no duration at all, the bar shows ∞.</summary>
    private void UpdateDurationUx()
    {
        if (DurationBox is null || DurationEffectiveText is null) return;

        bool permanent = PermanentCheck.IsChecked == true
                         && PermanentCheck.Visibility == Visibility.Visible;
        DurationAutoCheck.IsEnabled = !permanent;
        DurationForgetBtn.IsEnabled = !permanent;

        // The nudge: the library states NO duration for this spell — the one
        // moment the Permanent flag is worth considering (282 of 757 buffs sit
        // at 0, mostly just unstated, so this must stay a hint, never a guess).
        bool zeroDuration = Selected is { } s
            && PermanentCheck.Visibility == Visibility.Visible
            && _spellLibrary.FindByBaseName(SpellDurations.BaseName(s.Name)) is { DurationSec: <= 0 };
        PermanentHint.Visibility = zeroDuration && !permanent
            ? Visibility.Visible : Visibility.Collapsed;
        if (permanent)
        {
            DurationBox.IsEnabled = false;
            DurationEffectiveText.Text = "permanent — the bar shows ∞ until death";
            DurationForgetBtn.Visibility = Visibility.Collapsed;
            return;
        }

        bool auto = DurationAutoCheck.IsChecked == true
                    && DurationAutoCheck.Visibility == Visibility.Visible;
        DurationBox.IsEnabled = !auto;

        if (!auto || Selected is null)
        {
            DurationEffectiveText.Text = "";
            DurationForgetBtn.Visibility = Visibility.Collapsed;
            return;
        }
        double? eff = _durations?.LearnedMaxSeconds(Selected.Name);
        double? raw = _durations?.ObservedMaxSeconds(Selected.Name);
        int n = _durations?.SampleCount(Selected.Name) ?? 0;
        DurationForgetBtn.Visibility = raw is not null ? Visibility.Visible : Visibility.Collapsed;
        DurationEffectiveText.Text = eff is { } sec
            ? $"learning → currently {DurationText.Compact(sec)} ({n} samples)"
            : raw is { } r && _durations?.LibraryFloorSeconds(Selected.Name) is { } floor
                // Shared landing/wear-off sentences read short (a lesser regen
                // crossing a Chloroplast) — the evidence shows, but never rules.
                ? $"observed {DurationText.Compact(r)} ignored — below the library's {DurationText.Compact(floor)} ({n} samples)"
                : "learning → nothing observed yet, starts from this value";
    }

    /// <summary>Owner ruling: a polluted learned number needs a way back —
    /// wipe this spell's samples and let the log teach it again.</summary>
    private void DurationForget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        _durations?.Forget(Selected.Name);
        UpdateDurationUx();
        Status($"Forgot learned duration for '{Selected.Name}'.");
    }

    // ---- trigger list buttons ----------------------------------------------

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var t = new TriggerEditViewModel { Name = "New Trigger", Category = "Buffs", DurationSeconds = 60 };
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

    // Reorder against the UNDERLYING list (the grouped view's indices differ).
    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) return;
        int i = CurrentList.IndexOf(sel);
        if (i > 0) { CurrentList.Move(i, i - 1); TriggerList.SelectedItem = sel; }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) return;
        int i = CurrentList.IndexOf(sel);
        if (i >= 0 && i < CurrentList.Count - 1) { CurrentList.Move(i, i + 1); TriggerList.SelectedItem = sel; }
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

    private void WarnSpeak_Click(object sender, RoutedEventArgs e) => Preview(Selected?.WarnSpeak, null);
    private void FadedSpeak_Click(object sender, RoutedEventArgs e) => Preview(Selected?.FadedSpeak, null);
    private void WarnPlay_Click(object sender, RoutedEventArgs e) => Preview(null, Selected?.WarnSound);
    private void FadedPlay_Click(object sender, RoutedEventArgs e) => Preview(null, Selected?.FadedSound);

    private void Preview(string? speak, string? sound)
    {
        if (Selected is null) return;
        if (_alerts.Muted) { Status("Unmute to preview."); return; }
        _alerts.Fire(speak, sound);
    }

    // ---- the GLOBAL spawn-timer notices (one setting for every watched mob) ---

    private void RespawnNotice_Changed(object sender, RoutedEventArgs e) => UpdateRespawnNoticeUx();

    /// <summary>Rows follow their checkbox; the mode picks which input shows
    /// (phrase OR sound preset — one channel, never both).</summary>
    private void UpdateRespawnNoticeUx()
    {
        if (RespawnWarnRow is null || RespawnSpawnRow is null) return;
        RespawnWarnRow.IsEnabled = RespawnWarnOnCheck.IsChecked == true;
        RespawnSpawnRow.IsEnabled = RespawnSpawnOnCheck.IsChecked == true;
        bool wSound = RespawnWarnModeBox.SelectedValue as string == "sound";
        RespawnWarnSpeakBox.Visibility = wSound ? Visibility.Collapsed : Visibility.Visible;
        RespawnWarnSoundBox.Visibility = wSound ? Visibility.Visible : Visibility.Collapsed;
        bool sSound = RespawnSpawnModeBox.SelectedValue as string == "sound";
        RespawnSpawnSpeakBox.Visibility = sSound ? Visibility.Collapsed : Visibility.Visible;
        RespawnSpawnSoundBox.Visibility = sSound ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RespawnWarnTest_Click(object sender, RoutedEventArgs e) =>
        TestRespawnNotice(RespawnWarnModeBox, RespawnWarnSpeakBox, RespawnWarnSoundBox,
            Models.RespawnNotice.DefaultWarnPhrase);

    private void RespawnSpawnTest_Click(object sender, RoutedEventArgs e) =>
        TestRespawnNotice(RespawnSpawnModeBox, RespawnSpawnSpeakBox, RespawnSpawnSoundBox,
            Models.RespawnNotice.DefaultSpawnPhrase);

    /// <summary>Previews with the selected mob's name in {mob} (or a stand-in).</summary>
    private void TestRespawnNotice(System.Windows.Controls.ComboBox mode,
        System.Windows.Controls.TextBox speak, System.Windows.Controls.ComboBox sound,
        Func<string, string> defaultPhrase)
    {
        if (_alerts.Muted) { Status("Unmute to preview."); return; }
        string mob = SelectedRespawn?.Name is { Length: > 0 } n ? n : "Princess Cherista";
        if (mode.SelectedValue as string != "sound")
        {
            var p = Models.RespawnNotice.Payload(true, "speak", speak.Text.Trim(), "", mob, defaultPhrase);
            _alerts.Fire(p?.Speak, null);
        }
        else if (sound.SelectedItem is SoundPreset p && p.Path.Length > 0)
        {
            _alerts.Fire(null, p.Path);
        }
        else
        {
            Status("Pick a sound first.");
        }
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

    // ---- the window's two faces ----------------------------------------------
    // Owner ask: Triggers should feel like its own window, not a Settings
    // page. One window class keeps the (deeply intertwined) save machinery,
    // but the ⚡ bolt opens it as TRIGGERS (triggers + loadouts only) and the
    // cog's Settings… opens it as SETTINGS (panels + app only).

    private string _mode = "settings";

    private static readonly string[] TriggerFace = { "TRIGGERS", "Triggers", "Loadouts" };

    public void SetMode(string mode)
    {
        _mode = mode;
        bool triggers = mode == "triggers";
        foreach (var item in NavList.Items.OfType<System.Windows.Controls.ListBoxItem>())
        {
            bool trigItem = TriggerFace.Contains(item.Content as string);
            item.Visibility = trigItem == triggers ? Visibility.Visible : Visibility.Collapsed;
        }
        // A selection hidden by the face switch falls to the face's first page.
        if (NavList.SelectedItem is System.Windows.Controls.ListBoxItem sel
            && sel.Visibility != Visibility.Visible)
            SelectPage(triggers ? "Triggers" : "Bars & matrices");
        UpdateCharInfo();
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
            ["Spawn timer"] = RespawnsPage,
            ["DPS + Skills, Procs"] = MeterPage,
            ["Scrolling combat text"] = SctPage,
            ["Flash alerts"] = FlashPage,
            ["Death recap"] = DeathPage,
            ["Condition badges"] = ConditionsPage,
            ["Sky droppers"] = SkyHelperPage,
            ["General"] = GeneralPage,
            ["Sounds & voices"] = SoundsPage,
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
    /// type — a repop trigger has no category, color, or rebuff reminder —
    /// and hide the manual-only tooling (Show in, Type, cooldown reducer)
    /// for library triggers, which arrive with all of that predefined.
    /// </summary>
    private void ApplyPanelVisibility()
    {
        if (AlertsGroup is null) return; // still initializing

        string p = PanelCombo.SelectedValue as string ?? Panels.Bars;
        bool bars = p == Panels.Bars;
        bool matrix = p is Panels.SelfBuffs or Panels.TargetDebuffs;
        bool timer = p == Panels.TimerAuto;
        bool flash = p == Panels.Flash;
        bool manual = Selected?.IsLibrary != true;

        static Visibility V(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        ShowInGroup.Visibility = V(manual);
        CategoryGroup.Visibility = V(bars); // nested in ShowInGroup → manual && bars
        DurationGroup.Visibility = V(bars || matrix || timer);
        EndGroup.Visibility = V(bars || matrix);
        BarsChecksGroup.Visibility = V(bars);
        ReducerGroup.Visibility = V(bars && manual);
        AlertsGroup.Visibility = V(bars || matrix);
        // Flash text is a flash trigger's main field; elsewhere it's a manual
        // power tool (library adds never come with one).
        FlashGroup.Visibility = V(flash || manual);

        DurationLabel.Text = timer ? "Respawn time" : "Duration";
        // Auto-learn is a spell-duration concept — respawn timers don't learn.
        DurationAutoCheck.Visibility = V(bars || matrix);
        DurationAutoHint.Visibility = V(bars || matrix);
        PermanentCheck.Visibility = V(bars || matrix);

        // Cast-anchoring is likewise a spell concept: repop death lines and
        // flash patterns fire on any match.
        AnchorGroup.Visibility = V(bars || matrix);
        UpdateAnchorUx();

        // Reordering only matters for matrix triggers (cells lay out in list
        // order); bars always sort themselves by time left.
        MoveUpBtn.Visibility = V(matrix);
        MoveDownBtn.Visibility = V(matrix);
        UpdateDurationUx();
        StartLabel.Text = timer ? "Death line (regex — starts the repop timer)"
            : flash ? "Pattern (regex — fires the flash)"
            : matrix ? "Start pattern (regex — turns the cell green)"
            : "Start pattern (regex — starts the bar)";
        FlashLabel.Text = flash ? "Flash text (empty = the trigger's name)"
            : "Flash this text on screen when the start pattern matches (optional)";
        PanelHint.Text = timer
            ? "When the pattern matches (e.g. a named mob death), the circular repop watch starts with this respawn time. Also listed in the watch's ☰ preset menu."
            : matrix ? "A red/green cell: green with seconds left while active, red when missing."
            : flash ? "Big screen-center text in the trigger's colour that fades out."
            : "A depleting countdown bar in the bars panel, grouped by category.";
    }

    // ---- settings tab -------------------------------------------------------

    private void LoadSettingsFields()
    {
        ConditionsVisibleCheck.IsChecked = _config.Overlay.ConditionsVisible;
        SkyHelperVisibleCheck.IsChecked = _config.Overlay.SkyHelperVisible;
        SkyHelperCompletedCheck.IsChecked = _config.Overlay.SkyHelperShowCompleted;
        _soundUxLoading = true;
        InterruptOnCheck.IsChecked = _config.Overlay.InterruptNoticeEnabled;
        InterruptModeBox.SelectedValue = _config.Overlay.InterruptNoticeMode;
        // The default phrase shows IN the box — an empty field reads as broken.
        InterruptSpeakBox.Text = string.IsNullOrWhiteSpace(_config.Overlay.InterruptNoticeSpeak)
            ? "Interrupted!" : _config.Overlay.InterruptNoticeSpeak;
        SyncSoundCombo(InterruptSoundBox, _config.Overlay.InterruptNoticeSound);
        ResistOnCheck.IsChecked = _config.Overlay.ResistNoticeEnabled;
        ResistModeBox.SelectedValue = _config.Overlay.ResistNoticeMode;
        ResistSpeakBox.Text = string.IsNullOrWhiteSpace(_config.Overlay.ResistNoticeSpeak)
            ? "Resisted!" : _config.Overlay.ResistNoticeSpeak;
        SyncSoundCombo(ResistSoundBox, _config.Overlay.ResistNoticeSound);
        _soundUxLoading = false;
        UpdateMomentNoticeUx();

        // Sounds & voices: the one speaking voice for every spoken alert.
        VoiceBox.ItemsSource = new[] { "(system default)" }
            .Concat(_alerts.InstalledVoices()).ToList();
        VoiceBox.SelectedItem = string.IsNullOrWhiteSpace(_config.Overlay.VoiceName)
            ? "(system default)"
            : ((List<string>)VoiceBox.ItemsSource).FirstOrDefault(v =>
                  v.Equals(_config.Overlay.VoiceName, StringComparison.OrdinalIgnoreCase))
              ?? "(system default)";
        VoiceRateBox.SelectedValue = _config.Overlay.VoiceRate.ToString(CultureInfo.InvariantCulture);
        if (VoiceRateBox.SelectedValue is null) VoiceRateBox.SelectedValue = "0";

        // The global spawn-timer notices — phrase boxes show their {mob}
        // templates instead of standing empty.
        RespawnWarnOnCheck.IsChecked = _config.Overlay.RespawnWarnEnabled;
        RespawnWarnSecondsBox.Text = _config.Overlay.RespawnWarnSeconds.ToString(CultureInfo.InvariantCulture);
        RespawnWarnModeBox.SelectedValue = _config.Overlay.RespawnWarnMode;
        RespawnWarnSpeakBox.Text = string.IsNullOrWhiteSpace(_config.Overlay.RespawnWarnPhrase)
            ? "{mob} spawning soon" : _config.Overlay.RespawnWarnPhrase;
        SyncSoundCombo(RespawnWarnSoundBox, _config.Overlay.RespawnWarnSound);
        RespawnSpawnOnCheck.IsChecked = _config.Overlay.RespawnSpawnEnabled;
        RespawnSpawnModeBox.SelectedValue = _config.Overlay.RespawnSpawnMode;
        RespawnSpawnSpeakBox.Text = string.IsNullOrWhiteSpace(_config.Overlay.RespawnSpawnPhrase)
            ? "{mob} respawn" : _config.Overlay.RespawnSpawnPhrase;
        SyncSoundCombo(RespawnSpawnSoundBox, _config.Overlay.RespawnSpawnSound);
        UpdateRespawnNoticeUx();

        LogDirBox.Text = _config.Log.Directory;
        FilePatternBox.Text = _config.Log.FilePattern;
        // The live "Following eqlog_…" line moved here from the toolbar —
        // it's diagnostics, not play-time information.
        FollowingText.Text = _logStatus?.Invoke() ?? "";

        WidthBox.Text = _config.Overlay.Width.ToString(CultureInfo.InvariantCulture);
        BarHeightBox.Text = _config.Overlay.BarHeight.ToString(CultureInfo.InvariantCulture);
        FontSizeBox.Text = _config.Overlay.FontSize.ToString(CultureInfo.InvariantCulture);
        WarnBox.Text = _config.Overlay.WarnSeconds.ToString(CultureInfo.InvariantCulture);
        RemindBox.Text = _config.Overlay.RemindIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        OpacityBox.Text = _config.Overlay.Opacity.ToString(CultureInfo.InvariantCulture);
        MatrixColumnsBox.Text = _config.Overlay.MatrixColumns.ToString(CultureInfo.InvariantCulture);
        ShowHeadersCheck.IsChecked = _config.Overlay.ShowCategoryHeaders;
        StartLockedCheck.IsChecked = _config.Overlay.StartLocked;
        CursorRingCheck.IsChecked = _config.Overlay.CursorRingVisible;
        CompanionsBox.Text = _config.Overlay.CompanionNames;
        DeathRecapCheck.IsChecked = _config.Overlay.DeathRecapAuto;
        StartWithWindowsCheck.IsChecked = IsAutoStartEnabled();
        TimerVisibleCheck.IsChecked = _config.Overlay.TimerVisible;
        MeterVisibleCheck.IsChecked = _config.Overlay.MeterVisible;
        SkillsVisibleCheck.IsChecked = _config.Overlay.SkillTrackerVisible;
        ProcsVisibleCheck.IsChecked = _config.Overlay.ProcWatcherVisible;
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
        SctXpLifetimeBox.Text = _config.Overlay.SctXpLifetime.ToString(CultureInfo.InvariantCulture);

        EnemyDotsVisibleCheck.IsChecked = _config.Overlay.EnemyDotsVisible;
        EnemyDotsGroupBox.SelectedValue = _config.Overlay.EnemyDotsGroupByMob ? "mob" : "spell";
        EnemyDotsAnchorBox.SelectedValue = (_configService.LoadPlacement("enemyDots")?.Anchor ?? Anchor.TopLeft).ToString();
        RemindersAnchorBox.SelectedValue = (_configService.LoadPlacement("reminders")?.Anchor ?? Anchor.TopLeft).ToString();
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

    // ---- interrupt / resist notice UX ---------------------------------------

    private void MomentNotice_Changed(object sender, RoutedEventArgs e) => UpdateMomentNoticeUx();

    /// <summary>Rows follow their checkbox; the mode picks which input shows
    /// (sound preset OR phrase — one channel, never both).</summary>
    private void UpdateMomentNoticeUx()
    {
        if (InterruptRow is null || ResistRow is null) return;
        InterruptRow.IsEnabled = InterruptOnCheck.IsChecked == true;
        ResistRow.IsEnabled = ResistOnCheck.IsChecked == true;
        bool iSound = InterruptModeBox.SelectedValue as string != "speak";
        InterruptSoundBox.Visibility = iSound ? Visibility.Visible : Visibility.Collapsed;
        InterruptSpeakBox.Visibility = iSound ? Visibility.Collapsed : Visibility.Visible;
        bool rSound = ResistModeBox.SelectedValue as string != "speak";
        ResistSoundBox.Visibility = rSound ? Visibility.Visible : Visibility.Collapsed;
        ResistSpeakBox.Visibility = rSound ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InterruptTest_Click(object sender, RoutedEventArgs e) =>
        TestMomentNotice(InterruptModeBox, InterruptSpeakBox, InterruptSoundBox, "Interrupted!");

    private void ResistTest_Click(object sender, RoutedEventArgs e) =>
        TestMomentNotice(ResistModeBox, ResistSpeakBox, ResistSoundBox, "Resisted!");

    private void TestMomentNotice(System.Windows.Controls.ComboBox mode,
        System.Windows.Controls.TextBox speak, System.Windows.Controls.ComboBox sound, string def)
    {
        if (_alerts.Muted) { Status("Unmute to preview."); return; }
        if (mode.SelectedValue as string == "speak")
            _alerts.Fire(string.IsNullOrWhiteSpace(speak.Text) ? def : speak.Text.Trim(), null);
        else if (sound.SelectedItem is SoundPreset p && p.Path.Length > 0)
            _alerts.Fire(null, p.Path);
        else
            Status("Pick a sound first.");
    }

    // ---- sounds & voices ------------------------------------------------------

    /// <summary>Applies the pick right away and says a line in it — hearing
    /// the actual voice beats reading its name. Save persists the choice.</summary>
    private void VoiceTest_Click(object sender, RoutedEventArgs e)
    {
        if (_alerts.Muted) { Status("Unmute to preview."); return; }
        string name = VoiceBox.SelectedItem as string is "(system default)" or null
            ? "" : (string)VoiceBox.SelectedItem;
        int rate = int.TryParse(VoiceRateBox.SelectedValue as string, out int r) ? r : 0;
        _alerts.ApplyVoice(name, rate);
        _alerts.Speak("Kurven the Cruel respawn");
    }

    /// <summary>One-click natural-voice setup: fetch the adapter's LATEST
    /// official release (we bundle nothing — no stale copy, no COM DLLs in
    /// our download), unpack it into a PERMANENT home (its DLLs must stay
    /// where registered — the project's own rule, which also rules out temp)
    /// and open its installer. The admin prompt and the Install click stay
    /// with the user — a system-wide change should never be silent.</summary>
    private async void VoiceAdapterSetup_Click(object sender, RoutedEventArgs e)
    {
        var btn = (System.Windows.Controls.Button)sender;
        btn.IsEnabled = false;
        try
        {
            VoiceAdapterStatus.Text = "Fetching the latest release…";
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EQL-Assistant");
            string json = await http.GetStringAsync(
                "https://api.github.com/repos/gexgd0419/NaturalVoiceSAPIAdapter/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "latest";
            string wanted = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x86_x64";
            string? url = null, assetName = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string n = a.GetProperty("name").GetString() ?? "";
                if (n.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                    && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    assetName = n;
                    break;
                }
            }
            if (url is null || assetName is null)
            {
                VoiceAdapterStatus.Text = "No matching download in the latest release — use the project page instead.";
                return;
            }

            string home = System.IO.Path.Combine(_configService.ConfigDirectory, "voice-adapter", tag);
            VoiceAdapterStatus.Text = $"Downloading {assetName}…";
            byte[] zip = await http.GetByteArrayAsync(url);
            System.IO.Directory.CreateDirectory(home);
            string zipPath = System.IO.Path.Combine(home, assetName);
            await System.IO.File.WriteAllBytesAsync(zipPath, zip);
            VoiceAdapterStatus.Text = "Unpacking…";
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, home, overwriteFiles: true);
            try { System.IO.File.Delete(zipPath); } catch { /* tidy-up only */ }

            string? installer = System.IO.Directory
                .GetFiles(home, "Installer.exe", System.IO.SearchOption.AllDirectories)
                .FirstOrDefault();
            if (installer is null)
            {
                VoiceAdapterStatus.Text = "Installer.exe not found in the download — use the project page instead.";
                return;
            }
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = true });
            VoiceAdapterStatus.Text = $"Installer opened ({tag}). Click Install there — Windows asks for admin — "
                + "then restart EQL Assistant and the natural voices appear in the picker above. "
                + "The files live in the app's config folder; leave them in place.";
            Log.Info($"Voice adapter {tag} downloaded to '{home}', installer launched.");
        }
        catch (Exception ex)
        {
            VoiceAdapterStatus.Text = "Setup failed: " + ex.Message + " — the project page button still works.";
            Log.Error("Voice adapter setup failed", ex);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void VoiceAdapter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/gexgd0419/NaturalVoiceSAPIAdapter") { UseShellExecute = true });
        }
        catch { /* no browser is not our problem to solve */ }
    }

    private void BrowseLogDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your log folder" };
        if (!string.IsNullOrWhiteSpace(LogDirBox.Text)) dlg.InitialDirectory = LogDirBox.Text;
        if (dlg.ShowDialog(this) == true) LogDirBox.Text = dlg.FolderName;
    }

    // ---- save ---------------------------------------------------------------

    /// <summary>Every loadout's triggers as definitions; with an error sink,
    /// each pattern is also compile-checked (the save-time validation).</summary>
    private List<Loadout> BuildLoadouts(List<string>? errors)
    {
        var loadouts = new List<Loadout>();
        foreach (var name in _order)
        {
            var defs = new List<TriggerDefinition>();
            foreach (var vm in _byName[name])
            {
                var d = vm.ToDefinition();
                if (errors is not null)
                {
                    try { ConfigService.CompileOne(d); }
                    catch (ArgumentException ex) { errors.Add($"  • [{name}] {d.Name}: {ex.Message}"); }
                }
                defs.Add(d);
            }
            loadouts.Add(new Loadout { Name = name, Triggers = defs });
        }
        return loadouts;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Validate every trigger in every loadout.
        var errors = new List<string>();
        var loadouts = BuildLoadouts(errors);

        if (errors.Count > 0)
        {
            MessageBox.Show("Fix these invalid regex patterns before saving:\n\n" +
                string.Join("\n", errors), "Invalid pattern",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cfg = BuildConfigFromUi();

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
        ApplyAnchor("enemyDots", EnemyDotsAnchorBox);
        ApplyAnchor("reminders", RemindersAnchorBox);
        ApplyAnchor("selfMatrix", SelfAnchorBox);
        ApplyAnchor("targetDebuffs", TargetAnchorBox);
        ApplyAnchor("timer", TimerAnchorBox);
        ApplyAnchor("meter", MeterAnchorBox);
        ApplyAnchor("flash", FlashAnchorBox);

        _config = cfg;
        _onApplied(_currentName);
        _cleanFingerprint = Fingerprint(); // this IS the saved state now
        UpdateDirtyCta(clean: true);
        Status($"Saved {loadouts.Count} loadout(s). Active: {_currentName}.");
    }

    /// <summary>The AppConfig exactly as Save would write it, read from the UI
    /// (shared by the save and the dirty-check fingerprint).</summary>
    private AppConfig BuildConfigFromUi()
    {
        return new AppConfig
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
                CursorRingVisible = CursorRingCheck.IsChecked == true,
                CompanionNames = CompanionsBox.Text.Trim(),
                Muted = MuteCheck.IsChecked == true,
                VoiceName = VoiceBox.SelectedItem as string is "(system default)" or null
                    ? "" : (string)VoiceBox.SelectedItem,
                VoiceRate = int.TryParse(VoiceRateBox.SelectedValue as string, out int vr) ? vr : 0,
                DeathRecapAuto = DeathRecapCheck.IsChecked == true,
                ToolbarVisible = _config.Overlay.ToolbarVisible, // tray-toggled — carried through
                BarsVisible = _config.Overlay.BarsVisible,       // tray-toggled — carried through
                SelfMatrixVisible = _config.Overlay.SelfMatrixVisible,     // tray-toggled
                TargetMatrixVisible = _config.Overlay.TargetMatrixVisible, // tray-toggled
                RemindersVisible = _config.Overlay.RemindersVisible,       // tray-toggled
                EnemyDotsVisible = EnemyDotsVisibleCheck.IsChecked == true,
                EnemyDotsGroupByMob = EnemyDotsGroupBox.SelectedValue as string != "spell",
                ConditionsVisible = ConditionsVisibleCheck.IsChecked == true,
                SkyHelperVisible = SkyHelperVisibleCheck.IsChecked == true,
                SkyHelperShowCompleted = SkyHelperCompletedCheck.IsChecked == true,
                InterruptNoticeEnabled = InterruptOnCheck.IsChecked == true,
                InterruptNoticeMode = InterruptModeBox.SelectedValue as string ?? "sound",
                InterruptNoticeSpeak = InterruptSpeakBox.Text.Trim(),
                InterruptNoticeSound = (InterruptSoundBox.SelectedItem as SoundPreset)?.Path
                    ?? _config.Overlay.InterruptNoticeSound,
                ResistNoticeEnabled = ResistOnCheck.IsChecked == true,
                ResistNoticeMode = ResistModeBox.SelectedValue as string ?? "sound",
                ResistNoticeSpeak = ResistSpeakBox.Text.Trim(),
                ResistNoticeSound = (ResistSoundBox.SelectedItem as SoundPreset)?.Path
                    ?? _config.Overlay.ResistNoticeSound,
                RespawnWarnEnabled = RespawnWarnOnCheck.IsChecked == true,
                RespawnWarnSeconds = double.TryParse(RespawnWarnSecondsBox.Text,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double rws) && rws > 0 ? rws : 15,
                RespawnWarnMode = RespawnWarnModeBox.SelectedValue as string ?? "speak",
                RespawnWarnPhrase = RespawnWarnSpeakBox.Text.Trim(),
                RespawnWarnSound = (RespawnWarnSoundBox.SelectedItem as SoundPreset)?.Path
                    ?? _config.Overlay.RespawnWarnSound,
                RespawnSpawnEnabled = RespawnSpawnOnCheck.IsChecked == true,
                RespawnSpawnMode = RespawnSpawnModeBox.SelectedValue as string ?? "speak",
                RespawnSpawnPhrase = RespawnSpawnSpeakBox.Text.Trim(),
                RespawnSpawnSound = (RespawnSpawnSoundBox.SelectedItem as SoundPreset)?.Path
                    ?? _config.Overlay.RespawnSpawnSound,
                MeterSoloMode = _config.Overlay.MeterSoloMode,           // meter-toggled
                SessionStatsVisible = _config.Overlay.SessionStatsVisible,       // tray-toggled
                SessionStatsSlice = _config.Overlay.SessionStatsSlice,           // panel-toggled
                SessionStatsExactTier = _config.Overlay.SessionStatsExactTier,   // panel-toggled
                SessionStatsActiveBasis = _config.Overlay.SessionStatsActiveBasis, // panel-toggled
                TimerSeconds = _config.Overlay.TimerSeconds, // not edited here — carried through
                TimerVisible = TimerVisibleCheck.IsChecked == true,
                MeterVisible = MeterVisibleCheck.IsChecked == true,
                SkillTrackerVisible = SkillsVisibleCheck.IsChecked == true,
                ProcWatcherVisible = ProcsVisibleCheck.IsChecked == true,
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
                SctXpLifetime = Math.Clamp(ParseOr(SctXpLifetimeBox.Text, _config.Overlay.SctXpLifetime), 1, 15),
            },
        };
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
}
