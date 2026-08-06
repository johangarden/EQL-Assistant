using System.Windows;
using System.Windows.Controls;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Browse the prebaked spell library and add ready-made triggers with one
/// click. Owned by the Manager; additions land in the current loadout.
/// </summary>
public partial class SpellLibraryWindow : Window
{
    private const int MaxRows = 250;

    private readonly SpellLibrary _library;
    private readonly Action<TriggerDefinition> _onAdd;

    public sealed record SpellRow(SpellLibrary.Spell Spell, string Name, string Detail,
        string SeenBadge, bool CanBar, bool CanFade);

    public SpellLibraryWindow(SpellLibrary library, Action<TriggerDefinition> onAdd)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _library = library;
        _onAdd = onAdd;

        ClassBox.ItemsSource = new[]
        {
            "All classes", "WAR", "CLR", "PAL", "RNG", "SHD", "DRU", "MNK", "BRD",
            "ROG", "SHM", "NEC", "WIZ", "MAG", "ENC", "BST", "BER",
        };
        ClassBox.SelectedIndex = 0;

        Refresh();
    }

    private void Filters_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (ResultsList is null) return; // during InitializeComponent

        string filter = FilterBox?.SelectedValue as string ?? "";
        string cls = ClassBox?.SelectedIndex > 0 ? (string)ClassBox.SelectedItem : "";
        var hits = _library.Search(SearchBox?.Text ?? "", filter, cls);

        ResultsList.ItemsSource = hits.Take(MaxRows).Select(ToRow).ToList();
        CountText.Text = hits.Count > MaxRows
            ? $"{hits.Count} matches (showing {MaxRows}) · {_library.SeenCount} seen in your log"
            : $"{hits.Count} of {_library.Spells.Count} spells · {_library.SeenCount} seen in your log";
    }

    private SpellRow ToRow(SpellLibrary.Spell s)
    {
        var bits = new List<string>();
        if (s.Classes.Length > 0) bits.Add(s.Classes);
        if (s.DurationSec > 0) bits.Add(FormatDuration(s.DurationSec));
        bits.Add(s.Illusion ? "Illusion" : s.Type.Length > 0 ? s.Type : s.Bucket);
        return new SpellRow(s, s.Name, string.Join("  ·  ", bits),
            _library.IsSeen(s) ? "● seen" : "",
            CanBar: s.CastOnYou.Length > 0,
            CanFade: s.WearsOff.Length > 0);
    }

    private static string FormatDuration(double sec) =>
        sec >= 3600 ? $"{sec / 3600:0.#}h" : sec >= 60 ? $"{sec / 60:0} min" : $"{sec:0}s";

    // ---- add actions ----------------------------------------------------------

    private void AddBar_Click(object sender, RoutedEventArgs e) => Add(sender, s => SpellLibrary.BarTrigger(s, spokenWarning: false));
    private void AddBarVoice_Click(object sender, RoutedEventArgs e) => Add(sender, s => SpellLibrary.BarTrigger(s, spokenWarning: true));
    private void AddFade_Click(object sender, RoutedEventArgs e) => Add(sender, SpellLibrary.FadeFlashTrigger);

    private void Add(object sender, Func<SpellLibrary.Spell, TriggerDefinition?> build)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpellRow row) return;
        var def = build(row.Spell);
        if (def is null) return;
        _onAdd(def);
    }
}
