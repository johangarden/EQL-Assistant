using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The faction helper card (owner, 22 Sep): when a faction line lands on a
/// faction of a ★-tracked race it shows the race, the faction, the hit, the
/// standing out of max as a bar, points to max and "~N more like this" from
/// the mob named on the kill line just before. Green for a plus, MAXED when
/// the game caps it (with the race's next faction), red when a kill moved
/// a tracked faction the wrong way. Fades 20 s after the last line; locked
/// and quiet it vanishes, unlocked it keeps a placeholder to be placed.
/// </summary>
public partial class FactionHelperWindow : Window
{
    private const double LingerSec = 20;

    private static readonly Brush Dim = Freeze("#C9D4E3");
    private static readonly Brush Hint = Freeze("#7F93AD");
    private static readonly Brush Faint = Freeze("#5C6B82");
    private static readonly Brush Green = Freeze("#81C784");
    private static readonly Brush GreenDark = Freeze("#3D7A46");
    private static readonly Brush Red = Freeze("#FF8A80");
    private static readonly Brush RedDark = Freeze("#7A2E33");
    private static readonly Brush Track = Freeze("#0F141E");
    private static readonly Brush UnlockedBackdrop = Freeze("#F0141A24");
    private static readonly Brush LockedBackdrop = Freeze("#E0141A24");

    private readonly RaceBook _book;
    private readonly PanelPlacement _placement;
    private readonly DispatcherTimer _tick;
    private readonly Action<string, int, string?> _onHit;
    private readonly Action<string> _onMaxed;
    private nint _hwnd;
    private bool _locked, _hidden, _closed;
    private readonly List<string> _texts = new();

    // The card: the last hit (kind: "hit" / "maxed"), when it landed.
    private (string Kind, string Race, string Faction, int Delta, string? Mob, DateTime At)? _card;

    /// <summary>"Open Races…" from the context menu.</summary>
    public Action? OpenRacesRequested { get; set; }

    public FactionHelperWindow(RaceBook book, ConfigService configService, double opacity)
    {
        InitializeComponent();
        _book = book;
        Title = "EQL Assistant — Faction helper";
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);
        _placement = new PanelPlacement(this, configService, "factionhelper", Anchor.TopRight, 40, 520);

