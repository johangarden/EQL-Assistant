using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The two lines every tool that reads your /who or your inventory dump shows
/// first (owner, 30 Sep: "align anywhere /who and the dump are needed — the
/// same info, the character sheet's words"): what the game has stated about
/// your classes and level, and how old and how complete the dump is. Amber is
/// a thing to do in game; dim is fine.
/// </summary>
public static class SourceStrip
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Dim = F("#C9D4E3"), Faint = F("#5C6B82"), Warn = F("#FFB074");

    /// <summary>One line: "/WHO  Level 44 SHD/SHM/ENC · stated by /who at 21:31" or the amber ask.</summary>
    public static TextBlock Who(string snapshot)
    {
        bool known = snapshot.Length > 0 && !snapshot.Contains("type /who", StringComparison.Ordinal);
        var tb = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
        tb.Inlines.Add(new Run("/WHO  ") { Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, BaselineAlignment = BaselineAlignment.Center });
        tb.Inlines.Add(new Run(known ? snapshot : snapshot.Length > 0 ? snapshot : "no /who yet — type /who in game for your classes and level; pick your classes by hand meanwhile") { Foreground = known ? Dim : Warn });
        return tb;
    }

    /// <summary>One line (two when a storage is missing): the dump's age and coverage, the character sheet's words.</summary>
    public static TextBlock Dump(InventoryStore.Dump? dump, DateTime stamp)
    {
        var tb = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
        tb.Inlines.Add(new Run("DUMP  ") { Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, BaselineAlignment = BaselineAlignment.Center });
        if (dump is null)
        {
            tb.Inlines.Add(new Run("no inventory dump — type /outputfile inventory in game (open your bank, depot and hoard first so they're in it)") { Foreground = Warn });
            return tb;
        }
        int days = (int)(DateTime.Now - stamp).TotalDays;
        bool stale = days >= InventoryStore.DumpStaleDays;
        tb.Inlines.Add(new Run($"{stamp.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)} · {(days == 0 ? "today" : days == 1 ? "1 day old" : $"{days} days old")}") { Foreground = stale ? Warn : Dim });
        if (stale) tb.Inlines.Add(new Run(" — gear may have moved since; re-type /outputfile inventory in game") { Foreground = Warn });
        var missing = InventoryStore.MissingStorages(dump);
        if (missing.Count > 0)
        {
            tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(new Run("            ") { FontSize = 9.5 });
            tb.Inlines.Add(new Run($"not in this dump: {string.Join(" · ", missing)} — the game only writes a storage while its window is open; open them, then re-type /outputfile inventory") { Foreground = Warn });
        }
        return tb;
    }

    /// <summary>Both lines, stacked, for a page that reads both.</summary>
    public static StackPanel Both(string snapshot, InventoryStore.Dump? dump, DateTime stamp)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(Who(snapshot));
        sp.Children.Add(Dump(dump, stamp));
        return sp;
    }
}
