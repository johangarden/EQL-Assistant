using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Models;
using EQLOverlay.ViewModels;

namespace EQLOverlay.Services;

/// <summary>
/// Turns log lines into live countdown bars, fires alerts, and nags about
/// missing buffs. Must be used from the UI thread (owns the bound collection
/// and a DispatcherTimer that refreshes the bars).
/// </summary>
public sealed class TriggerEngine
{
    private readonly AlertService _alerts;
    private readonly DispatcherTimer _tick;

    private readonly Dictionary<string, TimerBarViewModel> _active = new();
    private readonly Dictionary<string, TimerBarViewModel> _missing = new(); // keyed by trigger id
    private readonly Dictionary<string, DateTime> _lastRemind = new();
    private readonly Dictionary<string, int> _remindCount = new();
    private readonly HashSet<string> _seen = new();

    private List<TriggerDefinition> _triggers;
    private double _warnSeconds;
    private double _remindInterval;
    private int _missingCheckAccum;

    /// <summary>Bound directly by the overlay's ItemsControl.</summary>
    public ObservableCollection<TimerBarViewModel> Bars { get; } = new();

    /// <summary>Missing-buff REBUFF indicators — their own panel since 2.10
    /// (they used to sit inside the bars panel).</summary>
    public ObservableCollection<TimerBarViewModel> Reminders { get; } = new();

    /// <summary>Persistent present/missing cells for the Self-Buffs matrix panel.</summary>
    public ObservableCollection<MatrixCellViewModel> SelfCells { get; } = new();
    private readonly Dictionary<string, MatrixCellViewModel> _selfById = new();

    /// <summary>Persistent present/missing cells for the Target-Debuffs matrix panel.</summary>
    public ObservableCollection<MatrixCellViewModel> TargetCells { get; } = new();
    private readonly Dictionary<string, MatrixCellViewModel> _targetById = new();

    /// <summary>Raised when a "timerAuto" trigger matches: (durationSeconds, triggerName).</summary>
    public event Action<double, string>? TimerRequested;

    /// <summary>Raised when a trigger with flash text matches: (text, colorString).</summary>
    public event Action<string, string>? FlashRequested;

    /// <summary>Raised when a cooldown reducer cuts a running bar: (triggerName, seconds).</summary>
    public event Action<string, double>? BarReduced;

    /// <summary>Optional observed-duration lookup (SpellDurations): returns the
    /// learned recent-window max for a trigger name, or null.</summary>
    public Func<string, double?>? LearnedDuration { get; set; }

    private const double CastAnchorWindowSec = 15;   // begin-cast -> landing (same as SpellDurations)
    private const double QuickBuffWindowSec = 8;     // activation -> burst (observed: 3s)
    private (string Key, DateTime At)? _lastOwnCast; // rank-stripped, from "You begin casting X."
    private (string Key, DateTime At)? _lastPetCast; // the pet's own "Lonaner begins casting X."
    private bool _petDown; // slain, and no new summon has shown itself yet
    private DateTime _quickBuffAt = DateTime.MinValue;
    private readonly HashSet<string> _everCast = new(StringComparer.Ordinal); // session, rank-stripped

    /// <summary>Is this name YOUR pet (current or any past summon)? Wired to
    /// the combat parser's known-pets ledger — the gate that keeps a
    /// groupmate's pet (or a mob) from starting a pet-buff bar.</summary>
    public Func<string, bool>? IsPetName { get; set; }

