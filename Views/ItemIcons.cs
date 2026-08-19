using System.Windows.Media.Imaging;

namespace EQLOverlay.Views;

/// <summary>
/// The wiki's 40×40 item icons, embedded as WPF resources
/// (data\item-icons\item-{id}.png). Lookup is cached and miss-tolerant —
/// an icon id the bundle doesn't carry returns null and the UI simply
/// draws no image.
/// </summary>
public static class ItemIcons
{
    private static readonly Dictionary<int, BitmapImage?> Cache = new();

    public static BitmapImage? Get(int? iconId)
    {
        if (iconId is not { } id) return null;
        if (Cache.TryGetValue(id, out var cached)) return cached;
        BitmapImage? img = null;
        try
        {
            img = new BitmapImage(new Uri(
                $"pack://application:,,,/data/item-icons/item-{id}.png"));
            img.Freeze();
        }
        catch
        {
            // no such resource — 14 of 11,375 items state no icon, and a few
            // ids may lack a shipped file; both draw as "no image".
        }
        Cache[id] = img;
        return img;
    }
}
