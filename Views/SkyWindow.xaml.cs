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

    /// <summary>One class badge: glyph (or ALL-text) ring + completion arc.</summary>
    public sealed record BadgeVm(string ClassName, string Abbr, string CountText, string Tip,
        Brush Ring, Brush Ink, Brush SelBg, Geometry? Arc, Visibility DoneVisibility,
        Geometry? Glyph)
    {
        public Visibility AbbrVisibility => Glyph is null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Class abbreviation over the icon (these aren't the game's
        /// icons, so the name rides along); ALL names itself in the ring.</summary>
        public string TopLabel => Glyph is null ? "" : Abbr;
    }

    // The 16 Sky test classes, each with a fixed badge tint (decoration — the
    // abbreviation is the identifier; tooltips carry the full name).
    private static readonly (string Name, string Abbr, string Hex)[] ClassBadges =
    {
        ("Warrior", "WAR", "#C97C5D"), ("Cleric", "CLR", "#EFE3B0"),
        ("Paladin", "PAL", "#EFA9C4"), ("Ranger", "RNG", "#6FBF73"),
        ("Shadow Knight", "SHD", "#A883D9"), ("Druid", "DRU", "#C9A15C"),
        ("Monk", "MNK", "#9FD9C3"), ("Bard", "BRD", "#E8C15A"),
        ("Rogue", "ROG", "#F2E063"), ("Shaman", "SHM", "#6FD3D3"),
        ("Necromancer", "NEC", "#86E0B0"), ("Wizard", "WIZ", "#6FA8F0"),
        ("Magician", "MAG", "#E06060"), ("Enchanter", "ENC", "#C883E8"),
        ("Beastlord", "BST", "#D9A06F"), ("Berserker", "BER", "#E07F7F"),
    };

    /// <summary>The selected class filter; "" = all quests.</summary>
    private string _selectedClass = "";

    public sealed record QuestVm(SkyQuests.SkyQuest Quest, string Title, string Subtitle,
        string Reward, string RewardStats, string Slot, string ProgressText, Brush ProgressBrush,
        bool Done, double CardOpacity, List<ChipVm> Chips)
    {
        public Visibility SlotVisibility => Slot.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public SkyWindow(SkyQuests sky)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _sky = sky;

        // Slots split into tokens — "FACE BACK" filters under both FACE and BACK.
        SlotBox.ItemsSource = new[] { "All slots" }
            .Concat(_sky.Quests.SelectMany(q => q.Slot.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s))
            .ToList();
        SlotBox.SelectedIndex = 0;

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

    private void Badge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BadgeVm vm) return;
        _selectedClass = vm.ClassName;
        Refresh();
    }

    /// <summary>A completion arc: from 12 o'clock, clockwise, done/total of the
    /// ring (radius 17 in the badge's 38px box). Full = the whole circle.</summary>
    private static Geometry? BuildArc(int done, int total)
    {
        if (done <= 0 || total <= 0) return null;
        const double cx = 19, cy = 19, r = 17;
        double f = Math.Min(1.0, (double)done / total);
        if (f >= 1.0) return new EllipseGeometry(new Point(cx, cy), r, r);
        double angle = -Math.PI / 2 + f * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0,
            isLargeArc: f > 0.5, SweepDirection.Clockwise, isStroked: true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    private void RefreshBadges()
    {
        var badges = new List<BadgeVm>();

        void Add(string className, string abbr, Color tint)
        {
            var quests = className.Length == 0
                ? (IReadOnlyList<SkyQuests.SkyQuest>)_sky.Quests
                : _sky.Quests.Where(q => q.Class.Equals(className, StringComparison.OrdinalIgnoreCase)).ToList();
            int done = quests.Count(_sky.IsCompleted);
            bool complete = quests.Count > 0 && done == quests.Count;
            bool selected = _selectedClass.Equals(className, StringComparison.OrdinalIgnoreCase);
            badges.Add(new BadgeVm(className, abbr, $"{done}/{quests.Count}",
                $"{(className.Length == 0 ? "All quests" : className)} — {done} of {quests.Count} complete",
                complete ? Freeze(Color.FromRgb(0x81, 0xC7, 0x84)) : Freeze(tint),
                selected ? Brushes.White : Freeze(Color.FromArgb(0xD8, tint.R, tint.G, tint.B)),
                selected ? Freeze(Color.FromArgb(0x50, tint.R, tint.G, tint.B)) : Brushes.Transparent,
                BuildArc(done, quests.Count),
                complete ? Visibility.Visible : Visibility.Collapsed,
                className.Length == 0 ? null : ClassGlyphs.For(className)));
        }

        Add("", "ALL", Color.FromRgb(0xFF, 0xC1, 0x2E));
        foreach (var (name, abbr, hex) in ClassBadges)
            Add(name, abbr, (Color)ColorConverter.ConvertFromString(hex));

        BadgesControl.ItemsSource = badges;
    }

    private void Refresh()
    {
        if (_initializing || QuestsControl is null) return;

        RefreshBadges();
        string cls = _selectedClass;
        string slot = SlotBox.SelectedIndex > 0 ? (string)SlotBox.SelectedItem : "";
        string search = SearchBox.Text.Trim();
        bool hideDone = HideDoneCheck.IsChecked == true;

        var vms = new List<QuestVm>();
        foreach (var q in _sky.Quests)
        {
            if (cls.Length > 0 && !q.Class.Equals(cls, StringComparison.OrdinalIgnoreCase)) continue;
            if (slot.Length > 0 && !q.Slot.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(slot, StringComparer.OrdinalIgnoreCase)) continue;
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
            q.Reward, q.RewardStats, q.Slot,
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
