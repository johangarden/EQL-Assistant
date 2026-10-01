using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// THE item line (owner, 1 Oct: "the slot finder's item tags — use them in BiS
/// and anywhere it fits"): the wiki icon, the name, a green +N pill, then tags —
/// WORN gold-filled, UPGRADE amber-filled, BiS green outline, CLASSES UNKNOWN dim.
/// One look for every list that names an item you own.
/// </summary>
public static class ItemChips
{
    private static Brush F(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush Card = F("#232B3D"), Edge = F("#3A4560"), Text = F("#E6ECF5"), Faint = F("#5C6B82"), Ink = F("#10151E"),
        Gold = F("#E8C15A"), Green = F("#81C784"), GreenEdge = F("#2E5A36"), GreenBg = F("#15261A"), Amber = F("#FFA85C"), Dim = F("#7F93AD");

    public enum Tag { Worn, Upgrade, Bis, Unknown }

    /// <summary>Icon · name · +N · tags. <paramref name="tier"/> ≤ 0 draws no pill; a null
    /// icon id draws an empty frame so names still line up.</summary>
    public static StackPanel Name(string baseName, int tier, int? iconId, Brush? nameFg = null, int copies = 1, params Tag[] tags)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var img = ItemIcons.Get(iconId);
        sp.Children.Add(img is not null
            ? new Image { Source = img, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }
            : new Border { Width = 22, Height = 22, Background = Card, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0) });
        sp.Children.Add(new TextBlock { Text = baseName, Foreground = nameFg ?? Text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (tier > 0) sp.Children.Add(TierPill(tier));
        if (copies > 1) sp.Children.Add(new TextBlock { Text = $"×{copies}", Foreground = Faint, FontSize = 10.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var t in tags) sp.Children.Add(Chip(t));
        return sp;
    }

    public static Border TierPill(int tier) => new()
    {
        Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(4, 0, 4, 1), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
        BorderBrush = GreenEdge, Background = GreenBg, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = $"+{tier}", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Green },
    };

    /// <summary>One tag: WORN and UPGRADE filled (they're states you act on), BiS and CLASSES UNKNOWN outlined.</summary>
    public static Border Chip(Tag tag)
    {
        var (text, fg, bg, line) = tag switch
        {
            Tag.Worn => ("WORN", Ink, Gold, Gold),
            Tag.Upgrade => ("UPGRADE", Ink, Amber, Amber),
            Tag.Bis => ("BiS", Green, Brushes.Transparent, GreenEdge),
            _ => ("CLASSES UNKNOWN", Dim, Brushes.Transparent, Edge),
        };
        return new Border
        {
            Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
            Background = bg, BorderBrush = line, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.ExtraBold, Foreground = fg },
        };
    }

    /// <summary>A tag inline in a TextBlock (section heads).</summary>
    public static InlineUIContainer Inline(Tag tag) => new(Chip(tag)) { BaselineAlignment = BaselineAlignment.Center };

    /// <summary>"Wicked Sallet +5" → the icon · name · +5 line, from the wiki table.</summary>
    public static StackPanel FromDumpName(string dumpName, ItemStats stats, Brush? nameFg = null, params Tag[] tags)
    {
        var rec = stats.Lookup(dumpName);
        string baseName = System.Text.RegularExpressions.Regex.Replace(dumpName.Trim(), @" \+\d+$", "");
        return Name(rec?.Name is { Length: > 0 } n ? n : baseName, BisFinder.TierOf(dumpName), rec?.Icon, nameFg, 1, tags);
    }
}
