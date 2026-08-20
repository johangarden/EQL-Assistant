using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EQLOverlay.Interop;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// The app's command strip, detached from the buff bars: always visible and
/// clickable whether the overlay is locked or not (the padlock governs the
/// other panels). DataContext is the shared OverlayViewModel. All actions are
/// forwarded to MainWindow via the callbacks below.
/// </summary>
public partial class ToolbarWindow : Window
{
    private readonly PanelPlacement _placement;

    public Action? QuitRequested { get; set; }
    public Action? LockRequested { get; set; }
    public Action? MuteRequested { get; set; }
    public Action? ManageRequested { get; set; }
    public Action<object>? MenuRequested { get; set; }
    public Action? RaidRequested { get; set; }
    public Action? QuestsRequested { get; set; }
    public Action? LootRequested { get; set; }
    public Action? SheetRequested { get; set; }

    public ToolbarWindow(ConfigService config)
    {
        InitializeComponent();
        _placement = new PanelPlacement(this, config, "toolbar", Models.Anchor.TopLeft, 60, 60);
        Loaded += (_, _) => _placement.Attach();
        SourceInitialized += (_, _) =>
            // Interactive (never click-through) but no-activate, so clicking it
            // doesn't steal focus from the game.
            NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, false);
    }

    public void ResetPosition() => _placement.ResetToDefault();
    public void ReloadPlacement() => _placement.Reload();

    private void Root_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
    private void OnLock(object sender, RoutedEventArgs e) => LockRequested?.Invoke();
    private void OnMute(object sender, RoutedEventArgs e) => MuteRequested?.Invoke();
    private void OnManage(object sender, RoutedEventArgs e) => ManageRequested?.Invoke();
    private void OnMenu(object sender, RoutedEventArgs e) => MenuRequested?.Invoke(sender);
    private void OnRaid(object sender, RoutedEventArgs e) => RaidRequested?.Invoke();
    private void OnQuests(object sender, RoutedEventArgs e) => QuestsRequested?.Invoke();
    private void OnLoot(object sender, RoutedEventArgs e) => LootRequested?.Invoke();
    private void OnSheet(object sender, RoutedEventArgs e) => SheetRequested?.Invoke();
}
