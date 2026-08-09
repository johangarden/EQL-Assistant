using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>Browser for the persisted loot history (search + kind filter).</summary>
public partial class LootWindow : Window
{
    private const int MaxRows = 400;

    private readonly LootTracker _loot;

    private static readonly Brush UpgradeFg = Freeze(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush SoldFg = Freeze(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush KeptFg = Freeze(Color.FromRgb(0x8F, 0xA6, 0xC4));

    public sealed record RowVm(string Item, string Detail, string ValueText, Brush ValueBrush);

    public LootWindow(LootTracker loot)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _loot = loot;
        _loot.Changed += OnLootChanged;
        Closed += (_, _) => _loot.Changed -= OnLootChanged;
        Refresh();
    }

    private void OnLootChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Filters_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (ResultsList is null) return; // fired during InitializeComponent

        string search = SearchBox.Text.Trim();
        string kindTag = (KindBox.SelectedValue as string) ?? "";

        var rows = new List<RowVm>();
        int matches = 0;
        foreach (var e in _loot.Entries)
        {
            if (kindTag.Length > 0 && e.Kind.ToString() != kindTag) continue;
            if (search.Length > 0
                && !e.Item.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !e.Mob.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !e.Zone.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            matches++;
            if (rows.Count < MaxRows) rows.Add(ToRow(e));
        }
        ResultsList.ItemsSource = rows;

        string vendored = LootTracker.FormatCoins(_loot.TotalVendorCopper);
        SummaryText.Text = $"{_loot.UpgradeCount} upgrades · vendored {vendored}";
        Title = matches > MaxRows
            ? $"EQL Assistant — Loot History ({matches} matches, showing {MaxRows})"
            : "EQL Assistant — Loot History";
    }

    private static RowVm ToRow(LootTracker.LootEntry e)
    {
        string when = e.When.Date == DateTime.Today
            ? e.When.ToString("HH:mm")
            : e.When.ToString("dd MMM HH:mm");
        string zone = e.Zone.Length > 0 ? $" · {e.Zone}" : "";
        string detail = $"{when} · from {e.Mob}{zone}";

        string item = e.Count > 1 ? $"{e.Count}× {e.Item}" : e.Item;
        return e.Kind switch
        {
            LootTracker.LootKind.Upgrade => new RowVm(item, detail, $"→ {e.Result}", UpgradeFg),
            LootTracker.LootKind.Sold => new RowVm(item, detail, $"+{LootTracker.FormatCoins(e.Copper)}", SoldFg),
            _ => new RowVm(item, detail, "kept", KeptFg),
        };
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
