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
    public sealed record KillRow(string Name, string Detail, Brush NameBrush, FontWeight Weight);
    public sealed record TierRow(string Title, Visibility ClearedVisibility, List<KillRow> Rows);

    private readonly RaidKills _raids;
    private readonly DispatcherTimer _tick;

    private static readonly Brush KilledFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush UnkilledFg = Freeze(Color.FromRgb(0x6E, 0x7C, 0x93));

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
            t.Targets.Select(x => new KillRow(
                x.Name,
                x.Count > 0
                    ? $"{x.Count}× · last {x.Last:dd MMM yyyy}"
                    : "—",
                x.Count > 0 ? KilledFg : UnkilledFg,
                x.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal)).ToList()
        )).ToList();
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
