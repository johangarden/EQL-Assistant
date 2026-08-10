using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Raid progression viewer: targets grouped by zone tier, with kill counts and
/// last-kill dates. Refreshes itself while open so a raid kill shows up live.
/// </summary>
public partial class RaidKillsWindow : Window
{
    public sealed record BadgeVm(string Text, Brush Bg, Brush Fg);
    public sealed record KillRow(string Name, string Detail, Brush NameBrush, FontWeight Weight,
        List<BadgeVm> Badges, string? Tip);
    public sealed record TierRow(string Title, Visibility ClearedVisibility, List<KillRow> Rows);

    private readonly RaidKills _raids;
    private readonly DispatcherTimer _tick;

    private static readonly Brush KilledFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush UnkilledFg = Freeze(Color.FromRgb(0x6E, 0x7C, 0x93));
    private static readonly Brush BadgeOnBg = Freeze(Color.FromRgb(0x1F, 0x6B, 0x2E));
    private static readonly Brush BadgeOnFg = Freeze(Color.FromRgb(0xC9, 0xF0, 0xD2));
    private static readonly Brush BadgeOffBg = Freeze(Color.FromRgb(0x20, 0x29, 0x3A));
    private static readonly Brush BadgeOffFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));

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
                int drops = x.Count > 0
                    ? _raids.KillsFor(x.Name).Sum(k => k.Items.Sum(i => i.Count)) : 0;
                return new KillRow(
                    x.Name,
                    x.Count > 0
                        ? $"{x.Count}× · last {x.Last:dd MMM yyyy}{(drops > 0 ? $" · {drops} drops" : "")}"
                        : "—",
                    x.Count > 0 ? KilledFg : UnkilledFg,
                    x.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    x.Count > 0
                        ? Enumerable.Range(0, 5).Select(d => new BadgeVm($"D{d}",
                            x.Tiers.Contains(d) ? BadgeOnBg : BadgeOffBg,
                            x.Tiers.Contains(d) ? BadgeOnFg : BadgeOffFg)).ToList()
                        : new List<BadgeVm>(),
                    BuildLootTip(x.Name));
            }).ToList()
        )).ToList();
    }

    /// <summary>Hover a killed target — every recorded kill with what it dropped.</summary>
    private string? BuildLootTip(string name)
    {
        var kills = _raids.KillsFor(name);
        if (kills.Count == 0) return null;

        const int maxKills = 12;
        var sb = new System.Text.StringBuilder();
        int shown = 0;
        foreach (var k in kills)
        {
            if (shown++ == maxKills)
            {
                sb.Append($"… and {kills.Count - maxKills} earlier kill(s)");
                break;
            }
            sb.Append($"{k.When:dd MMM yyyy HH:mm} · D{k.D}");
            if (k.Items.Count == 0)
            {
                sb.AppendLine(" — no drops recorded");
            }
            else
            {
                sb.AppendLine();
                foreach (var i in k.Items)
                    sb.AppendLine($"   • {i.Item}{(i.Count > 1 ? $" ×{i.Count}" : "")}  ({i.Kind.ToLowerInvariant()})");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