        // Named handlers, unhooked on Closed (the Sky helper's anonymous ones outlived it — 22 Sep).
        _onHit = (faction, delta, mob) => Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            string? race = _book.TrackedRaceOf(faction);
            if (race is null) return;
            _card = ("hit", race, faction, delta, mob, DateTime.Now);
            Refresh();
        });
        _onMaxed = faction => Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            string? race = _book.TrackedRaceOf(faction);
            if (race is null) return;
            _card = ("maxed", race, faction, 0, null, DateTime.Now);
            Refresh();
        });
        _book.FactionHit += _onHit;
        _book.FactionMaxed += _onMaxed;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _placement.Attach(); ApplyLockVisual(); Refresh(); _tick.Start(); };
        SourceInitialized += (_, _) => { _hwnd = new WindowInteropHelper(this).Handle; ApplyClickThrough(); };
        Closed += (_, _) => { _closed = true; _tick.Stop(); _book.FactionHit -= _onHit; _book.FactionMaxed -= _onMaxed; };

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open Races…" };
        open.Click += (_, _) => OpenRacesRequested?.Invoke();
        menu.Items.Add(open);
        ContextMenu = menu;
        Refresh();
    }

    public void SetHidden(bool hidden) { _hidden = hidden; Refresh(); }
    public void SetLocked(bool locked) { _locked = locked; ApplyClickThrough(); ApplyLockVisual(); Refresh(); }
    public void ReloadPlacement() => _placement.Reload();
    public void ResetPosition() => _placement.ResetToDefault();
    public void ApplySettings(double opacity) { Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0); _placement.Reload(); }

    /// <summary>Selftest / render: the texts painted last.</summary>
    public IReadOnlyList<string> LineTexts => _texts;

    /// <summary>Render / demo: show a card without a log line.</summary>
    public void ShowDemo(string kind, string race, string faction, int delta, string? mob)
    {
        _card = (kind, race, faction, delta, mob, DateTime.Now);
        Refresh();
    }

    public void Refresh()
    {
        if (_closed) return;
        if (_card is { } c0 && (DateTime.Now - c0.At).TotalSeconds > LingerSec) _card = null;
        _texts.Clear();
        Body.Children.Clear();
        if (_card is { } c) BuildCard(c);

        Placeholder.Visibility = !_locked && _card is null ? Visibility.Visible : Visibility.Collapsed;
        bool show = !_hidden && !_closed && (_card is not null || !_locked);
        if (show && Visibility != Visibility.Visible) Show();
        else if (!show && Visibility == Visibility.Visible) Hide();
    }

    private void BuildCard((string Kind, string Race, string Faction, int Delta, string? Mob, DateTime At) c)
    {
        var view = _book.ViewOf(c.Faction);
        bool maxed = c.Kind == "maxed" || view.Done;
        bool bad = c.Kind == "hit" && c.Delta < 0;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), BorderThickness = new Thickness(1),
            Background = maxed ? Freeze("#12261C") : bad ? Freeze("#3A1B1E") : Freeze("#12261C"),
            BorderBrush = maxed ? Freeze("#2E6B48") : bad ? RedDark : Freeze("#2E6B48"),
            Child = new TextBlock
            {
                Text = maxed ? "MAXED ✓" : (c.Delta >= 0 ? "+" : "") + c.Delta, FontSize = 10.5, FontWeight = FontWeights.Bold,
                Foreground = bad ? Red : Green,
            },
        };
        _texts.Add(((TextBlock)pill.Child).Text);
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        head.Children.Add(Text($"FACTION · {c.Race.ToUpperInvariant()}", Hint, 10.5, FontWeights.Bold));
        Body.Children.Add(head);

        var line = new DockPanel();
        var standing = new TextBlock { FontSize = 14, FontWeight = FontWeights.ExtraBold, Foreground = view.Negative ? Red : Green, VerticalAlignment = VerticalAlignment.Center };
        standing.Text = view.Done ? $"{Math.Min(view.Standing, view.Max):N0} / {view.Max:N0}" : view.Negative ? $"{view.Standing:N0}" : $"{view.Standing:N0} / {view.Max:N0}";
        _texts.Add(standing.Text);
        DockPanel.SetDock(standing, Dock.Right);
        line.Children.Add(standing);
        line.Children.Add(Text(c.Faction, Dim, 13, FontWeights.SemiBold));
        Body.Children.Add(line);

        if (!view.Done)
        {
            var bar = new Grid { Height = 9, Margin = new Thickness(0, 6, 0, 0) };
            bar.Children.Add(new Border { Background = view.Negative ? Freeze("#2A1416") : Track, CornerRadius = new CornerRadius(4) });
            if (view.Negative)
            {
                double frac = Math.Clamp(-view.Standing / (double)Math.Max(1, view.Max), 0, 1);
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, frac), GridUnitType.Star) });
                var fill = new Border { Background = new LinearGradientBrush(((SolidColorBrush)RedDark).Color, ((SolidColorBrush)Red).Color, 0), CornerRadius = new CornerRadius(4) };
                Grid.SetColumn(fill, 1);
                Grid.SetColumnSpan(bar.Children[0], 2);
                bar.Children.Add(fill);
            }
            else
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, view.Fraction), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - view.Fraction), GridUnitType.Star) });
                Grid.SetColumnSpan(bar.Children[0], 2);
                if (view.Fraction > 0.001)
                    bar.Children.Add(new Border { Background = new LinearGradientBrush(((SolidColorBrush)GreenDark).Color, ((SolidColorBrush)Green).Color, 0), CornerRadius = new CornerRadius(4) });
            }
            Body.Children.Add(bar);
        }

        var meta = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (maxed)
        {
            var race = _book.View(c.Race);
            string next = race is null ? "" : string.Join(", ", race.Factions.Where(f => !f.Done).Select(f => f.Name));
            var t = new TextBlock { FontSize = 11, Foreground = Hint, TextWrapping = TextWrapping.Wrap };
            t.Inlines.Add(new Run(c.Race + " "));
            t.Inlines.Add(new Run(race is null ? "" : $"{race.DoneCount} / {race.Factions.Count}") { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new Run(next.Length > 0 ? $" · next: {next}" : " · all maxed — unlock complete"));
            _texts.Add(t.Inlines.OfType<Run>().Aggregate("", (s, r) => s + r.Text));
            meta.Children.Add(t);
        }
        else if (bad)
        {
            var best = view.Sources.FirstOrDefault(s => s.Hit > 0);
            var t = new TextBlock { FontSize = 11, Foreground = Hint, TextWrapping = TextWrapping.Wrap };
            t.Inlines.Add(new Run("wrong way — "));
            t.Inlines.Add(new Run(c.Mob ?? "that kill") { Foreground = Dim });
            t.Inlines.Add(new Run(best is not null ? $" cost you {Math.Ceiling(-c.Delta / (double)best.Hit):0} kills' worth of {best.Mob}" : $" cost {-c.Delta:N0} points"));
            _texts.Add(t.Inlines.OfType<Run>().Aggregate("", (s, r) => s + r.Text));
            meta.Children.Add(t);
        }
        else
        {
            var mob = Text(c.Mob ?? "", Faint, 11);
            DockPanel.SetDock(mob, Dock.Right);
            meta.Children.Add(mob);
            var t = new TextBlock { FontSize = 11, Foreground = Hint };
            t.Inlines.Add(new Run($"{view.ToGo:N0}") { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new Run(" to max"));
            if (c.Delta > 0 && view.ToGo > 0)
            {
                int more = (int)Math.Ceiling(view.ToGo / (double)c.Delta);
                t.Inlines.Add(new Run(" · "));
                t.Inlines.Add(new Run(more == 1 ? "one more like this" : $"~{more:N0} more like this") { Foreground = Dim, FontWeight = FontWeights.SemiBold });
            }
            _texts.Add(t.Inlines.OfType<Run>().Aggregate("", (s, r) => s + r.Text));
            meta.Children.Add(t);
        }
        Body.Children.Add(meta);
    }

    private TextBlock Text(string text, Brush fg, double size, FontWeight? weight = null)
    {
        _texts.Add(text);
        return new TextBlock { Text = text, Foreground = fg, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    }

    private void ApplyClickThrough() { if (_hwnd != nint.Zero) NativeMethods.SetClickThrough(_hwnd, _locked); }

    private void ApplyLockVisual()
    {
        Header.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        RootBorder.Background = _locked ? LockedBackdrop : UnlockedBackdrop;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked) DragMove();
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
