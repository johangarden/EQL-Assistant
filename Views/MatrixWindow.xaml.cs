using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EQLOverlay.Interop;
using EQLOverlay.Services;
using EQLOverlay.ViewModels;

namespace EQLOverlay.Views;

/// <summary>
/// A movable, transparent overlay panel that renders a present/missing matrix of
/// cells. Locking (click-through) and hiding are driven by the main overlay; this
/// window only manages its own position.
/// </summary>
public partial class MatrixWindow : Window
{
    private readonly string _panelKey;
    private readonly ConfigService _configService;
    private nint _hwnd;
    private bool _locked;

    private static readonly Brush UnlockedBackdrop =
        new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x0E, 0x14));

    /// <summary>Bound by the UniformGrid in XAML (read once at load).</summary>
    public int Columns { get; }

    public MatrixWindow(string panelKey, string title,
        ObservableCollection<MatrixCellViewModel> cells, int columns,
        ConfigService configService, double opacity)
    {
        Columns = columns < 1 ? 1 : columns;
        InitializeComponent();

        _panelKey = panelKey;
        _configService = configService;
        Title = "EQL Overlay — " + title;
        HeaderText.Text = title;
        CellsControl.ItemsSource = cells;
        Opacity = Math.Clamp(opacity <= 0 ? 1.0 : opacity, 0.1, 1.0);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += OnLocationChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var pos = _configService.LoadPanelPos(_panelKey);
        if (pos is { } p) { Left = p.Left; Top = p.Top; }
        else { Left = 60; Top = 420; } // default: below the main overlay
        EnsureOnScreen();
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

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) _configService.SavePanelPos(_panelKey, Left, Top);
    }

    /// <summary>Snap back onto the primary monitor (tray recovery).</summary>
    public void ResetPosition()
    {
        Left = SystemParameters.WorkArea.Left + 40;
        Top = SystemParameters.WorkArea.Top + 220;
        _configService.SavePanelPos(_panelKey, Left, Top);
    }

    private void EnsureOnScreen()
    {
        double vL = SystemParameters.VirtualScreenLeft;
        double vT = SystemParameters.VirtualScreenTop;
        double vR = vL + SystemParameters.VirtualScreenWidth;
        double vB = vT + SystemParameters.VirtualScreenHeight;
        const double keep = 60;
        if (Left < vL || Left > vR - keep || Top < vT || Top > vB - keep)
        {
            Left = Math.Clamp(Left, vL, Math.Max(vL, vR - keep));
            Top = Math.Clamp(Top, vT, Math.Max(vT, vB - keep));
        }
    }
}
