using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The Sky quest helper panel — two sections, the same knowledge at two
/// moments. IN FRONT OF YOU: con or hit a known quest dropper and its quest
/// items appear, needed-first, fading ~20s after the last mention (zoning
/// clears it). HUNTING: every ★-tracked quest's still-missing items with
/// their droppers, checking themselves off through the loot counts.
/// Materializes only when it has something to say (or while unlocked, so it
/// can be placed). Right-click: admit completed quests / open the Sky window.
/// </summary>
public partial class SkyHelperWindow : Window
{
    private sealed record LineVm(string Text, Brush Fg, FontWeight Weight, double Size,
        Thickness Margin, TextDecorationCollection? Deco = null);

    private const double CardLingerSec = 20;

    private readonly SkyHelper _helper;
    private readonly SkyQuests _sky;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private nint _hwnd;
    private bool _locked;
    private bool _hidden;

    private string? _cardMob;
    private DateTime _cardAt;

    /// <summary>"Open Sky quests…" from the context menu.</summary>
    public Action? OpenSkyRequested { get; set; }

    /// <summary>Persist the show-completed choice (MainWindow owns the config).</summary>
    public Action<bool>? ShowCompletedChanged { get; set; }

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    private static readonly Brush LabelFg = Freeze("#5C6B82");
    private static readonly Brush MobFg = Freeze("#DCE6F5");
    private static readonly Brush HintFg = Freeze("#7F93AD");
    private static readonly Brush DimFg = Freeze("#5C6B82");
    private static readonly Brush NeedFg = Freeze("#E8C15A");
    private static readonly Brush HaveFg = Freeze("#66BB6A");

    public SkyHelperWindow(SkyHelper helper, SkyQuests sky, ConfigService configService, double opacity)
    {
        InitializeComponent();
        _helper = helper;
        _sky = sky;
        Title = "EQL Assistant — Sky helper";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "skyhelper", Anchor.TopRight, 40, 320);

        _helper.Sighted += mob => Dispatcher.BeginInvoke(() =>
        {
            _cardMob = mob;
            _cardAt = DateTime.Now;
            Refresh();
        });
        _helper.Cleared += () => Dispatcher.BeginInvoke(() => { _cardMob = null; Refresh(); });
        _sky.Changed += OnSkyChanged;

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _tick.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
        };
        Closed += (_, _) => { _tick.Stop(); _sky.Changed -= OnSkyChanged; };

        BuildContextMenu();
        Refresh(); // Loaded re-runs it; this keeps a never-shown window honest (selftest too)
    }

    private void OnSkyChanged() => Dispatcher.BeginInvoke(Refresh);

    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        Refresh();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyClickThrough();
        ApplyLockVisual();
        Refresh();
    }

    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();

    /// <summary>Test hook: the rendered lines (gated self-test only).</summary>
    internal IReadOnlyList<string> LineTexts =>
        (LinesControl.ItemsSource as List<LineVm>)?.Select(l => l.Text).ToList() ?? new();

    private void Refresh()
    {
        // The card expires quietly; the next mention restarts it.
        if (_cardMob is not null && (DateTime.Now - _cardAt).TotalSeconds > CardLingerSec)
            _cardMob = null;

        var lines = new List<LineVm>();
        BuildCard(lines);
        BuildHunting(lines);
        LinesControl.ItemsSource = lines;

        Placeholder.Visibility = !_locked && lines.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        bool show = !_hidden && (lines.Count > 0 || !_locked);
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private void BuildCard(List<LineVm> lines)
    {
        if (_cardMob is null) return;
        var items = _helper.ItemsFor(_cardMob);
        if (items.Count == 0) { _cardMob = null; return; }

        lines.Add(new LineVm("IN FRONT OF YOU", LabelFg, FontWeights.Bold, 9.5,
            new Thickness(2, 0, 0, 1)));
        double ago = (DateTime.Now - _cardAt).TotalSeconds;
        lines.Add(new LineVm($"{_cardMob}  ·  seen {ago:0}s ago", MobFg, FontWeights.Bold, 13,
            new Thickness(2, 0, 0, 2)));
        foreach (var it in items)
        {
            bool have = it.Held >= it.Need;
            string state = it.QuestDone ? "quest done"
                : have ? (it.Need > 1 ? $"HAVE {it.Held}/{it.Need} — hand in" : "HAVE — hand in")
                : it.Need > 1 ? $"NEEDED {it.Held}/{it.Need}" : "NEEDED";
            Brush fg = it.QuestDone ? DimFg : have ? HaveFg : NeedFg;
            lines.Add(new LineVm($"{it.Item} — {state}", fg, FontWeights.Bold, 12,
                new Thickness(10, 1, 0, 0)));
            lines.Add(new LineVm($"{it.Quest} · {it.Class}{(it.Island.Length > 0 ? $" · {it.Island}" : "")}",
                HintFg, FontWeights.Normal, 10.5, new Thickness(10, 0, 0, 2)));
        }
    }

    private void BuildHunting(List<LineVm> lines)
    {
        var tracked = _sky.TrackedQuests();
        if (tracked.Count == 0) return;

        lines.Add(new LineVm(
            $"HUNTING — {tracked.Count} QUEST{(tracked.Count == 1 ? "" : "S")} TRACKED",
            LabelFg, FontWeights.Bold, 9.5, new Thickness(2, lines.Count > 0 ? 8 : 0, 0, 1)));
        foreach (var q in tracked)
        {
            lines.Add(new LineVm($"{q.Name}  ·  {SkyHelper.Abbr(q.Class)}", MobFg,
                FontWeights.SemiBold, 12, new Thickness(2, 1, 0, 0)));
            foreach (var it in q.Items)
            {
                int held = Math.Min(it.Count, _sky.HeldCount(it));
                bool done = held >= it.Count;
                string who = it.Mobs.Count > 0 ? string.Join(" / ", it.Mobs) : it.Who;
                string count = it.Count > 1 ? $" {held}/{it.Count}" : "";
                lines.Add(new LineVm(
                    $"{it.Name}{count} — {who}{(it.Where.Length > 0 ? $" · {it.Where}" : "")}",
                    done ? DimFg : HintFg, FontWeights.Normal, 11,
                    new Thickness(10, 0, 0, 0),
                    done ? TextDecorations.Strikethrough : null));
            }
        }
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        var completed = new MenuItem
        {
            Header = "Show items for completed quests",
            IsCheckable = true,
            IsChecked = _helper.ShowCompleted,
        };
        completed.Click += (_, _) =>
        {
            _helper.ShowCompleted = completed.IsChecked;
            ShowCompletedChanged?.Invoke(completed.IsChecked);
            Refresh();
        };
        menu.Items.Add(completed);
        var open = new MenuItem { Header = "Open Sky quests…" };
        open.Click += (_, _) => OpenSkyRequested?.Invoke();
        menu.Items.Add(open);
        RootBorder.ContextMenu = menu;
    }

    private void ApplyClickThrough()
    {
        // Stays clickable while unlocked (drag + right-click); locked = click-through.
        if (_hwnd != nint.Zero)
            NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        // The card look stays in both states — the content must read in game;
        // unlocked only adds the drag handle (and right-click config).
        Header.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked)
            DragMove();
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
