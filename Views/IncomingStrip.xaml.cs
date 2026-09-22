using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The incoming-damage chart, host-agnostic (owner, 21 Sep: "stand-alone or
/// attached to the top of the DPS meter"). <see cref="IncomingWindow"/> wraps
/// it in its own frame; <see cref="MeterWindow"/> paints it as the cap of the
/// meter card. Both feed it an <see cref="IncomingWatch.Snapshot"/> through
/// <see cref="Paint"/>; the stance pill IS the verdict (green = your stance
/// halves the bigger half, red names the stance that would, grey = mixed or
/// nothing taken).
/// </summary>
public partial class IncomingStrip : UserControl
{
    private static readonly Brush MeleeFill = Freeze("#E57373");
    private static readonly Brush SpellFill = Freeze("#9575CD");
    private static readonly Brush ChipDim = Freeze("#C9D4E3");
    private static readonly Brush ChipDimBg = Freeze("#232B3D");
    private static readonly Brush ChipDimBorder = Freeze("#3A4560");
    private static readonly Brush ChipOk = Freeze("#7CE07C");
    private static readonly Brush ChipOkBg = Freeze("#1A2A1E");
    private static readonly Brush ChipOkBorder = Freeze("#2E6B48");
    private static readonly Brush ChipSwitch = Freeze("#FF8A80");
    private static readonly Brush ChipSwitchBg = Freeze("#2A1416");
    private static readonly Brush ChipSwitchBorder = Freeze("#7A2E33");
    private static readonly Brush TitleDim = Freeze("#7F93AD");
    private static readonly Brush TitleFaint = Freeze("#5C6B82");

    private bool _compact;
    private bool _foldQuiet = true;

    public IncomingStrip()
    {
        InitializeComponent();
    }

    /// <summary>Docked look: 28 px columns, the split shows numbers only (the
    /// colours already say melee / spells), and the title reads "quiet" while
    /// nothing hits you.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            Columns.Height = value ? 28 : 38;
            HeaderRow.Margin = new Thickness(0, 0, 0, value ? 5 : 6);
        }
    }

    /// <summary>Compact only: fold to the header row while nothing was taken
    /// (off = keep a one-line placeholder under the header instead).</summary>
    public bool FoldQuiet { get => _foldQuiet; set => _foldQuiet = value; }

    /// <summary>The verdict kind last painted ("switch" / "ok" / "mixed" / "") — selftest.</summary>
    public string LastKind { get; private set; } = "";
    /// <summary>The stance pill's text last painted ("DEFENSIVE ▸ MAGE HUNTER") — selftest.</summary>
    public string LastChip { get; private set; } = "";
    /// <summary>Whether the chart body was visible after the last paint — selftest.</summary>
    public bool BodyShown => Body.Visibility == Visibility.Visible;

    public void Paint(IncomingWatch.Snapshot s)
    {
        bool any = s.Any;
        TitleText.Text = _compact ? (any ? $"INCOMING · {s.WindowSec}S" : "INCOMING · QUIET") : $"INCOMING · LAST {s.WindowSec}S";
        TitleText.Foreground = _compact && !any ? TitleFaint : TitleDim;
        AxisLeft.Text = $"−{s.WindowSec}s";
        QuietText.Text = $"No damage taken in the last {s.WindowSec} s.";

        string st = s.Stance.Trim();
        string stanceLabel = st.Length > 0 ? st.ToUpperInvariant() : "STANCE ?";
        string target = s.VerdictKind == "switch" ? (s.SpellShare >= 0.6 ? "MAGE HUNTER" : "DEFENSIVE") : "";
        StanceText.Text = target.Length > 0 ? $"{stanceLabel} ▸ {target}" : stanceLabel;
        (StanceText.Foreground, StanceChip.Background, StanceChip.BorderBrush) = s.VerdictKind switch
        {
            "ok" => (ChipOk, ChipOkBg, ChipOkBorder),
            "switch" => (ChipSwitch, ChipSwitchBg, ChipSwitchBorder),
            _ => (ChipDim, ChipDimBg, ChipDimBorder),
        };
        LastChip = StanceText.Text;
        LastKind = s.VerdictKind;

        Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        // Standalone hosts paint their own placeholder; the compact cap
        // either folds to the header or keeps a one-liner under it.
        QuietText.Visibility = !any && _compact && !_foldQuiet ? Visibility.Visible : Visibility.Collapsed;
        HeaderRow.Margin = new Thickness(0, 0, 0, any || QuietText.Visibility == Visibility.Visible ? (_compact ? 5 : 6) : 0);
        if (!any) return;

        // Compact drops the words, never the shares (owner, 22 Sep: "the % is gone?").
        MeleeText.Text = _compact ? $"−{s.Melee:N0} · {s.MeleeShare * 100:0}%" : $"−{s.Melee:N0} melee {s.MeleeShare * 100:0}%";
        SpellText.Text = _compact ? $"{s.SpellShare * 100:0}% · −{s.Spell:N0}" : $"{s.SpellShare * 100:0}% spells −{s.Spell:N0}";
        MeleeCol.Width = new GridLength(Math.Max(0.0001, s.Melee), GridUnitType.Star);
        SpellCol.Width = new GridLength(Math.Max(0.0001, s.Spell), GridUnitType.Star);
        MeleeBar.Visibility = s.Melee > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpellBar.Visibility = s.Spell > 0 ? Visibility.Visible : Visibility.Collapsed;

        double colH = Columns.Height - 2;
        double max = 1;
        for (int i = 0; i < s.WindowSec; i++) max = Math.Max(max, s.MeleeCols[i] + s.SpellCols[i]);
        Columns.Columns = s.WindowSec;
        Columns.Children.Clear();
        for (int i = 0; i < s.WindowSec; i++)
        {
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0) };
            double sh = s.SpellCols[i] / max * colH, mh = s.MeleeCols[i] / max * colH;
            if (sh > 0.5) col.Children.Add(new Border { Height = sh, Background = SpellFill, Opacity = 0.9 });
            if (mh > 0.5) col.Children.Add(new Border { Height = mh, Background = MeleeFill, Opacity = 0.9 });
            Columns.Children.Add(col);
        }
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
