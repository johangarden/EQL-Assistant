using System.Windows.Media;

namespace EQLOverlay.Views;

/// <summary>
/// Each exaltation socket TYPE owns a color — the same on the character
/// sheet's doll and the Inventory window's pills, so a letter reads the same
/// everywhere: O ornamentation lavender, F focus blue, C click green,
/// W worn amber, P proc red.
/// </summary>
public static class SocketColors
{
    /// <summary>Dark ink for text sitting ON a filled pill.</summary>
    public static readonly Brush Ink = Make("#0F1620");

    private static readonly Dictionary<string, Brush> Fills = new(StringComparer.Ordinal)
    {
        ["O"] = Make("#B39DDB"),
        ["F"] = Make("#64B5F6"),
        ["C"] = Make("#66BB6A"),
        ["W"] = Make("#FFB74D"),
        ["P"] = Make("#E57373"),
    };

    private static readonly Brush Fallback = Make("#7F93AD");

    public static Brush Fill(string label) =>
        Fills.TryGetValue(label, out var b) ? b : Fallback;

    private static SolidColorBrush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
