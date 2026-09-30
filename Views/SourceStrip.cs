using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The lines every tool that reads your /who or your inventory dump shows
/// first (owner, 30 Sep: "align anywhere /who and the dump are needed — the
/// same info, the character sheet's words"): what the game has stated about
/// your classes and level; the dump's age; and a pill per storage with the age
/// of its last capture — the character sheet's pills (owner: "on each page that
/// uses the dump"). Amber is a thing to do in game; dim is fine.
/// </summary>
public static class SourceStrip
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Dim = F("#C9D4E3"), Hint = F("#7F93AD"), Faint = F("#5C6B82"), Warn = F("#FFB074"), WarnBg = F("#2A2010"), Edge = F("#3A4560");

    /// <summary>One line: "/WHO  Level 44 SHD/SHM/ENC · stated by /who at 21:31" or the amber ask.</summary>
    public static TextBlock Who(string snapshot)
    {
        bool known = snapshot.Length > 0 && !snapshot.Contains("type /who", StringComparison.Ordinal);
        var tb = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
        tb.Inlines.Add(new Run("/WHO  ") { Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, BaselineAlignment = BaselineAlignment.Center });
        tb.Inlines.Add(new Run(known ? snapshot : snapshot.Length > 0 ? snapshot : "no /who yet — type /who in game for your classes and level; pick your classes by hand meanwhile") { Foreground = known ? Dim : Warn });
        return tb;
    }

    /// <summary>The dump line and the storage pills. <paramref name="lastSeen"/> =
    /// when each storage was last captured (the character sheet's memory), for
    /// the ones this dump doesn't carry.</summary>
    public static UIElement Dump(InventoryStore.Dump? dump, DateTime stamp, IReadOnlyDictionary<string, DateTime>? lastSeen = null)
    {
        var sp = new StackPanel();
        var tb = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
        tb.Inlines.Add(new Run("DUMP  ") { Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, BaselineAlignment = BaselineAlignment.Center });
        sp.Children.Add(tb);
        if (dump is null)
        {
            tb.Inlines.Add(new Run("no inventory dump — type /outputfile inventory in game (open your bank, depot and hoard first so they're in it)") { Foreground = Warn });
            return sp;
        }
        var age = DateTime.Now - stamp;
        bool stale = age.TotalDays >= InventoryStore.DumpStaleDays;
        tb.Inlines.Add(new Run($"{stamp.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)} · {LongAge(age)}") { Foreground = stale ? Warn : Dim });
        if (stale) tb.Inlines.Add(new Run(" — gear may have moved since; re-type /outputfile inventory in game") { Foreground = Warn });

        // A pill per storage: in this dump (dim, the dump's age) or not (amber with
        // its last capture, faint "never") — the game only writes a storage while
        // its window is open.
        var pills = new WrapPanel { Margin = new Thickness(0, 1, 0, 0) };
        int present = 0;
        var missing = new List<UIElement>();
        foreach (var (key, label) in InventoryStore.StorageDefs)
        {
            bool covered = dump.Covered.Contains(key) || (key == "hoard" && dump.HasExtraItemSection);
            if (covered) { present++; pills.Children.Add(Pill($"{label} · {ShortAge(age)}", Faint, Edge, Brushes.Transparent, $"{label} — in this dump ({stamp:d MMM HH:mm}).")); continue; }
            DateTime seen = lastSeen is not null && lastSeen.TryGetValue(key, out var t) ? t : default;
            missing.Add(seen == default
                ? Pill($"{label} · never", Hint, Edge, Brushes.Transparent, $"{label} — never captured. Open it in game, then re-type /outputfile inventory.")
                : Pill($"{label} · {ShortAge(DateTime.Now - seen)}", Warn, Warn, WarnBg, $"{label} — last captured {seen:d MMM HH:mm}; this dump doesn't carry it. Open it in game, then re-dump."));
        }
        pills.Children.Insert(0, new TextBlock
        {
            Text = missing.Count == 0 ? $"all {InventoryStore.StorageDefs.Length} storages in this dump" : $"{present} of {InventoryStore.StorageDefs.Length} storages in this dump —",
            Foreground = Hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(38, 0, 6, 3),
        });
        foreach (var m in missing) pills.Children.Add(m);
        sp.Children.Add(pills);
        return sp;
    }

    private static Border Pill(string text, Brush fg, Brush line, Brush bg, string tip) => new()
    {
        CornerRadius = new CornerRadius(9), BorderBrush = line, BorderThickness = new Thickness(1), Background = bg, Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(0, 0, 5, 3),
        ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, FontSize = 10, Foreground = fg },
    };

    private static string ShortAge(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours < 48) return $"{(int)t.TotalHours}h";
        return $"{(int)t.TotalDays}d";
    }

    private static string LongAge(TimeSpan t)
    {
        int days = (int)t.TotalDays;
        return days <= 0 ? "today" : days == 1 ? "1 day old" : $"{days} days old";
    }

    /// <summary>Both, stacked, for a page that reads both.</summary>
    public static StackPanel Both(string snapshot, InventoryStore.Dump? dump, DateTime stamp, IReadOnlyDictionary<string, DateTime>? lastSeen = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(Who(snapshot));
        sp.Children.Add(Dump(dump, stamp, lastSeen));
        return sp;
    }
}
