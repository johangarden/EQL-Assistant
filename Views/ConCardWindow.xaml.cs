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
/// The con card: "/con a greater sphinx" → "shrugs off Envenomed Breath
/// (poison) 80% · n 5". Reads the resist book only; shows nothing for a mob
/// the book hasn't met past its sample floor. Fades after a few seconds, a
/// click pins it, ✕ closes it. Top-centre, beneath the level-up card's spot.
/// </summary>
public partial class ConCardWindow : Window
{
    private static readonly Brush Immune = Freeze("#FF5C5C");
    private static readonly Brush Resistant = Freeze("#FFB74D");
    private static readonly Brush Fine = Freeze("#7CE07C");
    private static readonly Brush Dim = Freeze("#9FB4D0");
    private static readonly Brush Faint = Freeze("#5C6B82");
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(12);

    private readonly DispatcherTimer _fade;
    private bool _pinned;

    public int RowCount { get; private set; }
    public string Mob { get; private set; } = "";

    public ConCardWindow()
    {
        InitializeComponent();
        Title = "EQL Assistant — Con card";
        _fade = new DispatcherTimer { Interval = Linger };
        _fade.Tick += (_, _) => { _fade.Stop(); if (!_pinned) Close(); };
        SourceInitialized += (_, _) =>
            NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, false); // interactive, never steals focus
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = 150;
            _fade.Start();
        };
        Closed += (_, _) => _fade.Stop(); // panel law: timers die with the window
    }

    /// <summary>Fill for a mob: its verdicts, and the honest line when the
    /// book knows it but it shrugs off nothing.</summary>
    public void Show(string mob, int level, int casts, IReadOnlyList<ResistBook.Verdict> verdicts)
    {
        Mob = mob;
        TitleText.Text = mob + (level > 0 ? $" · Lvl {level}" : "");
        Rows.Children.Clear();
        RowCount = 0;
        foreach (var v in verdicts)
        {
            var row = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
            row.Inlines.Add(new System.Windows.Documents.Run(v.Severity == "immune" ? "shrugs off " : "resists ")
            { Foreground = v.Severity == "immune" ? Immune : Resistant, FontWeight = FontWeights.SemiBold });
            row.Inlines.Add(new System.Windows.Documents.Run(v.Spell) { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
            if (v.School.Length > 0) row.Inlines.Add(new System.Windows.Documents.Run($" ({v.School})") { Foreground = Dim });
            row.Inlines.Add(new System.Windows.Documents.Run($"  {v.Rate * 100:0}% · n {v.N}") { Foreground = Faint, FontSize = 11 });
            Rows.Children.Add(row);
            RowCount++;
        }
        if (verdicts.Count == 0)
            Rows.Children.Add(new TextBlock
            {
                Text = "Nothing it shrugs off so far.", Foreground = Fine, FontSize = 12, FontWeight = FontWeights.SemiBold,
            });
        FootText.Text = $"From {casts} of your casts on this mob — Fight history → Resists has the table.";
        if (!IsVisible) base.Show();
        else { Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2; _pinned = false; _fade.Stop(); _fade.Start(); }
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        _pinned = true;
        RootBorder.BorderBrush = Dim;
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
