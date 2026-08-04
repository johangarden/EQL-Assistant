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
    private readonly HashSet<string> _seen = new();

    private List<TriggerDefinition> _triggers;
    private double _warnSeconds;
    private double _remindInterval;
    private int _missingCheckAccum;

    /// <summary>Bound directly by the overlay's ItemsControl.</summary>
    public ObservableCollection<TimerBarViewModel> Bars { get; } = new();

    /// <summary>Persistent present/missing cells for the Self-Buffs matrix panel.</summary>
    public ObservableCollection<MatrixCellViewModel> SelfCells { get; } = new();
    private readonly Dictionary<string, MatrixCellViewModel> _selfById = new();

    /// <summary>Persistent present/missing cells for the Target-Debuffs matrix panel.</summary>
    public ObservableCollection<MatrixCellViewModel> TargetCells { get; } = new();
    private readonly Dictionary<string, MatrixCellViewModel> _targetById = new();

    /// <summary>Raised when a "timerAuto" trigger matches: (durationSeconds, triggerName).</summary>
    public event Action<double, string>? TimerRequested;

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
        BuildMatrices();

        // Drop missing indicators whose trigger no longer wants them.
        foreach (var id in _missing.Keys.ToList())
        {
            var t = _triggers.FirstOrDefault(x => x.Id == id);
            if (t is null || !t.Enabled || !t.RemindWhenMissing)
            {
                if (_missing.Remove(id, out var mb)) Bars.Remove(mb);
                _lastRemind.Remove(id);
            }
        }
    }

    /// <summary>Clear all bars and tracking state (used when switching loadouts).</summary>
    public void Reset()
    {
        _active.Clear();
        _missing.Clear();
        _lastRemind.Clear();
        _seen.Clear();
        Bars.Clear();
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

            var cell = new MatrixCellViewModel(t.Id, t.Name, t.DurationSeconds,
                t.Alert?.AtSeconds ?? 0, t.Alert?.OnExpire ?? false, t.Alert?.Speak, t.Alert?.Sound);
            byId[t.Id] = cell;
            cells.Add(cell);
        }
    }

    public void ProcessLine(string rawLine)
    {
        DateTime eventTime = ExtractTimestamp(rawLine, out string body);

        foreach (var trigger in _triggers)
        {
            if (!trigger.Enabled) continue;

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
                if (m.Success) Remove(BuildKey(trigger, m));
            }

            if (trigger.StartRegex is { } startRx)
            {
                var m = startRx.Match(body);
                if (m.Success) StartOrRefresh(trigger, m, eventTime);
            }
        }
    }

    private static void ProcessMatrixLine(Dictionary<string, MatrixCellViewModel> byId,
        TriggerDefinition trigger, string body, DateTime eventTime)
    {
        if (!byId.TryGetValue(trigger.Id, out var cell)) return;

        if (trigger.EndRegex is { } endRx && endRx.IsMatch(body))
            cell.Deactivate();

        if (trigger.StartRegex is { } startRx && startRx.IsMatch(body))
            cell.Activate(eventTime.AddSeconds(trigger.DurationSeconds));
    }

    private void StartOrRefresh(TriggerDefinition trigger, Match match, DateTime eventTime)
    {
        _seen.Add(trigger.Id);

        string key = BuildKey(trigger, match);
        DateTime end = eventTime.AddSeconds(trigger.DurationSeconds);

        if (_active.TryGetValue(key, out var existing))
        {
            if (trigger.RefreshOnRetrigger)
            {
                existing.Restart(trigger.DurationSeconds, end);
                Reposition(existing);
            }
            return;
        }

        var vm = TimerBarViewModel.CreateTimer(
            key, BuildLabel(trigger, match), trigger.Category,
            trigger.DurationSeconds, end, MakeBrush(trigger.Color),
            trigger.Alert?.AtSeconds ?? 0,
            trigger.Alert?.OnExpire ?? false,
            trigger.Alert?.Speak,
            trigger.Alert?.Sound);

        _active[key] = vm;
        InsertSorted(vm);
    }

    private void Remove(string key)
    {
        if (_active.Remove(key, out var vm))
            Bars.Remove(vm);
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
                if (cell.AlertOnExpire) _alerts.Fire(cell.AlertSpeak, cell.AlertSound);
            }
            else if (cell.AlertAtSeconds > 0 && !cell.FadeAlertFired &&
                     cell.RemainingSeconds > 0 && cell.RemainingSeconds <= cell.AlertAtSeconds)
            {
                cell.FadeAlertFired = true;
                _alerts.Fire(cell.AlertSpeak, cell.AlertSound);
            }
        }
    }

    private void UpdateBars(DateTime now)
    {
        List<TimerBarViewModel>? expired = null;
        foreach (var bar in Bars)
        {
            bar.Refresh(now, _warnSeconds);

            if (!bar.IsMissing && bar.AlertAtSeconds > 0 && !bar.FadeAlertFired &&
                bar.RemainingSeconds > 0 && bar.RemainingSeconds <= bar.AlertAtSeconds)
            {
                bar.FadeAlertFired = true;
                _alerts.Fire(bar.AlertSpeak, bar.AlertSound);
            }

            if (bar.IsExpired)
                (expired ??= new()).Add(bar);
        }

        if (expired is not null)
        {
            foreach (var bar in expired)
            {
                if (bar.AlertOnExpire)
                    _alerts.Fire(bar.AlertSpeak, bar.AlertSound);
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

    private void CheckMissing(DateTime now)
    {
        foreach (var t in _triggers)
        {
            if (!t.Enabled || !t.RemindWhenMissing || !_seen.Contains(t.Id))
                continue;

            bool active = _active.Keys.Any(k =>
                k == t.Id || k.StartsWith(t.Id + "|", StringComparison.Ordinal));

            if (active)
            {
                if (_missing.Remove(t.Id, out var mb)) Bars.Remove(mb);
                _lastRemind.Remove(t.Id);
                continue;
            }

            if (!_missing.ContainsKey(t.Id))
            {
                var mb = TimerBarViewModel.CreateMissing(
                    "missing|" + t.Id, t.Name, t.Category, MakeBrush("#E53935"));
                _missing[t.Id] = mb;
                InsertSorted(mb);
                _alerts.Speak($"{t.Name} missing");
                _lastRemind[t.Id] = now;
            }
            else if (!_lastRemind.TryGetValue(t.Id, out var last) ||
                     (now - last).TotalSeconds >= _remindInterval)
            {
                _alerts.Speak($"{t.Name} missing");
                _lastRemind[t.Id] = now;
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
