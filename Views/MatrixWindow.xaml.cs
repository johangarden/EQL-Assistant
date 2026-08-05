using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Models;
using EQLOverlay.Services;
using EQLOverlay.ViewModels;

namespace EQLOverlay.Views;

/// <summary>
/// A movable, transparent overlay panel that renders a present/missing matrix of
/// cells. Locking (click-through) and hiding are driven by the main overlay; a
/// <see cref="PanelPlacement"/> keeps it anchored to its chosen screen corner.
/// </summary>
public partial class MatrixWindow : Window
{
    private readonly PanelPlacement _placement;
    private nint _hwnd;
    private bool _locked;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    /// <summary>Bound by the UniformGrid in XAML (read once at load).</summary>
    public int Columns { get; }

    public MatrixWindow(string panelKey, string title,
        ObservableCollection<MatrixCellViewModel> cells, int columns,
        ConfigService configService, double opacity,
        double defaultLeft, double defaultTop)
    {
        Columns = columns < 1 ? 1 : columns;
        InitializeComponent();

        Title = "EQL Assistant — " + title;
        HeaderText.Text = title;
        CellsControl.ItemsSource = cells;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);

        _placement = new PanelPlacement(this, configService, panelKey,
            Anchor.TopLeft, defaultLeft, defaultTop);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _placement.Attach();
        ApplyLockVisual();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyClickThrough();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyClickThrough();
        ApplyLockVisual();
    }

    /// <summary>Re-read the persisted anchor/offset (after Settings changed it).</summary>
    public void ReloadPlacement() => _placement.Reload();

    /// <summary>Home the panel back to its default corner (tray recovery).</summary>
    public void ResetPosition() => _placement.ResetToDefault();

    private void ApplyClickThrough()
    {
        if (_hwnd != nint.Zero)
            NativeMethods.SetClickThrough(_hwnd, _locked);
    }

    private void ApplyLockVisual()
    {
        Header.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        RootBorder.Background = _locked ? Brushes.Transparent : UnlockedBackdrop;
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_locked)
            DragMove();
    }
}