    private static readonly Regex BeginCastRx = new(
        @"^You begin (?:casting|singing) (?<s>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A single capitalized word casting — the shape of pet (and
    /// player) names; "a froglok shaman begins casting" stays lowercase and
    /// multi-word named mobs don't fit one word.</summary>
    private static readonly Regex PetCastRx = new(
        @"^(?<who>[A-Z][A-Za-z`]*) begins casting (?<s>.+?)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SlainRx = new(
        @"^(?:You have slain (?<n1>.+?)!|(?<n2>.+?) has been slain)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The cast-anchor gate (the Companion's ruling): an anchored
    /// trigger's start pattern only counts when it follows YOUR own
    /// "You begin casting &lt;Name&gt;." within the window — an unanchored
    /// ambiguous landing draws nothing, because a bar that guesses which of
    /// four hastes just landed would lie about the duration. Auto (null)
    /// anchors EVERY library trigger — EQL is solo-first, so by default a
    /// groupmate's buff landing on you starts nothing; untick per trigger to
    /// opt into group play. Manual triggers stay unanchored on auto (their
    /// names often aren't castable spell names, so an anchor would silently
    /// never arrive).</summary>
    private bool AnchorAllows(TriggerDefinition trigger, DateTime eventTime)
    {
        bool anchored = trigger.CastAnchored
            ?? trigger.Id.StartsWith("lib-", StringComparison.Ordinal);
        if (!anchored) return true;

        string key = SpellDurations.BaseKey(trigger.Name);
        if (_lastOwnCast is { } c && c.Key == key
            && (eventTime - c.At).TotalSeconds is >= 0 and <= CastAnchorWindowSec)
            return true;

        // Pet triggers accept the PET's own begin-cast as an anchor too — its
        // self-buffs (Shadow Vortex) land without any cast of yours.
        if (trigger.OnPet)
            return _lastPetCast is { } p && p.Key == key
                && (eventTime - p.At).TotalSeconds is >= 0 and <= CastAnchorWindowSec;

        // Quick Buff burst (the Companion's case 3): the AA lands the whole
        // spellbar at once with NO cast lines, so the named anchor never
        // arrives. During the activation window an anchored landing is
        // admitted when the spell is plausibly YOURS: cast at some point this
        // session, its bar/cell already running (a rebuff refresh), or known
        // to the duration learner (samples only mint from your own casts).
        if ((eventTime - _quickBuffAt).TotalSeconds is >= 0 and <= QuickBuffWindowSec)
        {
            if (_everCast.Contains(key)) return true;
            if (_active.Keys.Any(k => k == trigger.Id
                    || k.StartsWith(trigger.Id + "|", StringComparison.Ordinal))) return true;
            if (_selfById.TryGetValue(trigger.Id, out var sc) && sc.IsActive) return true;
            if (_targetById.TryGetValue(trigger.Id, out var tc) && tc.IsActive) return true;
            if (LearnedDuration?.Invoke(trigger.Name) is not null) return true;
        }
        return false;
    }

    /// <summary>Auto-learn triggers run on the learned estimate once samples
    /// exist (the configured value is only the starting point — the estimate
    /// may correct in EITHER direction, e.g. level-scaled durations shorter
    /// than the library's max-level number). Manual triggers enforce the
    /// configured duration exactly.</summary>
    /// <summary>The trigger's two alert payloads, each already narrowed to its
    /// chosen channel (phrase OR sound — never both). Disabled notices come
    /// back empty so downstream code needs no further gating.</summary>
    private static (double WarnAt, string? WarnSpeak, string? WarnSound,
                    bool OnFaded, string? FadedSpeak, string? FadedSound)
        AlertOf(TriggerDefinition t)
    {
        if (t.Alert is not { } a) return (0, null, null, false, null, null);
        bool warnOn = a.WarnEnabled == true;
        bool fadedOn = a.FadedEnabled == true;
        bool warnSpeaks = a.WarnMode != AlertConfig.ModeSound;
        bool fadedSpeaks = a.FadedMode != AlertConfig.ModeSound;
        return (
            warnOn ? a.AtSeconds : 0,
            warnOn && warnSpeaks ? a.Speak : null,
            warnOn && !warnSpeaks ? a.Sound : null,
            fadedOn,
            fadedOn && fadedSpeaks ? a.FadedSpeak : null,
            fadedOn && !fadedSpeaks ? a.FadedSound : null);
    }

    private double EffectiveDuration(TriggerDefinition trigger)
    {
        if (!trigger.DurationAuto) return trigger.DurationSeconds;
        return LearnedDuration?.Invoke(trigger.Name) is double learned && learned > 1
            ? learned
            : trigger.DurationSeconds;
    }

    private static readonly Regex TimestampPrefix =
        new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    public TriggerEngine(AppConfig config, AlertService alerts)
    {
        _alerts = alerts;
        _triggers = config.Triggers;
        _warnSeconds = config.Overlay.WarnSeconds;
        _remindInterval = config.Overlay.RemindIntervalSeconds;

        _tick = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(66) // ~15 fps
        };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        BuildMatrices();
    }

    public void UpdateConfig(AppConfig config)
    {
        _triggers = config.Triggers;
        _warnSeconds = config.Overlay.WarnSeconds;
        _remindInterval = config.Overlay.RemindIntervalSeconds;

        // Carry running matrix timers across the rebuild — saving in the
        // Manager must not blank an active buff (same idea as the repop fix).
        var running = new Dictionary<string, (DateTime End, bool FadeFired)>();
        foreach (var cell in SelfCells.Concat(TargetCells))
            if (cell.IsActive) running[cell.Key] = (cell.EndTimeLocal, cell.FadeAlertFired);

        BuildMatrices();

        foreach (var cell in SelfCells.Concat(TargetCells))
        {
            if (!running.TryGetValue(cell.Key, out var state)) continue;
            cell.Activate(state.End);
            cell.FadeAlertFired = state.FadeFired;
        }

        // Drop running bars whose trigger was deleted, disabled, or moved off
        // the bars panel; everything else keeps counting down untouched.
        var barIds = _triggers
            .Where(t => t.Enabled && t.Panel == Panels.Bars)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in _active.Keys.ToList())
        {
            if (key.StartsWith("__demo", StringComparison.Ordinal)) continue;
            int pipe = key.IndexOf('|');
            string id = pipe < 0 ? key : key[..pipe];
            if (!barIds.Contains(id)) Remove(key);
        }

        // Drop missing indicators whose trigger no longer wants them.
        foreach (var id in _missing.Keys.ToList())
        {
            var t = _triggers.FirstOrDefault(x => x.Id == id);
            if (t is null || !t.Enabled || !t.RemindWhenMissing)
            {
                if (_missing.Remove(id, out var mb)) Reminders.Remove(mb);
                _lastRemind.Remove(id);
                _remindCount.Remove(id);
            }
        }
    }

