using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;
using EQLOverlay.ViewModels;
using EQLOverlay.Views;

namespace EQLOverlay;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly AlertService _alerts = new();
    private readonly LogBus _logBus = new();
    private AppConfig _config = new();
    private TriggerEngine _engine = null!;
    private OverlayViewModel _vm = null!;
    private LogWatcher? _watcher;
    private TriggerManagerWindow? _manager;
    private MatrixWindow? _selfMatrix;
    private MatrixWindow? _targetMatrix;
    private TimerWindow? _timer;
    private FlashWindow? _flash;
    private MeterWindow? _meter;
    private EnemyDotsWindow? _enemyDotsWin;
    private ConditionsWindow? _conditionsWin;
    private SkyHelperWindow? _skyHelperWin;
    private SkyHelper _skyHelper = null!;
    private SessionStatsWindow? _sessionWin;
    private RemindersWindow? _remindersWin;
    private ConditionWatcher _conditions = null!;
    private readonly SessionStats _session = new();
    private readonly CombatParser _combat = new();
    private RaidKills _raids = null!;
    private LootTracker _loot = null!;
    private SkyQuests _skyQuests = null!;
    private SpellLibrary _spellLib = null!;
    private SpellDurations _durations = null!;
    private RaidKillsWindow? _raidsWindow;
    private readonly Dictionary<CombatParser.SctKind, SctLaneWindow> _sctLanes = new();
    private PanelPlacement? _mainPlacement;
    private bool _hidden;
    private bool _timerHidden;
    private bool _meterHidden;
    private bool _skillsHidden;
    private bool _flashHidden;
    private bool _sctHidden;
    private bool _suppressSct;   // true while replaying old lines (catch-up)
    private System.Windows.Forms.NotifyIcon? _tray;

    /// <summary>Set by self-tests so they never persist window position/lock.</summary>
    internal bool SuppressStatePersistence;

    private nint _hwnd;

    // Hotkey ids.
    private const int HK_LOCK    = 1;
    private const int HK_TEST    = 4;
    private const int HK_HIDE    = 5;
    private const int HK_MUTE    = 7;
    private const int HK_QUIT    = 9;
    private const int HK_REPOP   = 10;
    private const int HK_METER   = 11;
    private const int HK_SCT     = 12;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _config = _configService.LoadSettings();
            _configService.EnsureDefaultLoadout();
            _configService.MigrateRespawnsFromLoadouts();
            LoadActiveLoadoutInto(_config);
            Log.Info($"Config loaded. loadout='{_config.ActiveLoadout}', triggers={_config.Triggers.Count}, " +
                     $"logDir='{_config.Log.Directory}', startLocked={_config.Overlay.StartLocked}");
            Log.Info($"Config dir: {_configService.ConfigDirectory}");
        }
        catch (Exception ex)
        {
            Log.Error("Config load failed", ex);
            MessageBox.Show(ex.Message, "EQL Assistant — config problem",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _config = new AppConfig(); // run empty rather than crash
        }

        _alerts.Muted = _config.Overlay.Muted;
        _raids = new RaidKills(_configService);
        _loot = new LootTracker(_configService);
        _loot.Added += e => _raids.AttributeLoot(e);   // pin drops to raid kills
        _raids.BackfillLoot(_loot.Entries);            // one-time: history -> past kills
        _skyQuests = new SkyQuests(_configService, _loot);
        _skyHelper = new SkyHelper(_skyQuests)
        {
            // Lazy: a late /who updates the filter with no re-wiring.
            ClassesProvider = () => (_session?.WhoClasses ?? "")
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        _skyQuests.QuestCompleted += q =>
        {
            if (!_suppressSct) // replay/reparse re-completions shouldn't flash-spam
                OnFlashRequested($"Sky quest complete — {q.Reward}!", "#FFD54F");
        };
        _spellLib = new SpellLibrary(_configService);
        Log.Info($"Spell library: {_spellLib.Spells.Count} spells, {_spellLib.SeenCount} seen.");
        int retyped = _spellLib.HealLibraryTriggers(_config.Triggers); // pre-2.9 lib types heal
        if (retyped > 0) Log.Info($"Retyped {retyped} library trigger(s) (HoTs/DoTs split).");
        _durations = new SpellDurations(_configService, _spellLib);
        _conditions = new ConditionWatcher(_spellLib);
        WireRespawnLearner();
        // Enemy-DoT countdowns: learned first, library figure as the fallback.
        _combat.DotDurationLookup = spell =>
            spell.StartsWith("Demo ", StringComparison.Ordinal) ? 30 // Ctrl+Alt+T bars
            : _durations.LearnedMaxSeconds(spell)
              ?? (_spellLib.FindByBaseName(spell)?.DurationSec is > 0 and var libSec ? libSec : null);
        // A known beneficial spell's own heal/damage component is a buff at
        // work (Quick Buff bursts land them with no cast line) — never a proc.
        _combat.BeneficialLookup = name => _spellLib.FindByBaseName(name)?.Bucket == "Buff";
        // Non-damaging debuffs (slow/malo/cripple) tint orange in the panel.
        _combat.DebuffLookup = name =>
            name.StartsWith("Demo ", StringComparison.Ordinal) ? name.Contains("Slow")
            : _spellLib.FindByBaseName(name) is { } sp && SpellLibrary.TriggerCategory(sp) == "Debuffs";
        _combat.OtherLandingLookup = _spellLib.OtherLanding;
        _timerHidden = !_config.Overlay.TimerVisible;
        _meterHidden = !_config.Overlay.MeterVisible;
        _skillsHidden = !_config.Overlay.SkillTrackerVisible;
        _flashHidden = !_config.Overlay.FlashVisible;
        _sctHidden = !_config.Overlay.SctVisible;
        _toolbarHidden = !_config.Overlay.ToolbarVisible;
        _barsHidden = !_config.Overlay.BarsVisible;
        ApplySelfName();
        _combat.PetName = _config.Overlay.PetName;
        // The pet names itself on any /pet order — remember it so the meter's
        // pet lanes work without ever typing the name into the Manager.
        _combat.PetDetected += pet => Dispatcher.BeginInvoke(() =>
        {
            _config.Overlay.PetName = pet;
            _configService.SaveSettings(_config);
            _vm.Flash($"Pet detected: {pet}");
            Log.Info($"Pet auto-detected: {pet}");
        });
        _combat.SctEvent += OnSctEvent;
        _combat.PlayerDied += OnPlayerDied;
        _combat.FightArchived += OnFightArchived;
        _engine = new TriggerEngine(_config, _alerts);
        _engine.LearnedDuration = name => _durations.LearnedMaxSeconds(name);
        _engine.TimerRequested += OnTimerRequested;
        _engine.FlashRequested += OnFlashRequested;
        _engine.BarReduced += OnBarReduced;
        _vm = new OverlayViewModel(_engine, _config) { LoadoutName = _config.ActiveLoadout };
        DataContext = _vm;

        SizerHost.Width = _config.Overlay.Width;
        _mainPlacement = new PanelPlacement(this, _configService, "main", Anchor.TopLeft, 60, 140);
        _mainPlacement.Attach();

        // Start locked (game-ready) if configured — overrides the remembered state.
        if (_config.Overlay.StartLocked)
        {
            _vm.Locked = true;
            _config.Overlay.Locked = true;
        }

        ApplyOpacity();
        ApplyLockVisual();
        ApplyClickThrough(); // safe now that _vm exists (also called in OnSourceInitialized)
        SetupTrayIcon();
        StartWatcher();
        RebuildMatrixWindows();
        RebuildTimerWindow();
        RebuildMeterWindow();
        RebuildFlashWindow();
        RebuildSctLanes();
        BuildToolbarWindow();
        UpdateBarsVisibility(); // bars may start hidden (Panels toggle persisted)

        // Crash-tolerant last-seen marker: persisted every minute while lines flow.
        var lastSeenTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        lastSeenTick.Tick += (_, _) => SaveLastSeen();
        lastSeenTick.Start();

        // Startup flow: update check FIRST — catching up a log for an app we're
        // about to replace would be wasted work. Then catch-up ALWAYS runs:
        // log data is the app's foundation, every consumer dedupes, and alerts
        // stay quiet for old lines, so there is no reason to ask.
        var startupFlow = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        startupFlow.Tick += async (_, _) =>
        {
            startupFlow.Stop();
            await CheckForUpdates(manual: false);
            if (_updateHandoff) return;

            // Give the watcher a moment to resolve which log file to follow.
            var once = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            once.Tick += (_, _) =>
            {
                once.Stop();
                CatchUpToday();
            };
            once.Start();
        };
        startupFlow.Start();
    }

    // ---- self-update ----------------------------------------------------------

    private bool _updateBusy;
    private bool _updateHandoff; // true once we've handed off to the new exe

    /// <summary>Answer where the question was asked: a Windows notification from
    /// the tray icon (the overlay flash is invisible from the tray corner).</summary>
    private void TrayNotify(string text)
    {
        if (_tray is null) { _vm.Flash(text); return; }
        _tray.BalloonTipTitle = "EQL Assistant";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(4000);
    }

    /// <summary>Check GitHub for a newer release; offer to download + restart.
    /// Manual checks (tray) also report "up to date" / failures.</summary>
    private async Task CheckForUpdates(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            ReleaseInfo? rel;
            try
            {
                rel = await UpdateService.CheckLatestAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Update check failed: " + ex.Message);
                if (manual) TrayNotify("Update check failed — GitHub not reachable?");
                return;
            }
            if (rel is null)
            {
                if (manual) TrayNotify("Update check: no downloadable release found.");
                return;
            }
            if (!UpdateService.IsNewer(rel.Version))
            {
                Log.Info($"Update check: up to date (latest is {rel.Tag}).");
                if (manual) TrayNotify($"You're up to date (v{UpdateService.CurrentVersion.ToString(3)} is the latest).");
                return;
            }

            Log.Info($"Update available: {rel.Tag} ({rel.AssetName}, {rel.AssetSize / (1024 * 1024)} MB).");
            bool yes = ConfirmDialog.Show(null, "Update available",
                $"EQL Assistant {rel.Tag} is available — you have v{UpdateService.CurrentVersion.ToString(3)}.\n\n" +
                "Update now? The new version is downloaded, the app restarts itself, " +
                "and all your settings and history are untouched.",
                yesText: "Update now", noText: "Later");
            if (yes) await RunUpdateAsync(rel);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    /// <summary>Download the release and hand off to the temp exe (see UpdateService).</summary>
    private async Task RunUpdateAsync(ReleaseInfo rel)
    {
        var progress = new Views.UpdateProgressDialog(rel.Tag);
        progress.Show();
        string tempExe;
        try
        {
            tempExe = await UpdateService.DownloadAsync(rel,
                new Progress<double>(progress.SetProgress), progress.Cancellation);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Update cancelled by user.");
            return;
        }
        catch (Exception ex)
        {
            progress.Close();
            Log.Error("Update download failed", ex);
            _vm.Flash("Update download failed — see the log file.");
            return;
        }

        progress.SetStatus("Restarting…");
        string target = Environment.ProcessPath!;
        Log.Info($"Update: handing off to '{tempExe}' -> '{target}'.");
        _updateHandoff = true;
        UpdateService.LaunchFinisher(tempExe, target);
        Close(); // normal quit path: saves state, disposes tray, releases the exe
    }

    // ---- last-seen marker (how far the app has parsed) ------------------------

    private DateTime _lastLineSeen;

    private void NoteLineSeen(DateTime t)
    {
        if (t > _lastLineSeen) _lastLineSeen = t;
    }

    private void SaveLastSeen()
    {
        string? path = _watcher?.CurrentPath;
        if (_lastLineSeen != default && path is not null)
            _configService.SaveLastSeen(Path.GetFileName(path), _lastLineSeen);
    }

    /// <summary>
    /// Replay today's lines from the followed log into the combat history,
    /// raid kills and seen spells — for when the app was started late.
    /// Triggers/alerts and combat text are NOT fired for old lines.
    /// </summary>
    private void CatchUpToday()
    {
        string? path = _watcher?.CurrentPath;
        if (path is null || !File.Exists(path))
        {
            _vm.Flash("Catch-up: no log file is being followed yet.");
            return;
        }

        int fightsBefore = _combat.History.Count;
        int lines = 0;
        _suppressSct = true;
        // Session stats rebuild EXACTLY: the file read includes every line the
        // live feed already processed, so reset-and-refeed never double-counts.
        _session.Reset();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var today = DateTime.Today;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!TryParseLineTime(line, out var t) || t.Date != today) continue;
                _combat.Replay(line);
                _raids.ProcessLine(line, t);
                _loot.ProcessLine(line); // uses the line's own timestamp; exact dedupe
                _skyQuests.ProcessLine(line);
                _spellLib.MarkSeenFromLine(line);
                _durations.ProcessLine(line);
                _session.ProcessLine(line);
                NoteLineSeen(t);
                lines++;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Catch-up failed: " + ex.Message);
            _vm.Flash("Catch-up failed — see the log file.");
            return;
        }
        finally
        {
            _suppressSct = false;
        }

        _spellLib.SaveSeenIfDirty();
        SaveLastSeen();
        int fights = _combat.History.Count - fightsBefore + (_combat.InCombat ? 1 : 0);
        _vm.Flash($"Caught up: {lines:N0} lines, {fights} fight(s) from today's log.");
        Log.Info($"Catch-up: {lines} lines replayed from {Path.GetFileName(path)}, {fights} fights.");
    }

    /// <summary>
    /// Full-history reparse (Manager → General): the WHOLE log through the
    /// retroactive services — loot, raid kills (+drop attribution via
    /// loot.Added), Sky quests, seen spells, duration learning. NOT the combat
    /// parser: ancient fights would pollute the session's fight history. Every
    /// consumer dedupes (loot exact, kills 10-min window, duration samples by
    /// timestamp), so running this twice never double-counts.
    /// </summary>
    private string ReparseFullLog()
    {
        string? path = _watcher?.CurrentPath;
        if (path is null || !File.Exists(path))
            return "Reparse: no log file is being followed yet.";
        return ReparseFile(path);
    }

    /// <summary>Reparse ANY log file (e.g. one carried over from another PC) —
    /// same retroactive pipeline, same dedupes, so histories merge safely.</summary>
    private string ReparseFile(string path)
    {
        int lootBefore = _loot.Entries.Count;
        int killsBefore = 0, durBefore = 0;
        Action<string, DateTime> onKill = (_, _) => killsBefore++;      // counts NEW kills
        Action<string, double, int> onSample = (_, _, _) => durBefore++; // counts NEW samples
        _raids.KillRecorded += onKill;
        _durations.SampleLearned += onSample;

        int lines = 0;
        _suppressSct = true;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!TryParseLineTime(line, out var t)) continue;
                _raids.ProcessLine(line, t);
                _loot.ProcessLine(line);
                _skyQuests.ProcessLine(line);
                _spellLib.MarkSeenFromLine(line);
                _durations.ProcessLine(line);
                lines++;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Full reparse failed: " + ex.Message);
            return "Reparse failed — see the log file.";
        }
        finally
        {
            _suppressSct = false;
            _raids.KillRecorded -= onKill;
            _durations.SampleLearned -= onSample;
        }

        _spellLib.SaveSeenIfDirty();
        int lootNew = Math.Max(0, _loot.Entries.Count - lootBefore);
        string summary = $"Reparsed {lines:N0} lines from {Path.GetFileName(path)}: " +
            $"{lootNew} new loot, {killsBefore} new raid kills, {durBefore} new duration samples.";
        Log.Info(summary);
        return summary;
    }

    /// <summary>Data page's "Merge in another log file…": store a timestamped
    /// COPY in the config folder FIRST — the original may be deleted or moved,
    /// and Reset &amp; rebuild replays the stored copies — then run the
    /// retroactive replay on the copy.</summary>
    private string MergeLogFile(string pickedPath)
    {
        string copy;
        try { copy = _configService.StoreMergedLogCopy(pickedPath); }
        catch (Exception ex)
        {
            Log.Warn("Merge copy failed: " + ex.Message);
            return "Couldn't copy the file into the config folder — nothing was merged.";
        }
        Log.Info($"Merged log stored as {Path.GetFileName(copy)}."); // shows under Additional log files
        return ReparseFile(copy);
    }

    /// <summary>Data page's "Reset & rebuild": wipe every log-DERIVED data file
    /// (loot, raid kills, Sky progress, seen spells, learned durations), then
    /// rebuild them all with a full reparse of the followed log PLUS every
    /// stored merge copy. Config, loadouts, respawns, raid targets, kept
    /// fights and window positions are untouched.</summary>
    private string ResetAndRebuild()
    {
        Log.Info("Data reset: wiping derived data files before full reparse.");
        _loot.ResetAll();
        _raids.ResetKills();
        _skyQuests.ResetProgress();
        _spellLib.ResetSeen();
        _durations.ResetAll();

        string result = "Data files reset. " + ReparseFullLog();
        var merged = _configService.ListMergedLogs();
        foreach (var f in merged) ReparseFile(f); // each logs its own summary
        if (merged.Count > 0)
            result += $" Also replayed {merged.Count} stored merged log file(s).";
        return result;
    }

    private static readonly string[] LineTimeFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    private static bool TryParseLineTime(string line, out DateTime time)
    {
        time = default;
        int close = line.IndexOf(']');
        if (!line.StartsWith('[') || close < 0) return false;
        string ts = System.Text.RegularExpressions.Regex.Replace(
            line[1..close].Trim(), @"\s+", " ");
        return DateTime.TryParseExact(ts, LineTimeFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out time);
    }

    // ---- scrolling combat text ------------------------------------------------

    // Per-lane colors: melee / spell / proc (heals use one green).
    private static readonly (CombatParser.SctKind Kind, string Key, string Title,
        Color Melee, Color Spell, Color Proc, double OffsetFromCenter)[] SctLaneDefs =
    {
        (CombatParser.SctKind.IncomingSelf,  "sctIncoming",  "Incoming",
            Color.FromRgb(0xFF, 0x8A, 0x80), Color.FromRgb(0xFF, 0xAB, 0x40), Color.FromRgb(0xCE, 0x93, 0xD8), -280),
        (CombatParser.SctKind.OutgoingSelf,  "sctOutgoing",  "Outgoing",
            Color.FromRgb(0xF5, 0xF5, 0xF5), Color.FromRgb(0xFF, 0xD5, 0x4F), Color.FromRgb(0xBA, 0x68, 0xC8),  110),
        (CombatParser.SctKind.HealOut,       "sctHeals",     "Your heals",
            Color.FromRgb(0x81, 0xC7, 0x84), Color.FromRgb(0x81, 0xC7, 0x84), Color.FromRgb(0x81, 0xC7, 0x84),  300),
        (CombatParser.SctKind.HealIn,        "sctHealsIn",   "Heals on you",
            Color.FromRgb(0xA5, 0xD6, 0xA7), Color.FromRgb(0xA5, 0xD6, 0xA7), Color.FromRgb(0xA5, 0xD6, 0xA7),  -85),
        (CombatParser.SctKind.IncomingPet,   "sctPetIn",     "Pet incoming",
            Color.FromRgb(0xF0, 0x62, 0x92), Color.FromRgb(0xF4, 0x8F, 0xB1), Color.FromRgb(0xCE, 0x93, 0xD8), -470),
        (CombatParser.SctKind.OutgoingPet,   "sctPetOut",    "Pet outgoing",
            Color.FromRgb(0xFF, 0xB7, 0x4D), Color.FromRgb(0xFF, 0xE0, 0x82), Color.FromRgb(0xB3, 0x9D, 0xDB),  490),
        // Progress floats: melee slot = xp/AA gold, spell slot = faction up,
        // proc slot = faction down.
        (CombatParser.SctKind.Progress,      "sctProgress",  "XP & faction",
            Color.FromRgb(0xFF, 0xD5, 0x4F), Color.FromRgb(0x4D, 0xB6, 0xAC), Color.FromRgb(0xE5, 0x73, 0x73), -660),
    };

    private bool SctLaneEnabled(CombatParser.SctKind kind) => kind switch
    {
        CombatParser.SctKind.IncomingSelf => _config.Overlay.SctIncoming,
        CombatParser.SctKind.OutgoingSelf => _config.Overlay.SctOutgoing,
        CombatParser.SctKind.HealOut => _config.Overlay.SctHeals,
        CombatParser.SctKind.HealIn => _config.Overlay.SctHealsIn,
        CombatParser.SctKind.IncomingPet => _config.Overlay.SctPetIncoming,
        CombatParser.SctKind.OutgoingPet => _config.Overlay.SctPetOutgoing,
        CombatParser.SctKind.Progress => _config.Overlay.SctProgress,
        _ => false,
    };

    private void RebuildSctLanes()
    {
        foreach (var lane in _sctLanes.Values) { try { lane.Close(); } catch { /* ignore */ } }
        _sctLanes.Clear();

        double centerX = SystemParameters.WorkArea.Width / 2;
        double topY = SystemParameters.WorkArea.Height * 0.32;
        var o = _config.Overlay;
        foreach (var def in SctLaneDefs)
        {
            if (!SctLaneEnabled(def.Kind)) continue;
            var lane = new SctLaneWindow(_configService, def.Key, def.Title,
                new SolidColorBrush(def.Melee), new SolidColorBrush(def.Spell), new SolidColorBrush(def.Proc),
                o.Opacity, o.SctFontSize, o.SctBigHit,
                o.SctLaneWidth, o.SctLaneHeight,
                centerX + def.OffsetFromCenter - o.SctLaneWidth / 2, topY,
                // xp/faction floats are rare and worth reading — drift up slowly
                // (how slowly is the player's call: Manager → Combat text).
                lifetimeSeconds: def.Kind == CombatParser.SctKind.Progress ? o.SctXpLifetime : 2.6);
            lane.Show();
            lane.SetLocked(_vm.Locked);
            _sctLanes[def.Kind] = lane;
        }
        UpdateSctVisibility();
    }

    /// <summary>A cooldown reducer fired — float the cut on the progress lane
    /// ("-60s Harm Touch") so the jump on the bar is impossible to miss.</summary>
    private void OnBarReduced(string triggerName, double seconds)
    {
        if (_hidden || _sctHidden) return;
        if (_sctLanes.TryGetValue(CombatParser.SctKind.Progress, out var lane))
            lane.Post(triggerName, -seconds,
                flavor: CombatParser.SctFlavor.Spell, text: $"-{seconds:0}s");
    }

    private void OnSctEvent(CombatParser.SctHit hit)
    {
        if (_hidden || _sctHidden || _suppressSct) return;
        if (_sctLanes.TryGetValue(hit.Kind, out var lane))
            lane.Post(hit.Ability, hit.Amount,
                plus: hit.Kind is CombatParser.SctKind.HealOut or CombatParser.SctKind.HealIn,
                flavor: hit.Flavor, crit: hit.Crit, text: hit.Text);
    }

    private void UpdateSctVisibility()
    {
        foreach (var lane in _sctLanes.Values)
            lane.Visibility = (_hidden || _sctHidden) ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Master SCT toggle (toolbar ⚡ / tray / Ctrl+Alt+C), remembered.</summary>
    private void ToggleSct()
    {
        _sctHidden = !_sctHidden;
        _config.Overlay.SctVisible = !_sctHidden;
        _configService.SaveSettings(_config);
        if (_hidden && !_sctHidden) ToggleHide();
        UpdateSctVisibility();
        _vm.Flash(_sctHidden ? "Combat text hidden." : "Combat text shown.");
    }

    private void RebuildFlashWindow()
    {
        if (_flash is not null) { try { _flash.Close(); } catch { /* ignore */ } }
        _flash = new FlashWindow(_configService, _config.Overlay.Opacity,
            _config.Overlay.FlashFontSize, _config.Overlay.FlashWidth);
        _flash.Show();
        _flash.SetLocked(_vm.Locked);
        UpdateFlashVisibility();
    }

    private void UpdateFlashVisibility()
    {
        if (_flash is not null)
            _flash.Visibility = (_hidden || _flashHidden) ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Show/hide the flash-alert area (tray / Manager page), and remember it.</summary>
    private void ToggleFlash()
    {
        _flashHidden = !_flashHidden;
        _config.Overlay.FlashVisible = !_flashHidden;
        _configService.SaveSettings(_config);
        if (_hidden && !_flashHidden) ToggleHide(); // unhide everything if it was globally hidden
        UpdateFlashVisibility();
        _vm.Flash(_flashHidden ? "Flash alerts hidden." : "Flash alerts shown.");
    }

    private void OnFlashRequested(string text, string color)
    {
        if (_hidden || _flashHidden) return;
        _flash?.Flash(text, BrushFromColor(color));
    }

    private static Brush BrushFromColor(string color)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        catch { return new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x33)); }
    }

    // ---- timer panel --------------------------------------------------------

    private void RebuildTimerWindow()
    {
        if (_timer is not null) { try { _timer.Close(); } catch { /* ignore */ } }
        _timer = new TimerWindow(_configService, _alerts, _config.Overlay.TimerSeconds, _config.Overlay.Opacity,
            onDurationSet: s => { _config.Overlay.TimerSeconds = s; _configService.SaveSettings(_config); });
        _timer.RecentKillsProvider = () =>
        {
            var tracked = _configService.LoadRespawns();
            return (IReadOnlyList<(string, string, DateTime, bool)>)_raids.RecentDeaths
                .Select(d => (d.Name, d.Zone, d.When,
                    tracked.Any(r => r.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        };
        _timer.AddRespawnRequested = (name, zone, seconds) =>
        {
            var list = _configService.LoadRespawns();
            if (list.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new Models.RespawnEntry { Name = name, Zone = zone, Seconds = seconds });
            _configService.SaveRespawns(list);
            // The engine holds the same trigger list instance, so re-merging the
            // timerAuto triggers makes the new respawn live immediately.
            MergeGlobalRespawns(_config);
            _vm.Flash(seconds > 0
                ? $"Respawn added: {name} ({Services.DurationText.Compact(seconds)})."
                : $"Respawn added: {name} — learning its time from your kills.");
        };
        _timer.ManageRespawnsRequested = () => OpenManager("Respawns");
        _timer.RespawnLookup = name => _respawnCache.FirstOrDefault(
            r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _timer.RespawnsProvider = () => _respawnCache;
        _timer.Show();
        _timer.RefreshWatching(); // quiet rows for the current zone's watched mobs
        UpdateTimerVisibility();
    }

    private void UpdateTimerVisibility()
    {
        if (_timer is not null)
            _timer.Visibility = (_hidden || _timerHidden) ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Show/hide the repop timer watch (⏱ button / tray / Ctrl+Alt+R), and remember it.</summary>
    private void ToggleTimer()
    {
        _timerHidden = !_timerHidden;
        _config.Overlay.TimerVisible = !_timerHidden;
        _configService.SaveSettings(_config);
        if (_hidden && !_timerHidden) ToggleHide(); // unhide everything if it was globally hidden
        UpdateTimerVisibility();
        _vm.Flash(_timerHidden ? "Repop timer hidden." : "Repop timer shown.");
    }

    // ---- DPS meter panel ----------------------------------------------------

    private void RebuildMeterWindow()
    {
        if (_meter is not null) { try { _meter.Close(); } catch { /* ignore */ } }
        _meter = new MeterWindow(_configService, _combat, _raids, _loot, _skyQuests,
            _config.Overlay.Opacity,
            _config.Overlay.SkillTrackerSkills, _config.Overlay.SkillTrackerVisible,
            _config.Overlay.ProcWatcherVisible, _config.Overlay.MeterSoloMode);
        _meter.SoloModeChanged += solo =>
        {
            _config.Overlay.MeterSoloMode = solo;
            _configService.SaveSettings(_config);
        };
        _meter.Show();
        UpdateMeterVisibility();
    }

    private void UpdateMeterVisibility()
    {
        if (_meter is not null)
            _meter.Visibility = (_hidden || _meterHidden) ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Show/hide the DPS meter (toolbar button / tray / Ctrl+Alt+D), and remember it.</summary>
    private void ToggleMeter()
    {
        _meterHidden = !_meterHidden;
        _config.Overlay.MeterVisible = !_meterHidden;
        _configService.SaveSettings(_config);
        if (_hidden && !_meterHidden) ToggleHide(); // unhide everything if it was globally hidden
        UpdateMeterVisibility();
        _vm.Flash(_meterHidden ? "DPS meter hidden." : "DPS meter shown.");
    }

    /// <summary>Show/hide the proc watcher on the meter (tray / Manager page), and remember it.</summary>
    private void ToggleProcs()
    {
        _config.Overlay.ProcWatcherVisible = !_config.Overlay.ProcWatcherVisible;
        _configService.SaveSettings(_config);
        if (_hidden && _config.Overlay.ProcWatcherVisible) ToggleHide();
        _meter?.SetProcsVisible(_config.Overlay.ProcWatcherVisible);
        if (_config.Overlay.ProcWatcherVisible && _meterHidden) ToggleMeter(); // it lives on the meter
        _vm.Flash(_config.Overlay.ProcWatcherVisible ? "Proc watcher shown." : "Proc watcher hidden.");
    }

    /// <summary>Show/hide the skills section on the meter (tray / Manager page), and remember it.</summary>
    private void ToggleSkills()
    {
        _skillsHidden = !_skillsHidden;
        _config.Overlay.SkillTrackerVisible = !_skillsHidden;
        _configService.SaveSettings(_config);
        if (_hidden && !_skillsHidden) ToggleHide(); // unhide everything if it was globally hidden
        _meter?.SetSkillsVisible(!_skillsHidden);
        if (!_skillsHidden && _meterHidden) ToggleMeter(); // the section lives on the meter
        _vm.Flash(_skillsHidden ? "Skill tracker hidden." : "Skill tracker shown.");
    }

    // ---- matrix panels ------------------------------------------------------

    private void RebuildMatrixWindows()
    {
        _selfMatrix = RebuildPanel(_selfMatrix, "selfMatrix", "Self Buffs",
            _engine.SelfCells, defaultLeft: 60, defaultTop: 420);
        _targetMatrix = RebuildPanel(_targetMatrix, "targetDebuffs", "Target Debuffs",
            _engine.TargetCells, defaultLeft: 420, defaultTop: 420);
        RebuildEnemyDotsWindow();
        RebuildConditionsWindow();
        RebuildSkyHelperWindow();
        RebuildSessionStatsWindow();
        RebuildRemindersWindow();
        UpdateMatrixVisibility();
    }

    private void RebuildRemindersWindow()
    {
        if (_remindersWin is not null) { try { _remindersWin.Close(); } catch { /* ignore */ } _remindersWin = null; }
        if (!_config.Overlay.RemindersVisible) return;
        _remindersWin = new RemindersWindow(_engine.Reminders, _configService, _config.Overlay.Opacity);
        _remindersWin.Show();
        _remindersWin.SetLocked(_vm.Locked);
        _remindersWin.SetHidden(_hidden);
    }

    private void ToggleSelfMatrix()
    {
        _config.Overlay.SelfMatrixVisible = !_config.Overlay.SelfMatrixVisible;
        _configService.SaveSettings(_config);
        UpdateMatrixVisibility();
        _vm.Flash(_config.Overlay.SelfMatrixVisible ? "Self buffs shown." : "Self buffs hidden.");
    }

    private void ToggleTargetMatrix()
    {
        _config.Overlay.TargetMatrixVisible = !_config.Overlay.TargetMatrixVisible;
        _configService.SaveSettings(_config);
        UpdateMatrixVisibility();
        _vm.Flash(_config.Overlay.TargetMatrixVisible ? "Target debuffs shown." : "Target debuffs hidden.");
    }

    private void ToggleReminders()
    {
        _config.Overlay.RemindersVisible = !_config.Overlay.RemindersVisible;
        _configService.SaveSettings(_config);
        RebuildRemindersWindow();
        _vm.Flash(_config.Overlay.RemindersVisible ? "Rebuff reminders shown." : "Rebuff reminders hidden.");
    }

    private void RebuildEnemyDotsWindow()
    {
        if (_enemyDotsWin is not null) { try { _enemyDotsWin.Close(); } catch { /* ignore */ } _enemyDotsWin = null; }
        if (!_config.Overlay.EnemyDotsVisible) return;
        _enemyDotsWin = new EnemyDotsWindow(_combat, _configService, _config.Overlay.Opacity,
            _config.Overlay.EnemyDotsGroupByMob);
        _enemyDotsWin.Show();
        _enemyDotsWin.SetLocked(_vm.Locked);
        _enemyDotsWin.SetHidden(_hidden);
    }

    private void RebuildConditionsWindow()
    {
        if (_conditionsWin is not null) { try { _conditionsWin.Close(); } catch { /* ignore */ } _conditionsWin = null; }
        if (!_config.Overlay.ConditionsVisible) return;
        _conditionsWin = new ConditionsWindow(_conditions, _configService, _config.Overlay.Opacity);
        _conditionsWin.Show();
        _conditionsWin.SetLocked(_vm.Locked);
        _conditionsWin.SetHidden(_hidden);
    }

    private void ToggleConditions()
    {
        _config.Overlay.ConditionsVisible = !_config.Overlay.ConditionsVisible;
        _configService.SaveSettings(_config);
        RebuildConditionsWindow();
        _vm.Flash(_config.Overlay.ConditionsVisible ? "Condition badges shown." : "Condition badges hidden.");
    }

    private void RebuildSkyHelperWindow()
    {
        if (_skyHelperWin is not null) { try { _skyHelperWin.Close(); } catch { /* ignore */ } _skyHelperWin = null; }
        if (!_config.Overlay.SkyHelperVisible) return;
        _skyHelper.ShowCompleted = _config.Overlay.SkyHelperShowCompleted;
        _skyHelperWin = new SkyHelperWindow(_skyHelper, _skyQuests, _configService, _config.Overlay.Opacity)
        {
            OpenSkyRequested = OpenSkyQuests,
            ShowCompletedChanged = on =>
            {
                _config.Overlay.SkyHelperShowCompleted = on;
                _configService.SaveSettings(_config);
            },
        };
        _skyHelperWin.Show();
        _skyHelperWin.SetLocked(_vm.Locked);
        _skyHelperWin.SetHidden(_hidden);
    }

    private void ToggleSkyHelper()
    {
        _config.Overlay.SkyHelperVisible = !_config.Overlay.SkyHelperVisible;
        _configService.SaveSettings(_config);
        RebuildSkyHelperWindow();
        _vm.Flash(_config.Overlay.SkyHelperVisible ? "Sky quest helper on." : "Sky quest helper off.");
    }

    private void RebuildSessionStatsWindow()
    {
        if (_sessionWin is not null) { try { _sessionWin.Close(); } catch { /* ignore */ } _sessionWin = null; }
        if (!_config.Overlay.SessionStatsVisible) return;
        _sessionWin = new SessionStatsWindow(_session, _configService, _config.Overlay.Opacity,
            ParseStatsSlice(_config.Overlay.SessionStatsSlice),
            _config.Overlay.SessionStatsExactTier,
            _config.Overlay.SessionStatsActiveBasis ? SessionStats.Basis.Active : SessionStats.Basis.Elapsed);
        _sessionWin.SettingsChanged += (slice, exactTier, basis) =>
        {
            _config.Overlay.SessionStatsSlice = slice switch
            {
                SessionStats.Slice.Session => "session",
                SessionStats.Slice.Zone => "zone",
                SessionStats.Slice.All => "all",
                _ => "zoneSession",
            };
            _config.Overlay.SessionStatsExactTier = exactTier;
            _config.Overlay.SessionStatsActiveBasis = basis == SessionStats.Basis.Active;
            _configService.SaveSettings(_config);
        };
        _sessionWin.Show();
        _sessionWin.SetLocked(_vm.Locked);
        _sessionWin.SetHidden(_hidden);
    }

    private static SessionStats.Slice ParseStatsSlice(string s) => s switch
    {
        "session" => SessionStats.Slice.Session,
        "zone" => SessionStats.Slice.Zone,
        "all" => SessionStats.Slice.All,
        _ => SessionStats.Slice.ZoneSession,
    };

    private void ToggleSessionStats()
    {
        _config.Overlay.SessionStatsVisible = !_config.Overlay.SessionStatsVisible;
        _configService.SaveSettings(_config);
        RebuildSessionStatsWindow();
        _vm.Flash(_config.Overlay.SessionStatsVisible ? "Session stats shown." : "Session stats hidden.");
    }

    private MatrixWindow RebuildPanel(MatrixWindow? existing, string key, string title,
        System.Collections.ObjectModel.ObservableCollection<ViewModels.MatrixCellViewModel> cells,
        double defaultLeft, double defaultTop)
    {
        if (existing is not null) { try { existing.Close(); } catch { /* ignore */ } }
        var w = new MatrixWindow(key, title, cells, _config.Overlay.MatrixColumns,
            _configService, _config.Overlay.Opacity, defaultLeft, defaultTop);
        w.Show();
        w.SetLocked(_vm.Locked);
        return w;
    }

    private void UpdateMatrixVisibility()
    {
        SetPanelVisible(_selfMatrix, _engine.SelfCells.Count, _config.Overlay.SelfMatrixVisible);
        SetPanelVisible(_targetMatrix, _engine.TargetCells.Count, _config.Overlay.TargetMatrixVisible);
    }

    private void SetPanelVisible(MatrixWindow? w, int cellCount, bool enabled)
    {
        if (w is null) return;
        w.Visibility = (!_hidden && enabled && cellCount > 0) ? Visibility.Visible : Visibility.Hidden;
    }

    private void ApplyOpacity() =>
        Opacity = Math.Clamp(_config.Overlay.Opacity <= 0 ? 1.0 : _config.Overlay.Opacity, 0.1, 1.0);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        ApplyClickThrough();
        RegisterHotKeys();
    }

    // ---- log watcher --------------------------------------------------------

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = new LogWatcher(
            _config.Log,
            onLine: line => Dispatcher.BeginInvoke(() =>
            {
                _engine.ProcessLine(line);
                _combat.ProcessLine(line);
                _raids.ProcessLine(line);
                _loot.ProcessLine(line);
                _skyQuests.ProcessLine(line);
                _spellLib.MarkSeenFromLine(line);
                _durations.ProcessLine(line);
                _conditions.ProcessLine(line); // live CC state — not fed on catch-up
                _respawnLearner.ProcessLine(line); // live-only too: stale lines would mint stale sightings
                _skyHelper.ProcessLine(line);      // ibid. — quest-dropper sightings
                _session.ProcessLine(line);    // leveling pace (rebuilt by catch-up)
                if (TryParseLineTime(line, out var lineTime)) NoteLineSeen(lineTime);
                _logBus.Publish(line);
            }),
            onStatus: msg => Dispatcher.BeginInvoke(() => _vm.LogStatus = msg),
            onFileChanged: path => Dispatcher.BeginInvoke(() =>
            {
                _detectedName = ExtractCharacterName(path);
                ApplySelfName();
            }));
        _watcher.Start();
    }

    // ---- character-name auto-detection ---------------------------------------

    private string _detectedName = "";

    /// <summary>"eqlog_Thorrak_paineel.txt" → "Thorrak".</summary>
    private static string ExtractCharacterName(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileName(path), @"^eqlog_(?<name>[A-Za-z]+)[_.]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["name"].Value : "";
    }

    /// <summary>An explicit Character name in Settings wins; otherwise the log filename's.</summary>
    private void ApplySelfName()
    {
        string name = !string.IsNullOrWhiteSpace(_config.CharacterName)
            ? _config.CharacterName
            : _detectedName;
        if (string.IsNullOrWhiteSpace(name) || _combat.SelfName == name) return;
        _combat.SelfName = name;
        _session.SelfName = name; // the /who level row must be YOUR row
        Log.Info($"Combat parser character name: '{name}'" +
                 (string.IsNullOrWhiteSpace(_config.CharacterName) ? " (auto-detected from log filename)" : ""));
    }

    // ---- hotkeys ------------------------------------------------------------

    private void RegisterHotKeys()
    {
        uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        NativeMethods.RegisterHotKey(_hwnd, HK_LOCK,   mods, 0x4C); // L
        NativeMethods.RegisterHotKey(_hwnd, HK_TEST,   mods, 0x54); // T
        NativeMethods.RegisterHotKey(_hwnd, HK_HIDE,   mods, 0x48); // H
        NativeMethods.RegisterHotKey(_hwnd, HK_MUTE,   mods, 0x53); // S
        NativeMethods.RegisterHotKey(_hwnd, HK_QUIT,   mods, 0x51); // Q
        NativeMethods.RegisterHotKey(_hwnd, HK_REPOP,  mods, 0x52); // R
        NativeMethods.RegisterHotKey(_hwnd, HK_METER,  mods, 0x44); // D
        NativeMethods.RegisterHotKey(_hwnd, HK_SCT,    mods, 0x43); // C
    }

    private void UnregisterHotKeys()
    {
        foreach (int id in new[] { HK_LOCK, HK_TEST, HK_HIDE, HK_MUTE, HK_QUIT, HK_REPOP, HK_METER, HK_SCT })
            NativeMethods.UnregisterHotKey(_hwnd, id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HK_LOCK:   ToggleLock();       handled = true; break;
                case HK_TEST:
                    _engine.AddDemoTimer(); _engine.AddDemoMatrixCell(); _engine.AddDemoTargetCell();
                    _engine.AddDemoReminder();
                    _combat.AddDemoFight(); _combat.AddDemoEnemyDots(); _conditions.AddDemo();
                    if (_sessionWin is not null && !_session.HasData)
                    { _session.AddDemo(DateTime.Now); _sessionWin.Refresh(); }
                    UpdateMatrixVisibility();
                    OnFlashRequested("FLASH TEST — Get out of the fire!", "#FFCC33");
                    if (!_hidden && !_sctHidden)
                        foreach (var (kind, lane) in _sctLanes)
                            if (kind == CombatParser.SctKind.Progress)
                            {
                                lane.Post("xp", 2.4, flavor: CombatParser.SctFlavor.Melee, text: "+2,4%");
                                lane.Post("Steel Warriors", 5, flavor: CombatParser.SctFlavor.Spell, text: "+5");
                                lane.Post("Befallen Inhabitants", -30, flavor: CombatParser.SctFlavor.Proc, text: "-30");
                            }
                            else lane.SpawnDemo();
                    handled = true; break;
                case HK_HIDE:   ToggleHide();       handled = true; break;
                case HK_MUTE:   ToggleMute();       handled = true; break;
                case HK_QUIT:   Close();            handled = true; break;
                case HK_REPOP:  ToggleTimer();      handled = true; break;
                case HK_METER:  ToggleMeter();      handled = true; break;
                case HK_SCT:    ToggleSct();        handled = true; break;
            }
        }
        return nint.Zero;
    }

    // ---- lock / click-through -----------------------------------------------

    private void ToggleLock()
    {
        _vm.Locked = !_vm.Locked;
        _config.Overlay.Locked = _vm.Locked;
        ApplyClickThrough();
        ApplyLockVisual();
        _selfMatrix?.SetLocked(_vm.Locked);
        _targetMatrix?.SetLocked(_vm.Locked);
        _enemyDotsWin?.SetLocked(_vm.Locked);
        _conditionsWin?.SetLocked(_vm.Locked);
        _skyHelperWin?.SetLocked(_vm.Locked);
        _sessionWin?.SetLocked(_vm.Locked);
        _remindersWin?.SetLocked(_vm.Locked);
        _flash?.SetLocked(_vm.Locked);
        foreach (var lane in _sctLanes.Values) lane.SetLocked(_vm.Locked);
        _configService.SaveWindowState(_config.Overlay);
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero && _vm is not null)
            NativeMethods.SetClickThrough(_hwnd, _vm.Locked);
    }

    private void ApplyLockVisual() =>
        RootBorder.Background = _vm.Locked ? Brushes.Transparent : UnlockedBackdrop;

    private void ToggleEnemyDots()
    {
        _config.Overlay.EnemyDotsVisible = !_config.Overlay.EnemyDotsVisible;
        _configService.SaveSettings(_config);
        RebuildEnemyDotsWindow();
        _vm.Flash(_config.Overlay.EnemyDotsVisible ? "Enemy DoTs shown." : "Enemy DoTs hidden.");
    }

    private void ToggleHide()
    {
        _hidden = !_hidden;
        _enemyDotsWin?.SetHidden(_hidden);
        _conditionsWin?.SetHidden(_hidden);
        _skyHelperWin?.SetHidden(_hidden);
        _sessionWin?.SetHidden(_hidden);
        _remindersWin?.SetHidden(_hidden);
        UpdateBarsVisibility();
        UpdateMatrixVisibility();
        UpdateTimerVisibility();
        UpdateMeterVisibility();
        UpdateSctVisibility();
        UpdateFlashVisibility();
        UpdateToolbarVisibility();
    }

    /// <summary>
    /// Tray recovery hammer: home every panel AND unlock + unhide. A locked
    /// overlay with no active bars is invisible — recovery must reveal it.
    /// </summary>
    private void ResetPosition()
    {
        _hidden = false;
        Visibility = Visibility.Visible;
        Topmost = true;
        Activate();

        if (_vm.Locked)
        {
            _vm.Locked = false;
            _config.Overlay.Locked = false;
            ApplyClickThrough();
            ApplyLockVisual();
            _selfMatrix?.SetLocked(false);
            _targetMatrix?.SetLocked(false);
            _enemyDotsWin?.SetLocked(false);
            _conditionsWin?.SetLocked(false);
            _skyHelperWin?.SetLocked(false);
            _sessionWin?.SetLocked(false);
            _remindersWin?.SetLocked(false);
            _flash?.SetLocked(false);
            foreach (var lane in _sctLanes.Values) lane.SetLocked(false);
            _configService.SaveWindowState(_config.Overlay);
        }

        _mainPlacement?.ResetToDefault();
        _selfMatrix?.ResetPosition();
        _targetMatrix?.ResetPosition();
        _enemyDotsWin?.ResetPosition();
        _conditionsWin?.ResetPosition();
        _skyHelperWin?.ResetPosition();
        _sessionWin?.ResetPosition();
        _remindersWin?.ResetPosition();
        _timer?.ResetPosition();
        _meter?.ResetPosition();
        _flash?.ResetPosition();
        _toolbarWin?.ResetPosition();
        foreach (var lane in _sctLanes.Values) lane.ResetPosition();
        _toolbarHidden = false; // recovery must reveal everything
        _config.Overlay.ToolbarVisible = true;
        _barsHidden = false;
        _config.Overlay.BarsVisible = true;
        UpdateBarsVisibility();
        UpdateMatrixVisibility();
        UpdateTimerVisibility();
        UpdateMeterVisibility();
        UpdateSctVisibility();
        UpdateToolbarVisibility();
        _vm?.Flash("Panels reset — overlay unlocked.");
    }

    // ---- config -------------------------------------------------------------

    private void OpenManager(string? page = null)
    {
        if (_manager is null)
        {
            _manager = new TriggerManagerWindow(_configService, _config, _logBus, _alerts, _raids, _spellLib, _combat, OnManagerApplied, _durations,
                logStatus: () => _vm.LogStatus)
            {
                ReparseFullLogRequested = ReparseFullLog,
                ReparseOtherRequested = MergeLogFile,
                ResetAndRebuildRequested = ResetAndRebuild,
            };
            _manager.Closed += (_, _) => _manager = null;
            _manager.Show();
        }
        if (page is not null) _manager.SelectPage(page);
        BringToFront(_manager);
    }

    /// <summary>
    /// Force a window to the foreground. The overlay is a no-activate topmost
    /// window, so a child window it opens won't come to front on its own — the
    /// brief Topmost toggle bumps it above everything, then releases.
    /// </summary>
    private static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    /// <summary>Load the active loadout's (compiled) triggers into <paramref name="cfg"/>.</summary>
    private void LoadActiveLoadoutInto(AppConfig cfg)
    {
        Loadout? lo = null;
        try { lo = _configService.LoadLoadout(cfg.ActiveLoadout); }
        catch (Exception ex)
        {
            MessageBox.Show($"Loadout '{cfg.ActiveLoadout}' has an invalid pattern:\n{ex.Message}",
                "EQL Assistant", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (lo is null)
        {
            foreach (var candidate in _configService.ListLoadouts())
            {
                try { lo = _configService.LoadLoadout(candidate.Name); if (lo is not null) break; }
                catch { /* skip a broken loadout */ }
            }
        }

        if (lo is not null)
        {
            cfg.Triggers = lo.Triggers;
            cfg.ActiveLoadout = lo.Name;
        }

        MergeGlobalRespawns(cfg);
    }

    /// <summary>
    /// Respawn timers are global (respawns.json), not per-loadout — merge them
    /// into the active trigger set so they survive loadout switches.
    /// </summary>
    /// <summary>In-memory respawn entries, refreshed on every merge — the
    /// repop watch reads alert config from here every tick, never the disk.</summary>
    private List<Models.RespawnEntry> _respawnCache = new();

    private void MergeGlobalRespawns(AppConfig cfg)
    {
        _respawnCache = _configService.LoadRespawns();
        cfg.Triggers.RemoveAll(t => t.Panel == Panels.TimerAuto);
        cfg.Triggers.AddRange(_configService.BuildRespawnTriggers());
        _respawnLearner.UpdateEntries(_respawnCache);
        _timer?.RefreshWatching(); // the watching rows mirror the list
    }

    // ---- respawn learner (death → next-appearance gaps + UP sightings) --------

    private readonly Services.RespawnLearner _respawnLearner = new();

    private void WireRespawnLearner()
    {
        _respawnLearner.Sighted += name => _timer?.NotifySighted(name);
        _respawnLearner.ZoneChanged += zone => _timer?.SetZone(zone);
        _respawnLearner.GapLearned += (name, gap, when) =>
        {
            var entry = _respawnCache.FirstOrDefault(r =>
                r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            entry.AddGap(gap, when);
            _configService.SaveRespawns(_respawnCache);
            // Re-merge so the timerAuto trigger picks up the new estimate —
            // the engine holds the same trigger list instance.
            MergeGlobalRespawns(_config);
            _engine.UpdateConfig(_config);
            double est = entry.EffectiveSeconds ?? gap;
            _vm.Flash($"{name}: respawn ≤ {Services.DurationText.Compact(est)} ({entry.Gaps.Count} gap{(entry.Gaps.Count == 1 ? "" : "s")})");
            Log.Info($"[respawn] {name} gap {gap:0}s recorded — estimate now {est:0}s from {entry.Gaps.Count} gap(s).");
        };
    }

    /// <summary>Apply settings + active loadout saved from the manager, live.</summary>
    private void OnManagerApplied(string activeName)
    {
        AppConfig cfg;
        try { cfg = _configService.LoadSettings(); }
        catch { cfg = _config; }

        // Keep the live window position/lock rather than the persisted values.
        cfg.Overlay.Left = Left;
        cfg.Overlay.Top = Top;
        cfg.Overlay.Locked = _vm.Locked;
        cfg.ActiveLoadout = activeName;
        LoadActiveLoadoutInto(cfg);

        _config = cfg;
        _alerts.Muted = cfg.Overlay.Muted;
        _timerHidden = !cfg.Overlay.TimerVisible;
        _meterHidden = !cfg.Overlay.MeterVisible;
        _skillsHidden = !cfg.Overlay.SkillTrackerVisible;
        _flashHidden = !cfg.Overlay.FlashVisible;
        _sctHidden = !cfg.Overlay.SctVisible;
        _toolbarHidden = !cfg.Overlay.ToolbarVisible;
        _barsHidden = !cfg.Overlay.BarsVisible;
        _combat.PetName = cfg.Overlay.PetName;
        ApplySelfName();

        bool wasLocked = _vm.Locked;
        // No Reset here: UpdateConfig prunes what the new config dropped but
        // keeps running bars and active matrix timers alive across a save.
        _engine.UpdateConfig(cfg);

        // Rebuild the VM so sizing/header changes take effect immediately.
        _vm = new OverlayViewModel(_engine, cfg)
        {
            Locked = wasLocked,
            LoadoutName = cfg.ActiveLoadout,
        };
        DataContext = _vm;

        SizerHost.Width = cfg.Overlay.Width;
        ApplyOpacity();
        ApplyLockVisual();
        StartWatcher();
        _mainPlacement?.Reload();   // pick up any anchor change from Settings
        RebuildMatrixWindows();

        // The watch and meter are updated IN PLACE: rebuilding them would wipe
        // running repop timers (and close an open fight-history window).
        if (_timer is null) RebuildTimerWindow();
        else { _timer.ApplySettings(cfg.Overlay.Opacity); UpdateTimerVisibility(); }
        if (_meter is null) RebuildMeterWindow();
        else
        {
            _meter.ApplySettings(cfg.Overlay.Opacity,
                cfg.Overlay.SkillTrackerSkills, cfg.Overlay.SkillTrackerVisible,
                cfg.Overlay.ProcWatcherVisible, cfg.Overlay.MeterSoloMode);
            UpdateMeterVisibility();
        }

        RebuildFlashWindow();
        RebuildSctLanes();
        if (_toolbarWin is not null)
        {
            _toolbarWin.DataContext = _vm; // rebound: the VM was rebuilt above
            _toolbarWin.ReloadPlacement();
            UpdateToolbarVisibility();
        }
        UpdateBarsVisibility();
        _vm.Flash("Settings applied.");
        Log.Info($"Settings applied from manager. loadout='{cfg.ActiveLoadout}', triggers={cfg.Triggers.Count}, " +
                 $"selfCells={_engine.SelfCells.Count}");
    }

    private void ApplyLoadout(string name)
    {
        Loadout? lo;
        try { lo = _configService.LoadLoadout(name); }
        catch { _vm.Flash($"Loadout '{name}' has a bad pattern."); return; }
        if (lo is null) return;

        _config.Triggers = lo.Triggers;
        _config.ActiveLoadout = lo.Name;
        _spellLib.HealLibraryTriggers(_config.Triggers); // pre-2.9 lib types heal
        MergeGlobalRespawns(_config);
        _configService.SaveSettings(_config); // remember the choice

        _engine.Reset();
        _engine.UpdateConfig(_config);
        UpdateMatrixVisibility();
        _vm.LoadoutName = lo.Name;
        _vm.Flash($"Switched to: {lo.Name}");
        Log.Info($"Loadout switched to '{lo.Name}' ({lo.Triggers.Count} triggers)");
    }

    // ---- repop / respawn timer ----------------------------------------------

    // ---- detached toolbar (command strip) -------------------------------------

    private Views.ToolbarWindow? _toolbarWin;
    private bool _toolbarHidden;

    /// <summary>The command strip lives in its own always-clickable window; the
    /// padlock only governs the other panels. Hide it via ☰/tray → Panels.</summary>
    private void BuildToolbarWindow()
    {
        _toolbarWin = new Views.ToolbarWindow(_configService)
        {
            DataContext = _vm,
            QuitRequested = Close,
            LockRequested = ToggleLock,
            MuteRequested = ToggleMute,
            ManageRequested = () => OpenManager(),
            MenuRequested = el => ShowMainMenu(el as UIElement),
            RaidRequested = OpenRaidKills,
            QuestsRequested = OpenSkyQuests,
            LootRequested = OpenLootHistory,
            SheetRequested = OpenCharacterSheet,
        };
        _toolbarWin.Show();
        UpdateToolbarVisibility();
    }

    private void UpdateToolbarVisibility()
    {
        if (_toolbarWin is not null)
            _toolbarWin.Visibility = !_hidden && !_toolbarHidden
                ? Visibility.Visible : Visibility.Hidden;
    }

    private void ToggleToolbar()
    {
        _toolbarHidden = !_toolbarHidden;
        _config.Overlay.ToolbarVisible = !_toolbarHidden;
        _configService.SaveSettings(_config);
        UpdateToolbarVisibility();
    }

    // ---- buff bars panel (this window) ----------------------------------------

    private bool _barsHidden;

    private void UpdateBarsVisibility() =>
        Visibility = !_hidden && !_barsHidden ? Visibility.Visible : Visibility.Hidden;

    private void ToggleBars()
    {
        _barsHidden = !_barsHidden;
        _config.Overlay.BarsVisible = !_barsHidden;
        _configService.SaveSettings(_config);
        UpdateBarsVisibility();
        _vm.Flash(_barsHidden ? "Buff bars hidden." : "Buff bars shown.");
    }

    /// <summary>Toolbar ☰ — the SLIM play-time menu: what isn't already a
    /// toolbar button. A WPF ContextMenu, so it wears the app's dark theme
    /// (the WinForms tray menu stays native — the OS look is expected there)
    /// and each Panels row carries a REAL settings-cog button.</summary>
    private ContextMenu? _burgerMenu;

    private void ShowMainMenu(UIElement? target)
    {
        _burgerMenu ??= BuildBurgerMenu();
        // A PlacementTarget is load-bearing, not cosmetic: without one a
        // programmatically-opened ContextMenu has no tree context — Windows'
        // handedness setting decides which side it opens on, and SUBMENUS
        // silently refuse to open at all.
        _burgerMenu.PlacementTarget = target;
        _burgerMenu.Placement = target is null
            ? System.Windows.Controls.Primitives.PlacementMode.MousePoint
            : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _burgerMenu.IsOpen = true;
    }

    private ContextMenu BuildBurgerMenu()
    {
        var menu = new ContextMenu();

        // The most common play-time task sits on top.
        var create = new MenuItem { Header = "Create trigger…" };
        create.Click += (_, _) => OpenManager("Triggers");
        menu.Items.Add(create);
        menu.Items.Add(new Separator());

        var panels = new MenuItem { Header = "Panels" };
        panels.Items.Add(BurgerPanelRow("Toolbar", ToggleToolbar, "General", () => !_toolbarHidden));
        panels.Items.Add(BurgerPanelRow("Buff bars", ToggleBars, "Bars & matrices", () => !_barsHidden));
        panels.Items.Add(BurgerPanelRow("Self-buffs matrix", ToggleSelfMatrix, "Bars & matrices", () => _config.Overlay.SelfMatrixVisible));
        panels.Items.Add(BurgerPanelRow("Target-debuffs matrix", ToggleTargetMatrix, "Bars & matrices", () => _config.Overlay.TargetMatrixVisible));
        panels.Items.Add(BurgerPanelRow("Rebuff reminders", ToggleReminders, "Bars & matrices", () => _config.Overlay.RemindersVisible));
        panels.Items.Add(BurgerPanelRow("Enemy DoTs", ToggleEnemyDots, "Bars & matrices", () => _config.Overlay.EnemyDotsVisible));
        panels.Items.Add(BurgerPanelRow("Condition badges (stun/fear)", ToggleConditions, null, () => _config.Overlay.ConditionsVisible));
        panels.Items.Add(BurgerPanelRow("Sky quest helper", ToggleSkyHelper, null, () => _config.Overlay.SkyHelperVisible));
        panels.Items.Add(BurgerPanelRow("Session stats (XP/AA/motes)", ToggleSessionStats, null, () => _config.Overlay.SessionStatsVisible));
        panels.Items.Add(BurgerPanelRow("Repop timer", ToggleTimer, "Repop timer", () => !_timerHidden));
        panels.Items.Add(BurgerPanelRow("DPS meter", ToggleMeter, "DPS + Skills, Procs", () => !_meterHidden));
        panels.Items.Add(BurgerPanelRow("DPS meter · skills section", ToggleSkills, "DPS + Skills, Procs", () => !_skillsHidden));
        panels.Items.Add(BurgerPanelRow("DPS meter · proc watcher", ToggleProcs, "DPS + Skills, Procs", () => _config.Overlay.ProcWatcherVisible));
        panels.Items.Add(BurgerPanelRow("Combat text", ToggleSct, "Combat text", () => !_sctHidden));
        panels.Items.Add(BurgerPanelRow("Flash alerts", ToggleFlash, "Flash alerts", () => !_flashHidden));
        panels.SubmenuOpened += (_, _) =>
        {
            foreach (var it in panels.Items.OfType<MenuItem>())
                if (it.Tag is Func<bool> isOn) it.IsChecked = isOn();
        };
        menu.Items.Add(panels);

        var loadout = new MenuItem { Header = "Loadout" };
        loadout.Items.Add(new MenuItem { Header = "…" }); // arrow seed; replaced on open
        loadout.SubmenuOpened += (_, _) =>
        {
            loadout.Items.Clear();
            foreach (var name in _configService.ListLoadouts().Select(l => l.Name))
            {
                string captured = name;
                var mi = new MenuItem
                {
                    Header = name,
                    IsChecked = string.Equals(name, _config.ActiveLoadout, StringComparison.OrdinalIgnoreCase),
                };
                mi.Click += (_, _) => ApplyLoadout(captured);
                loadout.Items.Add(mi);
            }
            loadout.Items.Add(new Separator());
            var manage = new MenuItem { Header = "Manage loadouts…" };
            manage.Click += (_, _) => OpenManager("Loadouts");
            loadout.Items.Add(manage);
        };
        menu.Items.Add(loadout);

        menu.Items.Add(new Separator());
        var recap = new MenuItem { Header = "Show last death recap" };
        recap.Click += (_, _) => OpenDeathRecap();
        menu.Items.Add(recap);
        var catchUp = new MenuItem { Header = "Catch up from today's log" };
        catchUp.Click += (_, _) => CatchUpToday();
        menu.Items.Add(catchUp);

        return menu;
    }

    /// <summary>One Panels row: the NAME toggles the panel (with a live
    /// checkmark); the right-aligned ⚙ — a real button — jumps to that
    /// panel's settings page. Rows without a page carry no cog.</summary>
    private MenuItem BurgerPanelRow(string text, Action toggle, string? settingsPage, Func<bool> isOn)
    {
        var item = new MenuItem { Tag = isOn };
        if (settingsPage is null)
        {
            item.Header = text;
        }
        else
        {
            var dock = new DockPanel { LastChildFill = true, MinWidth = 220 };
            var cog = new Button
            {
                Content = "\uE713", // MDL2 gear, same as the toolbar cog
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(10, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xAD)),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Settings — {settingsPage}",
            };
            cog.Click += (_, e) =>
            {
                e.Handled = true;
                if (_burgerMenu is not null) _burgerMenu.IsOpen = false;
                OpenManager(settingsPage);
            };
            DockPanel.SetDock(cog, Dock.Right);
            dock.Children.Add(cog);
            dock.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            item.Header = dock;
        }
        item.Click += (_, _) => toggle();
        return item;
    }

    /// <summary>A "timerAuto" trigger matched (e.g. a named mob death) — start the watch.</summary>
    private void OnTimerRequested(double seconds, string name)
    {
        if (_timerHidden)
        {
            _timerHidden = false;
            _config.Overlay.TimerVisible = true;
            _configService.SaveSettings(_config);
        }
        if (_hidden) ToggleHide();
        UpdateTimerVisibility();
        _timer?.StartWith(seconds, name);
        _vm.Flash($"{name} down — repop timer started.");
        Log.Info($"Auto-started repop timer ({seconds:0}s) from trigger '{name}'.");
    }

    private void ToggleMute()
    {
        _alerts.Muted = !_alerts.Muted;
        _config.Overlay.Muted = _alerts.Muted;
        _vm.Muted = _alerts.Muted;
        _vm.Flash(_alerts.Muted ? "Alerts muted." : "Alerts on.");
    }

    /// <summary>Raid-target fights are keepers by definition: auto-★ them into
    /// the persistent history and stamp the kill with time-to-kill + the fight
    /// link (the Raid Kills window's "fight ↗" button).</summary>
    private void OnFightArchived(CombatParser.FightRecord rec)
    {
        if (!_raids.IsTarget(rec.Label)) return;
        _raids.AttachFight(rec.Label, rec.EndedAt, rec.DurationSeconds);

        var saved = _configService.SavedFights;
        if (saved.Any(s => s.EndedAt == rec.EndedAt && s.Label == rec.Label)) return;
        saved.Add(rec);
        saved.Sort((a, b) => b.EndedAt.CompareTo(a.EndedAt));
        _configService.SaveSavedFights();
        Log.Info($"Raid fight auto-kept: {rec.Label} ({rec.DurationSeconds:0}s).");
    }

    private HistoryWindow? _historyWindow;

    /// <summary>Open the fight history focused on one fight (raid kill links).</summary>
    private void OpenFightHistory(DateTime endedAt, string label)
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(_combat, _configService, _raids, _loot, _skyQuests);
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        BringToFront(_historyWindow);
        _historyWindow.SelectFight(endedAt, label);
    }

    private void OpenRaidKills()
    {
        if (_raidsWindow is null)
        {
            _raidsWindow = new RaidKillsWindow(_raids) { OpenFightRequested = OpenFightHistory };
            _raidsWindow.Closed += (_, _) => _raidsWindow = null;
            _raidsWindow.Show();
        }
        BringToFront(_raidsWindow);
    }

    private Views.LootWindow? _lootWindow;

    private void OpenLootHistory()
    {
        if (_lootWindow is null)
        {
            _lootWindow = new Views.LootWindow(_loot);
            _lootWindow.Closed += (_, _) => _lootWindow = null;
            _lootWindow.Show();
        }
        BringToFront(_lootWindow);
    }

    private Views.InventoryWindow? _inventoryWindow;

    /// <summary>The ONE Character window (sheet · all items · exaltations ·
    /// focus board) over the game's own inventory dump. The toolbar's helm
    /// and chest are two doors into it, each landing on its own tab.</summary>
    private void OpenCharacterWindow(string tab)
    {
        if (_inventoryWindow is null)
        {
            string logPath = _watcher?.CurrentPath ?? "";
            var (name, server) = InventoryStore.ParseLogName(logPath);
            _inventoryWindow = new Views.InventoryWindow(
                InventoryStore.EqRootOf(logPath), name, server, _session);
            _inventoryWindow.Closed += (_, _) => _inventoryWindow = null;
            _inventoryWindow.Show();
        }
        _inventoryWindow.ShowTab(tab);
        BringToFront(_inventoryWindow);
    }

    private void OpenCharacterSheet() => OpenCharacterWindow("sheet");

    private Views.DeathRecapWindow? _recapWindow;
    private CombatParser.DeathEvent? _lastDeath;

    /// <summary>A death line appeared — remember it and (optionally) pop the recap.
    /// Catch-up replay records the death quietly instead of popping old news.</summary>
    private void OnPlayerDied(CombatParser.DeathEvent death)
    {
        _lastDeath = death;
        Log.Info($"Player died ({(death.Killer.Length > 0 ? death.Killer : "no killer line")}), " +
                 $"recap events={death.Events.Count}");
        if (_suppressSct || !_config.Overlay.DeathRecapAuto) return;
        OpenDeathRecap(activate: false); // appear over the game without stealing focus
    }

    private void OpenDeathRecap(bool activate = true)
    {
        if (_lastDeath is null)
        {
            // A visible answer — the toolbar status flash was easy to miss.
            // (Catch-up replays today's log, so today's deaths survive a restart.)
            System.Windows.MessageBox.Show(
                "No deaths recorded from today's log — long may it last.",
                "EQL Assistant", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_recapWindow is null)
        {
            _recapWindow = new Views.DeathRecapWindow(_lastDeath);
            _recapWindow.Closed += (_, _) => _recapWindow = null;
            _recapWindow.Show();
        }
        else _recapWindow.Update(_lastDeath);
        if (activate) BringToFront(_recapWindow);
    }

    private Views.SkyWindow? _skyWindow;

    private void OpenSkyQuests()
    {
        if (_skyWindow is null)
        {
            _skyWindow = new Views.SkyWindow(_skyQuests);
            _skyWindow.Closed += (_, _) => _skyWindow = null;
            _skyWindow.Show();
        }
        BringToFront(_skyWindow);
    }

    private void OpenConfigFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _configService.ConfigDirectory,
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }

    // ---- toolbar buttons ----------------------------------------------------

    private void Toolbar_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_vm.Locked)
            DragMove();
    }


    /// <summary>Test hook: add a demo bar so a self-test can force the bar template to render.</summary>
    internal void AddDemoForTest() => _engine?.AddDemoTimer();

    // ---- system tray --------------------------------------------------------

    private void SetupTrayIcon()
    {
        if (_tray is not null) return;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "EQL Assistant",
            Visible = true,
            Icon = LoadAppIcon(),
        };

        // Grouped: settings · overlay state · Panels/Loadout · tools · recovery · quit.
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Manage settings…", null, (_, _) => OpenManager());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleHide());
        menu.Items.Add("Lock / Unlock", null, (_, _) => ToggleLock());
        menu.Items.Add("Mute / Unmute", null, (_, _) => ToggleMute());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(BuildPanelsMenu());
        menu.Items.Add(BuildLoadoutMenu());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add("Raid kills…", null, (_, _) => OpenRaidKills());
        menu.Items.Add("Loot history…", null, (_, _) => OpenLootHistory());
        menu.Items.Add("Character…", null, (_, _) => OpenCharacterSheet());
        menu.Items.Add("Sky quests…", null, (_, _) => OpenSkyQuests());
        menu.Items.Add("Show last death recap", null, (_, _) => OpenDeathRecap());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add("Catch up from today's log", null, (_, _) => CatchUpToday());
        menu.Items.Add("Check for updates…", null, (_, _) => _ = CheckForUpdates(manual: true));
        menu.Items.Add("Open config folder", null, (_, _) => OpenConfigFolder());
        menu.Items.Add("Reset position", null, (_, _) => ResetPosition());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Close());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleHide();
    }

    /// <summary>The Panels submenu with live checkmarks — built fresh per menu
    /// (a WinForms item can only live in one strip at a time).</summary>
    private System.Windows.Forms.ToolStripMenuItem BuildPanelsMenu()
    {
        var panelsItem = new System.Windows.Forms.ToolStripMenuItem("Panels");
        var panelTimer = new System.Windows.Forms.ToolStripMenuItem("Repop timer", null, (_, _) => ToggleTimer());
        var panelMeter = new System.Windows.Forms.ToolStripMenuItem("DPS meter", null, (_, _) => ToggleMeter());
        var panelSkills = new System.Windows.Forms.ToolStripMenuItem("DPS meter · skills section", null, (_, _) => ToggleSkills());
        var panelProcs = new System.Windows.Forms.ToolStripMenuItem("DPS meter · proc watcher", null, (_, _) => ToggleProcs());
        var panelSct = new System.Windows.Forms.ToolStripMenuItem("Combat text", null, (_, _) => ToggleSct());
        var panelFlash = new System.Windows.Forms.ToolStripMenuItem("Flash alerts", null, (_, _) => ToggleFlash());
        var panelToolbar = new System.Windows.Forms.ToolStripMenuItem("Toolbar", null, (_, _) => ToggleToolbar());
        var panelBars = new System.Windows.Forms.ToolStripMenuItem("Buff bars", null, (_, _) => ToggleBars());
        var panelDots = new System.Windows.Forms.ToolStripMenuItem("Enemy DoTs", null, (_, _) => ToggleEnemyDots());
        var panelConds = new System.Windows.Forms.ToolStripMenuItem("Condition badges (stun/fear)", null, (_, _) => ToggleConditions());
        var panelSession = new System.Windows.Forms.ToolStripMenuItem("Session stats (XP/AA/motes)", null, (_, _) => ToggleSessionStats());
        var panelSelfM = new System.Windows.Forms.ToolStripMenuItem("Self-buffs matrix", null, (_, _) => ToggleSelfMatrix());
        var panelTargetM = new System.Windows.Forms.ToolStripMenuItem("Target-debuffs matrix", null, (_, _) => ToggleTargetMatrix());
        var panelRemind = new System.Windows.Forms.ToolStripMenuItem("Rebuff reminders", null, (_, _) => ToggleReminders());
        panelsItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[]
            { panelToolbar, panelBars, panelSelfM, panelTargetM, panelRemind, panelDots,
              panelConds, panelSession, panelTimer, panelMeter, panelSkills, panelProcs, panelSct, panelFlash });
        panelsItem.DropDownOpening += (_, _) =>
        {
            panelToolbar.Checked = !_toolbarHidden;
            panelBars.Checked = !_barsHidden;
            panelSelfM.Checked = _config.Overlay.SelfMatrixVisible;
            panelTargetM.Checked = _config.Overlay.TargetMatrixVisible;
            panelRemind.Checked = _config.Overlay.RemindersVisible;
            panelDots.Checked = _config.Overlay.EnemyDotsVisible;
            panelConds.Checked = _config.Overlay.ConditionsVisible;
            panelSession.Checked = _config.Overlay.SessionStatsVisible;
            panelTimer.Checked = !_timerHidden;
            panelMeter.Checked = !_meterHidden;
            panelSkills.Checked = !_skillsHidden;
            panelProcs.Checked = _config.Overlay.ProcWatcherVisible;
            panelSct.Checked = !_sctHidden;
            panelFlash.Checked = !_flashHidden;
        };
        return panelsItem;
    }

    /// <summary>The Loadout submenu, rebuilt with checkmarks on open.</summary>
    private System.Windows.Forms.ToolStripMenuItem BuildLoadoutMenu()
    {
        var loadoutItem = new System.Windows.Forms.ToolStripMenuItem("Loadout");
        // Seed a dummy so the submenu ARROW renders before first open —
        // WinForms only draws it when DropDownItems is non-empty, and the
        // real list is built lazily on DropDownOpening.
        loadoutItem.DropDownItems.Add("…");
        loadoutItem.DropDownOpening += (_, _) =>
        {
            loadoutItem.DropDownItems.Clear();
            foreach (var name in _configService.ListLoadouts().Select(l => l.Name))
            {
                string captured = name;
                var mi = new System.Windows.Forms.ToolStripMenuItem(name)
                {
                    Checked = string.Equals(name, _config.ActiveLoadout, StringComparison.OrdinalIgnoreCase),
                };
                mi.Click += (_, _) => ApplyLoadout(captured);
                loadoutItem.DropDownItems.Add(mi);
            }
            loadoutItem.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            loadoutItem.DropDownItems.Add("Manage loadouts…", null, (_, _) => OpenManager("Loadouts"));
        };
        return loadoutItem;
    }

    /// <summary>The app's embedded exe icon (falls back to a drawn one).</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through to the drawn icon */ }
        return CreateTrayIcon();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(24, 30, 42));
            using var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(79, 195, 247));
            g.FillRectangle(blue, 2, 4, 12, 3);
            using var green = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(129, 199, 132));
            g.FillRectangle(green, 2, 9, 8, 3);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    // ---- persistence / teardown ---------------------------------------------


    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (!SuppressStatePersistence)
            {
                _config.Overlay.Left = Left;
                _config.Overlay.Top = Top;
                _configService.SaveWindowState(_config.Overlay);
                SaveLastSeen();
            }
        }
        catch { /* ignore */ }

        Log.Info("Shutting down");
        _spellLib?.SaveSeenIfDirty(); // null if the window closes before OnLoaded ran
        UnregisterHotKeys();
        _watcher?.Dispose();
        try { _selfMatrix?.Close(); } catch { /* ignore */ }
        try { _targetMatrix?.Close(); } catch { /* ignore */ }
        try { _enemyDotsWin?.Close(); } catch { /* ignore */ }
        try { _conditionsWin?.Close(); } catch { /* ignore */ }
        try { _skyHelperWin?.Close(); } catch { /* ignore */ }
        try { _sessionWin?.Close(); } catch { /* ignore */ }
        try { _remindersWin?.Close(); } catch { /* ignore */ }
        try { _timer?.Close(); } catch { /* ignore */ }
        try { _meter?.Close(); } catch { /* ignore */ }
        try { _flash?.Close(); } catch { /* ignore */ }
        try { _toolbarWin?.Close(); } catch { /* ignore */ }
        try { _raidsWindow?.Close(); } catch { /* ignore */ }
        try { _inventoryWindow?.Close(); } catch { /* ignore */ }
        foreach (var lane in _sctLanes.Values) { try { lane.Close(); } catch { /* ignore */ } }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }
}
