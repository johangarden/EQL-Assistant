using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// One scrolling-combat-text lane: numbers spawn at the bottom, float up and
/// fade out. Each lane is its own movable, anchored panel (drag frame when
/// unlocked, click-through when locked). Spawns are spaced out so overlapping
/// AoE spam stacks into a queue instead of piling on top of itself.
/// </summary>
public partial class SctLaneWindow : Window
{
    private const double LifetimeSeconds = 2.6;
    private const int MinSpawnGapMs = 260;
    private const int MaxQueued = 8;

    private readonly PanelPlacement _placement;
    private readonly Brush _meleeColor;
    private readonly Brush _spellColor;
    private readonly Brush _procColor;
    private readonly double _fontSize;
    private readonly double _bigThreshold;
    private readonly DispatcherTimer _pump;
    private readonly Queue<(string Label, double Amount, bool Plus, CombatParser.SctFlavor Flavor, bool Crit)> _queue = new();
    private DateTime _lastSpawn = DateTime.MinValue;
    private nint _hwnd;
    private bool _locked;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x28, 0x0A, 0x0E, 0x14));

    public SctLaneWindow(ConfigService config, string placementKey, string title,
        Brush meleeColor, Brush spellColor, Brush procColor,
        double opacity, double fontSize, double bigThreshold, double laneWidth, double laneHeight,
        double defaultOffX, double defaultOffY)
    {
        InitializeComponent();

        Title = "EQL Assistant — SCT " + title;
        HeaderText.Text = "SCT — " + title;
        _meleeColor = meleeColor;
        _spellColor = spellColor;
        _procColor = procColor;
        _fontSize = Math.Clamp(fontSize <= 0 ? 18 : fontSize, 10, 72);
        _bigThreshold = bigThreshold <= 0 ? double.MaxValue : bigThreshold;
        Lane.Width = Math.Clamp(laneWidth <= 0 ? 170 : laneWidth, 80, 800);
        Lane.Height = Math.Clamp(laneHeight <= 0 ? 300 : laneHeight, 100, 1500);
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);

        _placement = new PanelPlacement(this, config, placementKey, Anchor.TopLeft,
            Math.Max(0, defaultOffX), Math.Max(0, defaultOffY));

        _pump = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _pump.Tick += (_, _) => PumpQueue();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _pump.Stop();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _placement.Attach();
        ApplyLockVisual();
        _pump.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyClickThrough();
    }

    // ---- public API -----------------------------------------------------------

    /// <summary>Queue a combat number ("254 Frost Breath"); heals get a leading +.</summary>
    public void Post(string label, double amount, bool plus = false,
        CombatParser.SctFlavor flavor = CombatParser.SctFlavor.Melee, bool crit = false)
    {
        if (Visibility != Visibility.Visible) return;
        if (_queue.Count >= MaxQueued) _queue.Dequeue(); // shed the oldest under AoE spam
        _queue.Enqueue((label, amount, plus, flavor, crit));
        PumpQueue();
    }

    /// <summary>A few sample numbers for the Ctrl+Alt+T demo.</summary>
    public void SpawnDemo()
    {
        Post("backstab", 312, flavor: CombatParser.SctFlavor.Melee, crit: true);
        Post("thorns", 44, flavor: CombatParser.SctFlavor.Proc);
        Post("Frost Breath", 254, flavor: CombatParser.SctFlavor.Spell);
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyClickThrough();
        ApplyLockVisual();
    }

    public void ResetPosition() => _placement.ResetToDefault();

    // ---- spawning ---------------------------------------------------------------

    private void PumpQueue()
    {
        if (_queue.Count == 0) return;
        if ((DateTime.Now - _lastSpawn).TotalMilliseconds < MinSpawnGapMs) return;

        var (label, amount, plus, flavor, crit) = _queue.Dequeue();
        _lastSpawn = DateTime.Now;
        Spawn(label, amount, plus, flavor, crit);
    }

    private void Spawn(string label, double amount, bool plus, CombatParser.SctFlavor flavor, bool crit)
    {
        bool big = crit || amount >= _bigThreshold;
        var text = new TextBlock
        {
            Width = Lane.Width,
            TextAlignment = TextAlignment.Center,
            Foreground = flavor switch
            {
                CombatParser.SctFlavor.Spell => _spellColor,
                CombatParser.SctFlavor.Proc => _procColor,
                _ => _meleeColor,
            },
            FontWeight = FontWeights.Bold,
            FontSize = big ? _fontSize * 1.4 : _fontSize,
            Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 4, Opacity = 0.95, Color = Colors.Black },
        };
        text.Inlines.Add(new Run((plus ? "+" : "") + amount.ToString("N0") + (crit ? "!" : "")));
        if (!string.IsNullOrEmpty(label))
            text.Inlines.Add(new Run("  " + label)
            {
                FontSize = Math.Max(10, _fontSize * 0.62),
                FontWeight = FontWeights.Normal,
            });

        Canvas.SetLeft(text, 0);
        Canvas.SetTop(text, Lane.Height - _fontSize * 1.8);
        Lane.Children.Add(text);

        var dur = TimeSpan.FromSeconds(LifetimeSeconds);
        var rise = new DoubleAnimation(Lane.Height - _fontSize * 1.8, 4, dur);

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.10))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(LifetimeSeconds * 0.7))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(dur)));

        rise.Completed += (_, _) => Lane.Children.Remove(text);
        text.BeginAnimation(Canvas.TopProperty, rise);
        text.BeginAnimation(OpacityProperty, fade);
    }

    // ---- lock / chrome ------------------------------------------------------------

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero)
            NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        Header.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        Outline.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        RootBorder.Background = _locked ? Brushes.Transparent : UnlockedBackdrop;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked)
            DragMove();
    }
}
