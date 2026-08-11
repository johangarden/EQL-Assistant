using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Raid progression viewer: targets grouped by zone tier, with kill counts and
/// last-kill dates. A killed target unfolds into its full kill history — when,
/// difficulty, time-to-kill, what dropped — with a "fight ↗" jump into the DPS
/// history for kills recorded live. Refreshes itself while open.
/// </summary>
public partial class RaidKillsWindow : Window
{
    public sealed record BadgeVm(string Text, Brush Bg, Brush Fg);
    public sealed record KillDetailVm(string Header, string ItemsText, Brush ItemsBrush,
        Visibility FightVisibility, DateTime FightEndedAt, string FightLabel);
    public sealed record KillRow(string Name, string Detail, Brush NameBrush, FontWeight Weight,
        List<BadgeVm> Badges, string Chevron, Visibility DetailsVisibility, List<KillDetailVm> Details,
        Geometry? Glyph, Brush GlyphBrush, Brush GlyphBg, Brush GlyphRing, string Monogram,
        double GlyphOpacity)
    {
        public bool Expandable => Chevron.Length > 0;
    }
    public sealed record TierRow(string Title, Visibility ClearedVisibility, List<KillRow> Rows);

    private readonly RaidKills _raids;
    private readonly DispatcherTimer _tick;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set by the opener: jump to a fight (EndedAt + Label) in the DPS history.</summary>
    public Action<DateTime, string>? OpenFightRequested { get; set; }

    private const int MaxDetailKills = 15;

    private static readonly Brush KilledFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush UnkilledFg = Freeze(Color.FromRgb(0x6E, 0x7C, 0x93));
    private static readonly Brush BadgeOnBg = Freeze(Color.FromRgb(0x1F, 0x6B, 0x2E));
    private static readonly Brush BadgeOnFg = Freeze(Color.FromRgb(0xC9, 0xF0, 0xD2));
    private static readonly Brush BadgeOffBg = Freeze(Color.FromRgb(0x20, 0x29, 0x3A));
    private static readonly Brush BadgeOffFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush ItemsFg = Freeze(Color.FromRgb(0x8F, 0xA6, 0xC4));
    private static readonly Brush NoItemsFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));

    public RaidKillsWindow(RaidKills raids)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _raids = raids;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();

        Loaded += (_, _) => Refresh();
        Closed += (_, _) => _tick.Stop();
    }

    private void Refresh()
    {
        SummaryText.Text = $"{_raids.TotalKilled} / {_raids.TotalTargets} defeated";

        TiersControl.ItemsSource = _raids.GetView().Select(t => new TierRow(
            $"{t.Name}  ({t.Killed}/{t.Targets.Count})",
            t.Cleared ? Visibility.Visible : Visibility.Collapsed,
            t.Targets.Select(x =>
            {
                bool killed = x.Count > 0;
                bool expanded = killed && _expanded.Contains(x.Name);
                int drops = killed
                    ? _raids.KillsFor(x.Name).Sum(k => k.Items.Sum(i => i.Count)) : 0;
                var badge = RaidGlyphs.For(x.Name);
                var c = badge.Tint;
                return new KillRow(
                    x.Name,
                    killed
                        ? $"{x.Count}× · last {x.Last:dd MMM yyyy}{(drops > 0 ? $" · {drops} drops" : "")}"
                        : "—",
                    killed ? KilledFg : UnkilledFg,
                    killed ? FontWeights.SemiBold : FontWeights.Normal,
                    killed
                        ? Enumerable.Range(0, 5).Select(d => new BadgeVm($"D{d}",
                            x.Tiers.Contains(d) ? BadgeOnBg : BadgeOffBg,
                            x.Tiers.Contains(d) ? BadgeOnFg : BadgeOffFg)).ToList()
                        : new List<BadgeVm>(),
                    killed ? (expanded ? "▾" : "▸") : "",
                    expanded ? Visibility.Visible : Visibility.Collapsed,
                    expanded ? BuildDetails(x.Name) : new List<KillDetailVm>(),
                    badge.Glyph,
                    Freeze(c),
                    Freeze(Color.FromArgb(40, c.R, c.G, c.B)),
                    Freeze(Color.FromArgb(96, c.R, c.G, c.B)),
                    badge.Monogram ?? "",
                    killed ? 1.0 : 0.35); // unearned targets show a faded tease
            }).ToList()
        )).ToList();
    }

    /// <summary>One line per recorded kill, newest first: timestamp · difficulty
    /// · time-to-kill (when the fight was captured live) · the drops.</summary>
    private List<KillDetailVm> BuildDetails(string name)
    {
        var list = new List<KillDetailVm>();
        var kills = _raids.KillsFor(name);
        foreach (var k in kills.Take(MaxDetailKills))
        {
            var parts = new List<string> { $"{k.When:ddd dd MMM yyyy HH:mm}", $"D{k.D}" };
            if (!string.IsNullOrEmpty(k.Zone)) parts.Add(k.Zone);
            if (k.FightSeconds > 0) parts.Add($"killed in {Ttk(k.FightSeconds)}");

            string items = k.Items.Count == 0
                ? "no drops recorded"
                : string.Join("    ", k.Items.Select(i =>
                    $"• {i.Item}{(i.Count > 1 ? $" ×{i.Count}" : "")} ({i.Kind.ToLowerInvariant()})"));

            list.Add(new KillDetailVm(
                string.Join(" · ", parts), items,
                k.Items.Count == 0 ? NoItemsFg : ItemsFg,
                k.FightEndedAt is not null && OpenFightRequested is not null
                    ? Visibility.Visible : Visibility.Collapsed,
                k.FightEndedAt ?? default,
                k.FightLabel ?? ""));
        }
        if (kills.Count > MaxDetailKills)
            list.Add(new KillDetailVm($"… and {kills.Count - MaxDetailKills} earlier kill(s)",
                "", NoItemsFg, Visibility.Collapsed, default, ""));
        return list;
    }

    private static string Ttk(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KillRow row || !row.Expandable) return;
        if (!_expanded.Remove(row.Name)) _expanded.Add(row.Name);
        Refresh();
    }

    private void Fight_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is KillDetailVm vm && vm.FightLabel.Length > 0)
            OpenFightRequested?.Invoke(vm.FightEndedAt, vm.FightLabel);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
