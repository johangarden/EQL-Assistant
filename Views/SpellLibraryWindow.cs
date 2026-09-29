using System.Windows;
using System.Windows.Controls;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The trigger editor's Library window: a shell around <see cref="SpellLibraryPanel"/>.
/// Since 29 Sep it shows Durations only — Efficiency and Invocations live in
/// the Tools window — unless a caller (selftests, renders) asks for more tabs.
/// </summary>
public sealed class SpellLibraryWindow : Window
{
    public SpellLibraryPanel Panel { get; }

    public SpellLibraryWindow(SpellLibrary library, Action<TriggerDefinition> onAdd,
        SpellDurations? durations = null, SpellYield? yield = null,
        Func<string>? classesProvider = null, Func<int>? levelProvider = null, string? viewStatePath = null,
        Func<string>? snapshotText = null, Func<string?>? logPath = null, IReadOnlyList<string>? tabs = null)
    {
        Title = "EQL Assistant — Spell Library";
        Width = 820; Height = 600; MinWidth = 620; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/assets/eqloverlay.ico"));
        UseLayoutRounding = true;
        Panel = new SpellLibraryPanel(library, onAdd, durations, yield, classesProvider, levelProvider, viewStatePath, snapshotText, logPath, tabs);
        Panel.TriggerAdded += Close;
        Content = Panel;
        WindowTheme.ApplyDark(this);
    }

    // Selftest / render forwards — the panel owns the behaviour.
    public void ShowTab(string tab) => Panel.ShowTab(tab);
    internal void SearchForTest(string text) => Panel.SearchForTest(text);
    internal List<string> EffectTexts => Panel.EffectTexts;
    internal List<SpellEfficiency.Row> EffRowsForTest => Panel.EffRowsForTest;
    internal void EffSetForTest(bool? healing = null, string? resist = null, string? classes = null) => Panel.EffSetForTest(healing, resist, classes);
    internal string WhoLine => Panel.WhoLine;
    internal int BandForTest => Panel.BandForTest;
    internal List<string> VerdictTexts => Panel.VerdictTexts;
    internal void InvUseLinesForTest(List<string> lines, DateTime now, int regen, int pool) => Panel.InvUseLinesForTest(lines, now, regen, pool);
    internal InvocationPlanner.Result? InvResultForTest => Panel.InvResultForTest;
    internal TextBox? InvRegenBoxForTest => Panel.InvRegenBoxForTest;
}
