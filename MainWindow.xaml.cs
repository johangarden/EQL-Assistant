using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;
using EQLOverlay.ViewModels;
using EQLOverlay.Views;

namespace EQLOverlay;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly AlertService _alerts = new();
    private readonly LogBus _logBus = new();
    private AppConfig _config = new();
    private TriggerEngine _engine = null!;
    private OverlayViewModel _vm = null!;
    private LogWatcher? _watcher;
    private TriggerManagerWindow? _manager;
    private MatrixWindow? _selfMatrix;
    private MatrixWindow? _targetMatrix;
    private TimerWindow? _timer;
    private FlashWindow? _flash;
    private MeterWindow? _meter;
    private readonly CombatParser _combat = new();
    private PanelPlacement? _mainPlacement;
    private bool _hidden;
    private bool _timerHidden;
    private bool _meterHidden;
    private System.Windows.Forms.NotifyIcon? _tray;

    /// <summary>Set by self-tests so they never persist window position/lock.</summary>
    internal bool SuppressStatePersistence;

    private nint _hwnd;

    // Hotkey ids.
    private const int HK_LOCK    = 1;
    private const int HK_TEST    = 4;
    private const int HK_HIDE    = 5;
    private const int HK_MUTE    = 7;
    private const int HK_QUIT    = 9;
    private const int HK_REPOP   = 10;
    private const int HK_METER   = 11;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _config = _configService.LoadSettings();
            _configService.EnsureDefaultLoadout();
            LoadActiveLoadoutInto(_config);
            Log.Info($"Config loaded. loadout='{_config.ActiveLoadout}', triggers={_config.Triggers.Count}, " +
                     $"logDir='{_config.Log.Directory}', startLocked={_config.Overlay.StartLocked}");
        }
        catch (Exception ex)
        {
            Log.Error("Config load failed", ex);
            MessageBox.Show(ex.Message, "EQL Assistant — config problem",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _config = new AppConfig(); // run empty rather than crash
        }

        _alerts.Muted = _config.Overlay.Muted;
        _timerHidden = !_config.Overlay.TimerVisible;
        _meterHidden = !_config.Overlay.MeterVisible;
        _combat.SelfName = _config.CharacterName;
        _combat.PetName = _config.Overlay.PetName;
        _engine = new TriggerEngine(_config, _alerts);
        _engine.TimerRequested += OnTimerRequested;
        _engine.FlashRequested += OnFlashRequested;
        _vm = new OverlayViewModel(_engine, _config) { LoadoutName = _config.ActiveLoadout };
        DataContext = _vm;

        SizerHost.Width = _config.Overlay.Width;
        _mainPlacement = new PanelPlacement(this, _configService, "main", Anchor.TopLeft, 60, 140);
        _mainPlacement.Attach();

        // Start locked (game-ready) if configured — overrides the remembered state.
        if (_config.Overlay.StartLocked)
        {
            _vm.Locked = true;
            _config.Overlay.Locked = true;
        }

        ApplyOpacity();
        ApplyLockVisual();
        ApplyClickThrough(); // safe now that _vm exists (also called in OnSourceInitialized)
        SetupTrayIcon();
        StartWatcher();
        RebuildMatrixWindows();
        RebuildTimerWindow();
        RebuildMeterWindow();
        _flash = new FlashWindow();
        _flash.Show();
    }

    private void OnFlashRequested(string text, string color)
    {
        if (_hidden) return;
        _flash?.Flash(text, BrushFromColor(color));
    }

    private static Brush BrushFromColor(string color)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        catch { return new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x33)); }
    }

    // ---- timer panel --------------------------------------------------------

    private void RebuildTimerWindow()
    {
        if (_timer is not null) { try { _timer.Close(); } catch { /* ignore */ } }
        _timer = new TimerWindow(_configService, _alerts, _config.Overlay.TimerSeconds, _config.Overlay.Opacity,
            onDurationSet: s => { _config.Overlay.TimerSeconds = s; _configService.SaveSettings(_config); });
        _timer.PresetProvider = () => _config.Triggers
            .Where(t => t.Panel == Panels.TimerAuto && t.Enabled)
            .Select(t => (t.Name, t.DurationSeconds))
            .ToList();
        _timer.Show();
        UpdateTimerVisibility();
    }

    private void UpdateTimerVisibility()
    {
        if (_timer is not null)
            _timer.Visibility = (_hidden || _timerHidden) ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Show/hide the repop timer watch (⏱ button / tray / Ctrl+Alt+R), and remember it.</summary>
    private void ToggleTimer()
    {
        _timerHidden = !_timerHidden;
        _config.Overlay.TimerVisible = !_timerHidden;
        _configService.SaveSettings(_config);
        if (_hidden && !_timerHidden) ToggleHide(); // unhide everything if it was globally hidden
        UpdateTimerVisibility();
        _vm.Flash(_timerHidden ? "Repop timer hidden." : "Repop timer shown.");
    }

    // ---- DPS meter panel ----------------------------------------------------

    private void RebuildMeterWindow()
    {
        if (_meter is not null) { try { _meter.Close(); } catch { /* ignore */ } }
        _meter = new MeterWindow(_configService, _combat, _config.Overlay.Opacity);
        _meter.Show();
        UpdateMeterVisibility();
    }

    private void UpdateMeterVisibility()
    {
        if (_meter is not null)
            _meter.Visibility = (_hidden || _meterHidden) ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Show/hide the DPS meter (toolbar button / tray / Ctrl+Alt+D), and remember it.</summary>
    private void ToggleMeter()
    {
        _meterHidden = !_meterHidden;
        _config.Overlay.MeterVisible = !_meterHidden;
        _configService.SaveSettings(_config);
        if (_hidden && !_meterHidden) ToggleHide(); // unhide everything if it was globally hidden
        UpdateMeterVisibility();
        _vm.Flash(_meterHidden ? "DPS meter hidden." : "DPS meter shown.");
    }

    // ---- matrix panels ------------------------------------------------------

    private void RebuildMatrixWindows()
    {
        _selfMatrix = RebuildPanel(_selfMatrix, "selfMatrix", "Self Buffs",
            _engine.SelfCells, defaultLeft: 60, defaultTop: 420);
        _targetMatrix = RebuildPanel(_targetMatrix, "targetDebuffs", "Target Debuffs",
            _engine.TargetCells, defaultLeft: 420, defaultTop: 420);
        UpdateMatrixVisibility();
    }

    private MatrixWindow RebuildPanel(MatrixWindow? existing, string key, string title,
        System.Collections.ObjectModel.ObservableCollection<ViewModels.MatrixCellViewModel> cells,
        double defaultLeft, double defaultTop)
    {
        if (existing is not null) { try { existing.Close(); } catch { /* ignore */ } }
        var w = new MatrixWindow(key, title, cells, _config.Overlay.MatrixColumns,
            _configService, _config.Overlay.Opacity, defaultLeft, defaultTop);
        w.Show();
        w.SetLocked(_vm.Locked);
        return w;
    }

    private void UpdateMatrixVisibility()
    {
        SetPanelVisible(_selfMatrix, _engine.SelfCells.Count);
        SetPanelVisible(_targetMatrix, _engine.TargetCells.Count);
    }

    private void SetPanelVisible(MatrixWindow? w, int cellCount)
    {
        if (w is null) return;
        w.Visibility = (!_hidden && cellCount > 0) ? Visibility.Visible : Visibility.Hidden;
    }

    private void ApplyOpacity() =>
        Opacity = Math.Clamp(_config.Overlay.Opacity <= 0 ? 1.0 : _config.Overlay.Opacity, 0.1, 1.0);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        ApplyClickThrough();
        RegisterHotKeys();
    }

    // ---- log watcher --------------------------------------------------------

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = new LogWatcher(
            _config.Log,
            onLine: line => Dispatcher.BeginInvoke(() =>
            {
                _engine.ProcessLine(line);
                _combat.ProcessLine(line);
                _logBus.Publish(line);
            }),
            onStatus: msg => Dispatcher.BeginInvoke(() => _vm.LogStatus = msg));
        _watcher.Start();
    }

    // ---- hotkeys ------------------------------------------------------------

    private void RegisterHotKeys()
    {
        uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        NativeMethods.RegisterHotKey(_hwnd, HK_LOCK,   mods, 0x4C); // L
        NativeMethods.RegisterHotKey(_hwnd, HK_TEST,   mods, 0x54); // T
        NativeMethods.RegisterHotKey(_hwnd, HK_HIDE,   mods, 0x48); // H
        NativeMethods.RegisterHotKey(_hwnd, HK_MUTE,   mods, 0x53); // S
        NativeMethods.RegisterHotKey(_hwnd, HK_QUIT,   mods, 0x51); // Q
        NativeMethods.RegisterHotKey(_hwnd, HK_REPOP,  mods, 0x52); // R
        NativeMethods.RegisterHotKey(_hwnd, HK_METER,  mods, 0x44); // D
    }

    private void UnregisterHotKeys()
    {
        foreach (int id in new[] { HK_LOCK, HK_TEST, HK_HIDE, HK_MUTE, HK_QUIT, HK_REPOP, HK_METER })
            NativeMethods.UnregisterHotKey(_hwnd, id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HK_LOCK:   ToggleLock();       handled = true; break;
                case HK_TEST:   _engine.AddDemoTimer(); _engine.AddDemoMatrixCell(); _engine.AddDemoTargetCell(); _combat.AddDemoFight(); UpdateMatrixVisibility(); handled = true; break;
                case HK_HIDE:   ToggleHide();       handled = true; break;
                case HK_MUTE:   ToggleMute();       handled = true; break;
                case HK_QUIT:   Close();            handled = true; break;
                case HK_REPOP:  ToggleTimer();      handled = true; break;
                case HK_METER:  ToggleMeter();      handled = true; break;
            }
        }
        return nint.Zero;
    }

    // ---- lock / click-through -----------------------------------------------

    private void ToggleLock()
    {
        _vm.Locked = !_vm.Locked;
        _config.Overlay.Locked = _vm.Locked;
        ApplyClickThrough();
        ApplyLockVisual();
        _selfMatrix?.SetLocked(_vm.Locked);
        _targetMatrix?.SetLocked(_vm.Locked);
        _configService.SaveWindowState(_config.Overlay);
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero && _vm is not null)
            NativeMethods.SetClickThrough(_hwnd, _vm.Locked);
    }

    private void ApplyLockVisual() =>
        RootBorder.Background = _vm.Locked ? Brushes.Transparent : UnlockedBackdrop;

    private void ToggleHide()
    {
        _hidden = !_hidden;
        Visibility = _hidden ? Visibility.Hidden : Visibility.Visible;
        UpdateMatrixVisibility();
        UpdateTimerVisibility();
        UpdateMeterVisibility();
    }

    /// <summary>Home every panel to its default corner (tray recovery).</summary>
    private void ResetPosition()
    {
        _hidden = false;
        Visibility = Visibility.Visible;
        Topmost = true;
        Activate();

        _mainPlacement?.ResetToDefault();
        _selfMatrix?.ResetPosition();
        _targetMatrix?.ResetPosition();
        _timer?.ResetPosition();
        _meter?.ResetPosition();
        UpdateMatrixVisibility();
        UpdateTimerVisibility();
        UpdateMeterVisibility();
        _vm?.Flash("Panels reset to their corners.");
    }

    // ---- config -------------------------------------------------------------

    private void OpenManager()
    {
        if (_manager is null)
        {
            _manager = new TriggerManagerWindow(_configService, _config, _logBus, _alerts, OnManagerApplied);
            _manager.Closed += (_, _) => _manager = null;
            _manager.Show();
        }
        BringToFront(_manager);
    }

    /// <summary>
    /// Force a window to the foreground. The overlay is a no-activate topmost
    /// window, so a child window it opens won't come to front on its own — the
    /// brief Topmost toggle bumps it above everything, then releases.
    /// </summary>
    private static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    /// <summary>Load the active loadout's (compiled) triggers into <paramref name="cfg"/>.</summary>
    private void LoadActiveLoadoutInto(AppConfig cfg)
    {
        Loadout? lo = null;
        try { lo = _configService.LoadLoadout(cfg.ActiveLoadout); }
        catch (Exception ex)
        {
            MessageBox.Show($"Loadout '{cfg.ActiveLoadout}' has an invalid pattern:\n{ex.Message}",
                "EQL Assistant", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (lo is null)
        {
            foreach (var candidate in _configService.ListLoadouts())
            {
                try { lo = _configService.LoadLoadout(candidate.Name); if (lo is not null) break; }
                catch { /* skip a broken loadout */ }
            }
        }

        if (lo is not null)
        {
            cfg.Triggers = lo.Triggers;
            cfg.ActiveLoadout = lo.Name;
        }
    }

    /// <summary>Apply settings + active loadout saved from the manager, live.</summary>
    private void OnManagerApplied(string activeName)
    {
        AppConfig cfg;
        try { cfg = _configService.LoadSettings(); }
        catch { cfg = _config; }

        // Keep the live window position/lock rather than the persisted values.
        cfg.Overlay.Left = Left;
        cfg.Overlay.Top = Top;
        cfg.Overlay.Locked = _vm.Locked;
        cfg.ActiveLoadout = activeName;
        LoadActiveLoadoutInto(cfg);

        _config = cfg;
        _alerts.Muted = cfg.Overlay.Muted;
        _timerHidden = !cfg.Overlay.TimerVisible;
        _meterHidden = !cfg.Overlay.MeterVisible;
        _combat.SelfName = cfg.CharacterName;
        _combat.PetName = cfg.Overlay.PetName;

        bool wasLocked = _vm.Locked;
        _engine.Reset();
        _engine.UpdateConfig(cfg);

        // Rebuild the VM so sizing/header changes take effect immediately.
        _vm = new OverlayViewModel(_engine, cfg)
        {
            Locked = wasLocked,
            LoadoutName = cfg.ActiveLoadout,
        };
        DataContext = _vm;

        SizerHost.Width = cfg.Overlay.Width;
        ApplyOpacity();
        ApplyLockVisual();
        StartWatcher();
        _mainPlacement?.Reload();   // pick up any anchor change from Settings
        RebuildMatrixWindows();
        RebuildTimerWindow();
        RebuildMeterWindow();
        _vm.Flash("Settings applied.");
        Log.Info($"Settings applied from manager. loadout='{cfg.ActiveLoadout}', triggers={cfg.Triggers.Count}, " +
                 $"selfCells={_engine.SelfCells.Count}");
    }

    private void ApplyLoadout(string name)
    {
        Loadout? lo;
        try { lo = _configService.LoadLoadout(name); }
        catch { _vm.Flash($"Loadout '{name}' has a bad pattern."); return; }
        if (lo is null) return;

        _config.Triggers = lo.Triggers;
        _config.ActiveLoadout = lo.Name;
        _configService.SaveSettings(_config); // remember the choice

        _engine.Reset();
        _engine.UpdateConfig(_config);
        UpdateMatrixVisibility();
        _vm.LoadoutName = lo.Name;
        _vm.Flash($"Switched to: {lo.Name}");
        Log.Info($"Loadout switched to '{lo.Name}' ({lo.Triggers.Count} triggers)");
    }

    // ---- repop / respawn timer ----------------------------------------------

    private void OnRepop(object sender, RoutedEventArgs e) => ToggleTimer();

    private void OnMeter(object sender, RoutedEventArgs e) => ToggleMeter();

    /// <summary>A "timerAuto" trigger matched (e.g. a named mob death) — start the watch.</summary>
    private void OnTimerRequested(double seconds, string name)
    {
        if (_timerHidden)
        {
            _timerHidden = false;
            _config.Overlay.TimerVisible = true;
            _configService.SaveSettings(_config);
        }
        if (_hidden) ToggleHide();
        UpdateTimerVisibility();
        _timer?.StartWith(seconds, name);
        _vm.Flash($"{name} down — repop timer started.");
        Log.Info($"Auto-started repop timer ({seconds:0}s) from trigger '{name}'.");
    }

    private void ToggleMute()
    {
        _alerts.Muted = !_alerts.Muted;
        _config.Overlay.Muted = _alerts.Muted;
        _vm.Muted = _alerts.Muted;
        _vm.Flash(_alerts.Muted ? "Alerts muted." : "Alerts on.");
    }

    private void OpenConfigFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _configService.ConfigDirectory,
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }

    // ---- toolbar buttons ----------------------------------------------------

    private void Toolbar_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_vm.Locked)
            DragMove();
    }

    private void OnManage(object sender, RoutedEventArgs e) => OpenManager();
    private void OnLock(object sender, RoutedEventArgs e) => ToggleLock();
    private void OnQuit(object sender, RoutedEventArgs e) => Close();

    /// <summary>Test hook: add a demo bar so a self-test can force the bar template to render.</summary>
    internal void AddDemoForTest() => _engine?.AddDemoTimer();

    /// <summary>Open a popup menu to pick a loadout directly (toolbar dropdown).</summary>
    private void OnLoadoutMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var name in _configService.ListLoadouts().Select(l => l.Name))
        {
            var item = new MenuItem
            {
                Header = name,
                IsChecked = string.Equals(name, _config.ActiveLoadout, StringComparison.OrdinalIgnoreCase),
            };
            string captured = name;
            item.Click += (_, _) => ApplyLoadout(captured);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- system tray --------------------------------------------------------

    private void SetupTrayIcon()
    {
        if (_tray is not null) return;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "EQL Assistant",
            Visible = true,
            Icon = LoadAppIcon(),
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleHide());
        menu.Items.Add("Lock / Unlock", null, (_, _) => ToggleLock());

        var loadoutItem = new System.Windows.Forms.ToolStripMenuItem("Loadout");
        loadoutItem.DropDownOpening += (_, _) =>
        {
            loadoutItem.DropDownItems.Clear();
            foreach (var name in _configService.ListLoadouts().Select(l => l.Name))
            {
                string captured = name;
                var mi = new System.Windows.Forms.ToolStripMenuItem(name)
                {
                    Checked = string.Equals(name, _config.ActiveLoadout, StringComparison.OrdinalIgnoreCase),
                };
                mi.Click += (_, _) => ApplyLoadout(captured);
                loadoutItem.DropDownItems.Add(mi);
            }
        };
        menu.Items.Add(loadoutItem);

        menu.Items.Add("Show / hide repop timer", null, (_, _) => ToggleTimer());
        menu.Items.Add("Show / hide DPS meter", null, (_, _) => ToggleMeter());
        menu.Items.Add("Manage…", null, (_, _) => OpenManager());
        menu.Items.Add("Mute / Unmute", null, (_, _) => ToggleMute());
        menu.Items.Add("Open config folder", null, (_, _) => OpenConfigFolder());
        menu.Items.Add("Reset position", null, (_, _) => ResetPosition());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Close());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleHide();
    }

    /// <summary>The app's embedded exe icon (falls back to a drawn one).</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through to the drawn icon */ }
        return CreateTrayIcon();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(24, 30, 42));
            using var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(79, 195, 247));
            g.FillRectangle(blue, 2, 4, 12, 3);
            using var green = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(129, 199, 132));
            g.FillRectangle(green, 2, 9, 8, 3);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    // ---- persistence / teardown ---------------------------------------------


    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (!SuppressStatePersistence)
            {
                _config.Overlay.Left = Left;
                _config.Overlay.Top = Top;
                _configService.SaveWindowState(_config.Overlay);
            }
        }
        catch { /* ignore */ }

        Log.Info("Shutting down");
        UnregisterHotKeys();
        _watcher?.Dispose();
        try { _selfMatrix?.Close(); } catch { /* ignore */ }
        try { _targetMatrix?.Close(); } catch { /* ignore */ }
        try { _timer?.Close(); } catch { /* ignore */ }
        try { _meter?.Close(); } catch { /* ignore */ }
        try { _flash?.Close(); } catch { /* ignore */ }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }
}
