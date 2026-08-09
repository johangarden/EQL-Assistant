using System.Windows;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Death recap popup: the last hits and heals on you before a death line,
/// with time offsets counted back from the moment you died.
/// </summary>
public partial class DeathRecapWindow : Window
{
    private const int MaxRows = 15;

    private static readonly Brush DamageFg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x80));
    private static readonly Brush HealFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush MissFg = Freeze(Color.FromRgb(0x5C, 0x6B, 0x82));
    private static readonly Brush TextFg = Freeze(Color.FromRgb(0xDC, 0xE6, 0xF5));
    private static readonly Brush TextDimFg = Freeze(Color.FromRgb(0x7F, 0x93, 0xAD));
    private static readonly Brush RowEven = Freeze(Color.FromRgb(0x1B, 0x21, 0x30));
    private static readonly Brush RowBig = Freeze(Color.FromRgb(0x3A, 0x1F, 0x24)); // biggest hit tint
    private static readonly Brush RowOdd = Brushes.Transparent;

    public sealed record RowVm(string T, string Text, string AmountText,
        Brush AmountBrush, Brush TextBrush, Brush RowBg);

    public DeathRecapWindow(CombatParser.DeathEvent death)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        Update(death);
    }

    /// <summary>Refill the window for a newer death (window is reused).</summary>
    public void Update(CombatParser.DeathEvent death)
    {
        TitleText.Text = death.Killer.Length > 0
            ? $"💀 Killed by {death.Killer}"
            : "💀 You died";

        var events = death.Events.TakeLast(MaxRows).ToList();
        var biggest = events.Where(e => !e.Heal && !e.Miss).MaxBy(e => e.Amount);

        var rows = new List<RowVm>();
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            double dt = (death.When - e.When).TotalSeconds;
            string t = dt <= 0 ? "0.0s" : $"-{dt:0.0}s";

            string who = e.Source.Length > 0 ? e.Source : "(unknown)";
            string text = e.Heal
                ? $"{who} · {e.Ability}"
                : $"{who} · {e.Ability}{(e.Crit ? "  (crit)" : "")}";

            string amount = e.Miss ? "miss"
                : e.Heal ? $"+{e.Amount:N0}"
                : $"-{e.Amount:N0}";

            bool isBiggest = biggest is not null && ReferenceEquals(e, biggest);
            rows.Add(new RowVm(t, text, amount,
                e.Miss ? MissFg : e.Heal ? HealFg : DamageFg,
                e.Miss ? TextDimFg : TextFg,
                isBiggest ? RowBig : i % 2 == 0 ? RowEven : RowOdd));
        }
        RowsControl.ItemsSource = rows;

        double taken = events.Where(e => !e.Heal).Sum(e => e.Amount);
        double healed = events.Where(e => e.Heal).Sum(e => e.Amount);
        double span = events.Count > 0
            ? Math.Max(0, (death.When - events[0].When).TotalSeconds) : 0;
        string big = biggest is null ? "" :
            $" · biggest hit {biggest.Amount:N0} ({biggest.Ability}, {(biggest.Source.Length > 0 ? biggest.Source : "unknown")})";
        SummaryText.Text = events.Count == 0
            ? $"{death.When:HH:mm:ss} — no incoming damage was recorded before this death."
            : $"{death.When:HH:mm:ss} · took {taken:N0} damage{(healed > 0 ? $", healed {healed:N0}" : "")} over the last {span:0.0}s{big}";
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