    /// <summary>Death strips buffs — clear every non-cooldown bar (cooldowns
    /// keep ticking through death) and blank both matrices. Also what makes
    /// the generous overrun cap honest: the usual eater of a fade line is
    /// dying before it prints.</summary>
    private void StripOnDeath()
    {
        var petIds = _triggers.Where(t => t.OnPet).Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (key, bar) in _active.ToList())
        {
            if (bar.Category.Contains("cool", StringComparison.OrdinalIgnoreCase)) continue;
            // YOUR death doesn't strip the PET's buffs — it outlives you.
            int pipe = key.IndexOf('|');
            if (petIds.Contains(pipe < 0 ? key : key[..pipe])) continue;
            _active.Remove(key);
            Bars.Remove(bar);
        }
        foreach (var cell in SelfCells) cell.Deactivate();
        foreach (var cell in TargetCells) cell.Deactivate();
    }

    /// <summary>Clear all bars and tracking state (used when switching loadouts).</summary>
    public void Reset()
    {
        _active.Clear();
        _missing.Clear();
        _lastRemind.Clear();
        _remindCount.Clear();
        _seen.Clear();
        Bars.Clear();
        Reminders.Clear();
        foreach (var c in SelfCells) c.Deactivate();
        foreach (var c in TargetCells) c.Deactivate();
    }

    /// <summary>(Re)build the matrix cells from the current trigger set (all start missing).</summary>
    private void BuildMatrices()
    {
        SelfCells.Clear(); _selfById.Clear();
        TargetCells.Clear(); _targetById.Clear();

        foreach (var t in _triggers)
        {
            if (!t.Enabled) continue;
            var (cells, byId) = t.Panel switch
            {
                Panels.SelfBuffs => (SelfCells, _selfById),
                Panels.TargetDebuffs => (TargetCells, _targetById),
                _ => (null, null),
            };
            if (cells is null || byId is null) continue;

            var al = AlertOf(t);
            var cell = new MatrixCellViewModel(t.Id, t.Name, t.DurationSeconds,
                al.WarnAt, al.OnFaded, al.WarnSpeak, al.WarnSound,
                al.FadedSpeak, al.FadedSound);
            byId[t.Id] = cell;
            cells.Add(cell);
        }
    }

    public void ProcessLine(string rawLine)
    {
        DateTime eventTime = ExtractTimestamp(rawLine, out string body);

        var cast = BeginCastRx.Match(body);
        if (cast.Success)
        {
            string castKey = SpellDurations.BaseKey(cast.Groups["s"].Value);
            _lastOwnCast = (castKey, eventTime);
            _everCast.Add(castKey);
        }
        else if (body.StartsWith("You activate Quick Buff.", StringComparison.Ordinal))
        {
            // ("Caladar activates Quick Buff." is someone else — no window.)
            _quickBuffAt = eventTime;
        }
        else if (body.StartsWith("You died.", StringComparison.Ordinal)
                 || body.StartsWith("You have been slain", StringComparison.Ordinal))
        {
            StripOnDeath();
        }
        else if (IsPetName is not null && body.Contains(" begins casting ", StringComparison.Ordinal))
        {
            var pc = PetCastRx.Match(body);
            if (pc.Success && IsPetName(pc.Groups["who"].Value))
            {
                _lastPetCast = (SpellDurations.BaseKey(pc.Groups["s"].Value), eventTime);
                _petDown = false; // a casting pet is a living pet
            }
        }
        else if (IsPetName is not null && body.Contains("slain", StringComparison.Ordinal))
        {
            var sm = SlainRx.Match(body);
            string slain = sm.Success
                ? (sm.Groups["n1"].Success ? sm.Groups["n1"].Value : sm.Groups["n2"].Value).Trim()
                : "";
            if (slain.Length > 0 && IsPetName(slain)) StripPetBars(slain);
        }

        foreach (var trigger in _triggers)
        {
            if (!trigger.Enabled) continue;

            // Flash-only panel: screen-center text on match, nothing else.
            if (trigger.Panel == Panels.Flash)
            {
                if (trigger.StartRegex is { } fr && fr.IsMatch(body))
                {
                    string text = string.IsNullOrWhiteSpace(trigger.Alert?.FlashText)
                        ? trigger.Name : trigger.Alert!.FlashText!;
                    FlashRequested?.Invoke(text, TriggerColors.For(trigger));
                }
                continue;
            }

            // Any other trigger may also carry an optional flash on its start match.
            if (!string.IsNullOrWhiteSpace(trigger.Alert?.FlashText)
                && trigger.StartRegex is { } sr && sr.IsMatch(body))
                FlashRequested?.Invoke(trigger.Alert!.FlashText!, TriggerColors.For(trigger));

            if (trigger.Panel == Panels.SelfBuffs)
            {
                ProcessMatrixLine(_selfById, trigger, body, eventTime);
                continue;
            }
            if (trigger.Panel == Panels.TargetDebuffs)
            {
                ProcessMatrixLine(_targetById, trigger, body, eventTime);
                continue;
            }

            if (trigger.Panel == Panels.TimerAuto)
            {
                if (trigger.StartRegex is { } tr && tr.IsMatch(body))
                    TimerRequested?.Invoke(trigger.DurationSeconds, trigger.Name);
                continue;
            }

            // Anything else that isn't "bars" is skipped rather than falling
            // through to the bars logic.
            if (trigger.Panel != Panels.Bars) continue;

            // Default: countdown bars (Area 1).
            if (trigger.EndRegex is { } endRx)
            {
                var m = endRx.Match(body);
                if (m.Success)
                {
                    // A pet trigger's wear-off ("Your pet's X spell has worn
                    // off.") names the owner but not WHICH pet — the OLDEST
                    // bar closes (the enemy-DoT law).
                    if (trigger.OnPet) RemoveOldestFor(trigger.Id);
                    else Remove(BuildKey(trigger, m));
                }
            }

            if (trigger.StartRegex is { } startRx)
            {
                var m = startRx.Match(body);
                if (m.Success && AnchorAllows(trigger, eventTime)
                    && (!trigger.OnPet || IsPetTarget(m)))
                    StartOrRefresh(trigger, m, eventTime);
            }

            // Cooldown reducer: each match cuts time off this trigger's RUNNING
            // bars (SK: Reave landing shaves 60s off the Harm Touch cooldown).
            if (trigger.ReduceRegex is { } reduceRx && trigger.ReduceSeconds > 0
                && reduceRx.IsMatch(body))
            {
                bool reduced = false;
                foreach (var (key, bar) in _active)
                {
                    if (key != trigger.Id
                        && !key.StartsWith(trigger.Id + "|", StringComparison.Ordinal))
                        continue;
                    bar.ReduceBy(trigger.ReduceSeconds);
                    bar.Refresh(eventTime, _warnSeconds);
                    reduced = true;
                }
                if (reduced) BarReduced?.Invoke(trigger.Name, trigger.ReduceSeconds);
            }
        }
    }

    private void ProcessMatrixLine(Dictionary<string, MatrixCellViewModel> byId,
        TriggerDefinition trigger, string body, DateTime eventTime)
    {
        if (!byId.TryGetValue(trigger.Id, out var cell)) return;

        if (trigger.EndRegex is { } endRx && endRx.IsMatch(body))
            cell.Deactivate();

        if (trigger.StartRegex is { } startRx && startRx.IsMatch(body)
            && AnchorAllows(trigger, eventTime))
            cell.Activate(trigger.Permanent
                ? DateTime.MaxValue
                : eventTime.AddSeconds(EffectiveDuration(trigger)));
    }

    private void StartOrRefresh(TriggerDefinition trigger, Match match, DateTime eventTime)
    {
        _seen.Add(trigger.Id);
        if (trigger.OnPet) _petDown = false; // a buff landing on it = a pet to buff

        string key = BuildKey(trigger, match);
        double duration = EffectiveDuration(trigger);
        DateTime end = trigger.Permanent ? DateTime.MaxValue : eventTime.AddSeconds(duration);

        if (_active.TryGetValue(key, out var existing))
        {
            if (trigger.RefreshOnRetrigger)
            {
                existing.Restart(duration, end);
                Reposition(existing);
            }
            return;
        }

        var al = AlertOf(trigger);
        var vm = TimerBarViewModel.CreateTimer(
            key, BuildLabel(trigger, match), trigger.Category,
            duration, end, MakeBrush(TriggerColors.For(trigger)),
            al.WarnAt, al.OnFaded, al.WarnSpeak, al.WarnSound,
            al.FadedSpeak, al.FadedSound,
            waitsForFade: trigger.EndRegex is not null,
            learnsDuration: trigger.DurationAuto && !trigger.Permanent,
            permanent: trigger.Permanent);

        _active[key] = vm;
        InsertSorted(vm);
    }

    private void Remove(string key)
    {
        if (_active.Remove(key, out var vm))
            Bars.Remove(vm);
    }

    /// <summary>The landing's named target must be YOUR pet — a groupmate's
    /// pet printing the same sentence starts nothing. A pattern without a
    /// target group (hand-made pet trigger) passes through.</summary>
    private bool IsPetTarget(Match match)
    {
        var target = match.Groups["target"];
        if (!target.Success || target.Value.Length == 0) return true;
        return IsPetName?.Invoke(target.Value.Trim()) ?? false;
    }

    /// <summary>Close the trigger's oldest running bar (earliest end time) —
    /// how a wear-off that doesn't name WHICH pet picks among instances.</summary>
    private void RemoveOldestFor(string triggerId)
    {
        string? oldest = null;
        DateTime oldestEnd = DateTime.MaxValue;
        foreach (var (key, bar) in _active)
        {
            if (key != triggerId
                && !key.StartsWith(triggerId + "|", StringComparison.Ordinal)) continue;
            if (bar.EndTimeLocal < oldestEnd) { oldestEnd = bar.EndTimeLocal; oldest = key; }
        }
        if (oldest is not null) Remove(oldest);
    }

    /// <summary>Pet died — its buffs die with it. Bars keyed to that pet's
    /// name go; keyless pet bars (hand-made trigger without a target group)
    /// go too, since they can only mean the pet you had.</summary>
    private void StripPetBars(string pet)
    {
        _petDown = true; // nothing to rebuff until the next summon speaks up
        var petIds = _triggers.Where(t => t.OnPet).Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (petIds.Count == 0) return;
        foreach (var key in _active.Keys.ToList())
        {
            int pipe = key.IndexOf('|');
            string id = pipe < 0 ? key : key[..pipe];
            if (!petIds.Contains(id)) continue;
            string target = pipe < 0 ? "" : key[(pipe + 1)..];
            if (target.Length == 0
                || target.Equals(pet, StringComparison.OrdinalIgnoreCase))
                Remove(key);
        }
    }

    public void AddDemoTimer()
    {
        int n = _active.Count(k => k.Key.StartsWith("__demo", StringComparison.Ordinal)) + 1;
        string[] cats = { "Buffs", "HoTs", "DoTs" };
        string[] colors = { "#4FC3F7", "#81C784", "#E57373" };
        int idx = (n - 1) % cats.Length;
        double dur = 10 + n * 4;

        string key = "__demo" + n;
        var vm = TimerBarViewModel.CreateTimer(key, $"Demo {cats[idx]} {n}", cats[idx],
            dur, DateTime.Now.AddSeconds(dur), MakeBrush(colors[idx]),
            alertAtSeconds: 0, alertOnExpire: false, alertSpeak: null, alertSound: null);
        _active[key] = vm;
        InsertSorted(vm);
    }

    private void Tick()
    {
        var now = DateTime.Now;
        UpdateBars(now);
        UpdateMatrix(SelfCells, now);
        UpdateMatrix(TargetCells, now);
    }

    /// <summary>Test hook: a REBUFF row for ~15s (panel placement without
    /// waiting for a real buff to drop).</summary>
    public void AddDemoReminder()
    {
        var mb = TimerBarViewModel.CreateMissing(
            "missing|__demo" + DateTime.Now.Ticks, "Demo Rebuff", "Buffs", MakeBrush("#E53935"));
        Reminders.Add(mb);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        t.Tick += (_, _) => { t.Stop(); Reminders.Remove(mb); };
        t.Start();
    }

    /// <summary>Test hook: drop a demo cell into the Self matrix (present ~15s, then missing).</summary>
    public void AddDemoMatrixCell() => AddDemoCell(SelfCells, _selfById, "Buff");

    /// <summary>Test hook: drop a demo cell into the Target-Debuffs matrix.</summary>
    public void AddDemoTargetCell() => AddDemoCell(TargetCells, _targetById, "Debuff");

    private static void AddDemoCell(ObservableCollection<MatrixCellViewModel> cells,
        Dictionary<string, MatrixCellViewModel> byId, string label)
    {
        int n = byId.Keys.Count(k => k.StartsWith("__demoCell", StringComparison.Ordinal)) + 1;
        string key = "__demoCell" + n;
        var cell = new MatrixCellViewModel(key, $"Demo {label} {n}", 15, 0, false, null, null);
        cell.Activate(DateTime.Now.AddSeconds(15));
        byId[key] = cell;
        cells.Add(cell);
    }

    private void UpdateMatrix(ObservableCollection<MatrixCellViewModel> cells, DateTime now)
    {
        foreach (var cell in cells)
        {
            if (!cell.IsActive) continue;

            bool expired = cell.Refresh(now, _warnSeconds);
            if (expired)
            {
                if (cell.AlertOnExpire) _alerts.Fire(cell.AlertFadedSpeak, cell.AlertFadedSound);
            }
            else if (cell.AlertAtSeconds > 0 && !cell.FadeAlertFired &&
                     cell.RemainingSeconds > 0 && cell.RemainingSeconds <= cell.AlertAtSeconds)
            {
                cell.FadeAlertFired = true;
                _alerts.Fire(cell.AlertSpeak, cell.AlertSound);
            }
        }
    }

    /// <summary>Floor of the unwitnessed-overrun cull. The real cap scales with
    /// the bar: an overrunning bar may squat up to max(60s, its own estimated
    /// duration) past 0 — a learning trigger whose library starting value runs
    /// short by minutes must not vanish while the buff is demonstrably still
    /// up. Bounded all the same, because a fade line that never arrives (crash,
    /// missed log) must not leave a gray bar squatting forever; the own-death
    /// censor below covers the common eater of fade lines.</summary>
    public const double OverrunCapSec = 60;

    public static double OverrunCapFor(TimerBarViewModel bar) =>
        Math.Max(OverrunCapSec, bar.TotalSeconds);

    private void UpdateBars(DateTime now)
    {
        List<TimerBarViewModel>? expired = null;
        foreach (var bar in Bars)
        {
            bar.Refresh(now, _warnSeconds);

            if (bar.IsOverrun)
            {
                if (bar.OverrunSeconds > OverrunCapFor(bar)) (expired ??= new()).Add(bar);
                continue; // no fade warnings while gray
            }

            if (!bar.IsMissing && bar.AlertAtSeconds > 0 && !bar.FadeAlertFired &&
                bar.RemainingSeconds > 0 && bar.RemainingSeconds <= bar.AlertAtSeconds)
            {
                bar.FadeAlertFired = true;
                _alerts.Fire(bar.AlertSpeak, bar.AlertSound);
            }

            if (bar.IsExpired)
            {
                // A bar with an end pattern doesn't vanish on a mere estimate:
                // it grays out and counts UP until the real fade line (which
                // also teaches the learner the true duration).
                if (bar.WaitsForFade)
                {
                    if (bar.AlertOnExpire) _alerts.Fire(bar.AlertFadedSpeak, bar.AlertFadedSound);
                    bar.EnterOverrun();
                }
                else
                {
                    (expired ??= new()).Add(bar);
                }
            }
        }

        if (expired is not null)
        {
            foreach (var bar in expired)
            {
                if (bar.AlertOnExpire && !bar.IsOverrun) // overrun already alerted at 0
                    _alerts.Fire(bar.AlertFadedSpeak, bar.AlertFadedSound);
                _active.Remove(bar.Key);
                Bars.Remove(bar);
            }
        }

        // Missing-buff scan ~ once per second (15 ticks).
        if (++_missingCheckAccum >= 15)
        {
            _missingCheckAccum = 0;
            CheckMissing(now);
        }
    }

    /// <summary>Test hook: spoken-reminder count for one trigger this outage.</summary>
    internal int RemindCountFor(string triggerId) => _remindCount.GetValueOrDefault(triggerId);

    internal void CheckMissing(DateTime now)
    {
        foreach (var t in _triggers)
        {
            if (!t.Enabled || !t.RemindWhenMissing || !_seen.Contains(t.Id))
                continue;

            bool active = _active.Keys.Any(k =>
                k == t.Id || k.StartsWith(t.Id + "|", StringComparison.Ordinal));

            // A dead pet has nothing to rebuff — pet reminders wait for the
            // next summon to show itself (a cast or a landing on it).
            if (active || (t.OnPet && _petDown))
            {
                if (_missing.Remove(t.Id, out var mb)) Reminders.Remove(mb);
                _lastRemind.Remove(t.Id);
                _remindCount.Remove(t.Id); // rebuffed: the backoff resets
                continue;
            }

            // The pet's reminders say so — its row and its voice must never
            // read as YOUR buff missing.
            string label = t.OnPet ? "Pet · " + t.Name : t.Name;
            string phrase = t.OnPet ? $"Your pet's {t.Name} missing" : $"{t.Name} missing";

            // Ignored nagging earns quieter nagging (owner ruling): after 5
            // spoken warnings the interval DOUBLES, and snaps back to the
            // configured value the moment the buff is reapplied. The red bar
            // stays regardless — only the voice backs off.
            double interval = _remindInterval
                * (_remindCount.GetValueOrDefault(t.Id) >= 5 ? 2 : 1);

            if (!_missing.ContainsKey(t.Id))
            {
                var mb = TimerBarViewModel.CreateMissing(
                    "missing|" + t.Id, label, t.Category, MakeBrush("#E53935"));
                _missing[t.Id] = mb;
                Reminders.Add(mb);
                _alerts.Speak(phrase);
                _lastRemind[t.Id] = now;
                _remindCount[t.Id] = 1;
            }
            else if (!_lastRemind.TryGetValue(t.Id, out var last) ||
                     (now - last).TotalSeconds >= interval)
            {
                _alerts.Speak(phrase);
                _lastRemind[t.Id] = now;
                _remindCount[t.Id] = _remindCount.GetValueOrDefault(t.Id) + 1;
            }
        }
    }

    // ---- ordering -----------------------------------------------------------

    private void InsertSorted(TimerBarViewModel vm)
    {
        for (int i = 0; i < Bars.Count; i++)
        {
            if (!string.Equals(Bars[i].Category, vm.Category, StringComparison.OrdinalIgnoreCase))
                continue;

            for (; i < Bars.Count &&
                   string.Equals(Bars[i].Category, vm.Category, StringComparison.OrdinalIgnoreCase); i++)
            {
                if (vm.EndTimeLocal < Bars[i].EndTimeLocal)
                {
                    Bars.Insert(i, vm);
                    return;
                }
            }
            Bars.Insert(i, vm);
            return;
        }
        Bars.Add(vm);
    }

    private void Reposition(TimerBarViewModel vm)
    {
        if (Bars.Remove(vm))
            InsertSorted(vm);
    }

    // ---- helpers ------------------------------------------------------------

    private static string BuildKey(TriggerDefinition trigger, Match match)
    {
        var target = match.Groups["target"];
        return target.Success && target.Value.Length > 0
            ? trigger.Id + "|" + target.Value
            : trigger.Id;
    }

    private static string BuildLabel(TriggerDefinition trigger, Match match)
    {
        var target = match.Groups["target"];
        return target.Success && target.Value.Length > 0
            ? $"{trigger.Name} — {target.Value}"
            : trigger.Name;
    }

    private static DateTime ExtractTimestamp(string line, out string body)
    {
        var m = TimestampPrefix.Match(line);
        if (m.Success)
        {
            body = line.Substring(m.Length);
            string ts = Regex.Replace(m.Groups["ts"].Value.Trim(), @"\s+", " ");
            if (DateTime.TryParseExact(ts, TimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;
        }
        else
        {
            body = line;
        }
        return DateTime.Now;
    }

    private static SolidColorBrush MakeBrush(string color)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(color);
            return new SolidColorBrush(c);
        }
        catch
        {
            return new SolidColorBrush(Colors.DodgerBlue);
        }
    }
}
