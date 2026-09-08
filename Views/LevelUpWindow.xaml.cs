using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The level-up card: "Level 46!" and the spells your class combo unlocks at
/// that level, from the spell library's per-class levels (1,074 spells carry
/// them). Sits at the top-centre of the primary screen, above the game,
/// never takes focus; fades after a while, a click pins it, "Got it" closes.
/// Skills and disciplines aren't in the library — the footer says so.
/// </summary>
public partial class LevelUpWindow : Window
{
    private static readonly Brush Chip = Freeze("#232B40");
    private static readonly Brush ChipLine = Freeze("#5A6B8C");
    private static readonly Brush Text = Freeze("#E6ECF5");
    private static readonly Brush Dim = Freeze("#9FB4D0");
    private static readonly Brush Gold = Freeze("#E8C15A");
    private static readonly Brush Faint = Freeze("#5C6B82");
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(90);

    private readonly DispatcherTimer _fade;
    private bool _pinned;

    public int RowCount { get; private set; }

    public LevelUpWindow()
    {
        InitializeComponent();
        Title = "EQL Assistant — New at this level";
        _fade = new DispatcherTimer { Interval = Linger };
        _fade.Tick += (_, _) => { _fade.Stop(); if (!_pinned) Close(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetClickThrough(hwnd, false); // interactive, never steals focus from the game
        };
        Loaded += (_, _) =>
        {
            // Top-centre of the primary screen, under the game's top HUD.
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = 90;
            _fade.Start();
        };
        Closed += (_, _) => _fade.Stop(); // panel law: timers die with the window
    }

    /// <summary>Fill the card. <paramref name="classes"/> empty = combo unknown
    /// (the card lists every class and says to /who).</summary>
    public void Show(int level, IReadOnlyCollection<string> classes, IReadOnlyList<(SpellLibrary.Spell Spell, string Cls)> unlocks)
    {
        TitleText.Text = $"Level {level}!";
        string combo = classes.Count > 0 ? string.Join("/", classes) : "";
        SubText.Text = unlocks.Count == 0
            ? (combo.Length > 0 ? $"{combo} · nothing new in the spellbook at {level}." : $"Nothing in the library unlocks at {level}.")
            : combo.Length > 0
                ? $"{combo} · {unlocks.Count} new spell{(unlocks.Count == 1 ? "" : "s")} to buy or scribe"
                : $"{unlocks.Count} spell{(unlocks.Count == 1 ? "" : "s")} unlock at {level} across all classes — type /who to narrow to your combo";

        Rows.Children.Clear();
        RowCount = 0;
        foreach (var (spell, cls) in unlocks.OrderBy(u => u.Cls).ThenBy(u => u.Spell.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var chip = new Border
            {
                Background = Chip, BorderBrush = ChipLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = cls, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Gold },
            };
            DockPanel.SetDock(chip, Dock.Left);
            row.Children.Add(chip);
            var kind = new TextBlock
            {
                Text = spell.Bucket.Length > 0 ? spell.Bucket.ToUpperInvariant() : "",
                FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Faint,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
            DockPanel.SetDock(kind, Dock.Right);
            row.Children.Add(kind);
            row.Children.Add(new TextBlock { Text = spell.Name, FontSize = 12.5, Foreground = Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            Rows.Children.Add(row);
            RowCount++;
        }
        FootText.Text = "Spells only — skills and disciplines aren't in the library yet. The card fades on its own; click it to keep it.";
        if (!IsVisible) base.Show();
        else { Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2; _fade.Stop(); _fade.Start(); }
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        _pinned = true;
        RootBorder.BorderBrush = Dim; // pinned: the gold edge relaxes
        FootText.Text = "Pinned — Got it closes it.";
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
