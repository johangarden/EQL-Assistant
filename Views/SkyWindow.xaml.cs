using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Plane of Sky quest browser: class filter + search, quests sorted
/// closest-to-done, have/need chips per turn-in item with dropper tooltips,
/// reward stats on hover, and a manual/automatic completion checkmark.
/// </summary>
public partial class SkyWindow : Window
{
    private readonly SkyQuests _sky;
    private bool _initializing = true;

    private static readonly Brush HaveBg = Freeze(Color.FromRgb(0x1F, 0x6B, 0x2E));
    private static readonly Brush HaveFg = Freeze(Color.FromRgb(0xC9, 0xF0, 0xD2));
    private static readonly Brush PartBg = Freeze(Color.FromRgb(0x5A, 0x46, 0x1B));
    private static readonly Brush PartFg = Freeze(Color.FromRgb(0xFF, 0xE0, 0x82));
    private static readonly Brush NeedBg = Freeze(Color.FromRgb(0x20, 0x29, 0x3A));
    private static readonly Brush NeedFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush DoneFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush OpenFg = Freeze(Color.FromRgb(0xDC, 0xE6, 0xF5));

    public sealed record ChipVm(string Text, Brush Bg, Brush Fg, string Tip);

    public sealed record QuestVm(SkyQuests.SkyQuest Quest, string Title, string Subtitle,
        string Reward, string RewardStats, string ProgressText, Brush ProgressBrush,
        bool Done, double CardOpacity, List<ChipVm> Chips);

    public SkyWindow(SkyQuests sky)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _sky = sky;

        ClassBox.ItemsSource = new[] { "All classes" }
            .Concat(_sky.Quests.Select(q => q.Class).Distinct().OrderBy(c => c))
            .ToList();
        ClassBox.SelectedIndex = 0;

        _sky.Changed += OnSkyChanged;
        Closed += (_, _) => _sky.Changed -= OnSkyChanged;

        _initializing = false;
        Refresh();
    }

    private void OnSkyChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Filters_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Done_Checked(object sender, RoutedEventArgs e) => SetDone(sender, true);
    private void Done_Unchecked(object sender, RoutedEventArgs e) => SetDone(sender, false);

    private void SetDone(object sender, bool done)
    {
        if (_initializing) return;
        if ((sender as FrameworkElement)?.DataContext is QuestVm vm)
            _sky.SetCompleted(vm.Quest, done);
    }

    private void Refresh()
    {
        if (_initializing || QuestsControl is null) return;

        string cls = ClassBox.SelectedIndex > 0 ? (string)ClassBox.SelectedItem : "";
        string search = SearchBox.Text.Trim();
        bool hideDone = HideDoneCheck.IsChecked == true;

        var vms = new List<QuestVm>();
        foreach (var q in _sky.Quests)
        {
            if (cls.Length > 0 && !q.Class.Equals(cls, StringComparison.OrdinalIgnoreCase)) continue;
            bool done = _sky.IsCompleted(q);
            if (hideDone && done) continue;
            if (search.Length > 0
                && !q.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !q.Reward.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !q.Items.Any(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                continue;

            vms.Add(ToVm(q, done));
        }

        // Closest-to-done first; completed sink to the bottom.
        vms.Sort((a, b) =>
        {
            if (a.Done != b.Done) return a.Done ? 1 : -1;
            var (ah, an) = _sky.Progress(a.Quest);
            var (bh, bn) = _sky.Progress(b.Quest);
            int cmp = (bn > 0 ? (double)bh / bn : 0).CompareTo(an > 0 ? (double)ah / an : 0);
            return cmp != 0 ? cmp : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        QuestsControl.ItemsSource = vms;
        SummaryText.Text = $"{_sky.CompletedCount} / {_sky.Quests.Count} complete";
    }

    private QuestVm ToVm(SkyQuests.SkyQuest q, bool done)
    {
        var (have, need) = _sky.Progress(q);
        var chips = q.Items.Select(it =>
        {
            int held = Math.Min(it.Count, _sky.HeldCount(it));
            var (bg, fg) = held >= it.Count ? (HaveBg, HaveFg)
                : held > 0 ? (PartBg, PartFg)
                : (NeedBg, NeedFg);
            return new ChipVm($"{it.Name}  {held}/{it.Count}", bg, fg,
                $"{it.Name} — drops from {it.Who} ({it.Where})"
                + (it.Stats is null ? "" : "\n\n" + it.Stats));
        }).ToList();

        return new QuestVm(q,
            $"{q.Name}",
            $"{q.Class} · turn in to {q.Giver}",
            q.Reward, q.RewardStats,
            done ? "✓ done" : $"{have}/{need}",
            done ? DoneFg : OpenFg,
            done, done ? 0.55 : 1.0, chips);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
