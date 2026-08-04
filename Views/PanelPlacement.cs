using System.Windows;
using EQLOverlay.Models;
using EQLOverlay.Services;

namespace EQLOverlay.Views;

/// <summary>
/// Keeps a window pinned to a chosen screen corner: position is stored as an
/// offset from that corner of the (primary) work area, so the panel survives
/// resolution changes and grows *away* from the anchor (bottom anchors grow up,
/// right anchors grow left) as its content resizes.
/// </summary>
public sealed class PanelPlacement
{
    private readonly Window _window;
    private readonly ConfigService _config;
    private readonly string _key;
    private readonly Anchor _defaultAnchor;
    private readonly double _defOffX;
    private readonly double _defOffY;

    private Anchor _anchor;
    private double _offX;
    private double _offY;
    private bool _applying;

    public PanelPlacement(Window window, ConfigService config, string key,
        Anchor defaultAnchor, double defOffX, double defOffY)
    {
        _window = window;
        _config = config;
        _key = key;
        _defaultAnchor = defaultAnchor;
        _defOffX = defOffX;
        _defOffY = defOffY;
    }

    /// <summary>Call from the window's Loaded handler.</summary>
    public void Attach()
    {
        LoadState();
        Apply();
        _window.SizeChanged += (_, _) => Apply();          // keep anchored edge fixed as content grows
        _window.LocationChanged += (_, _) => { if (!_applying) SaveFromCurrent(); };
    }

    /// <summary>Re-read persisted anchor/offset (e.g. after Settings changed it) and reposition.</summary>
    public void Reload()
    {
        LoadState();
        Apply();
    }

    /// <summary>Home the panel to its default anchor + offset.</summary>
    public void ResetToDefault()
    {
        _anchor = _defaultAnchor;
        _offX = _defOffX;
        _offY = _defOffY;
        Apply();
        SaveFromCurrent();
    }

    private void LoadState()
    {
        var p = _config.LoadPlacement(_key);
        if (p is not null)
        {
            _anchor = p.Anchor; _offX = p.OffX; _offY = p.OffY;
            return;
        }

        // Legacy {left,top} file → treat as a TopLeft offset so old positions carry over.
        var legacy = _config.LoadPanelPos(_key);
        var wa = SystemParameters.WorkArea;
        if (legacy is { } l)
        {
            _anchor = Anchor.TopLeft; _offX = l.Left - wa.Left; _offY = l.Top - wa.Top;
        }
        else
        {
            _anchor = _defaultAnchor; _offX = _defOffX; _offY = _defOffY;
        }
    }

    private void Apply()
    {
        double w = _window.ActualWidth, h = _window.ActualHeight;
        if (w <= 0 || h <= 0) return; // not laid out yet; SizeChanged will re-run this

        var wa = SystemParameters.WorkArea;
        double left = _anchor is Anchor.TopLeft or Anchor.BottomLeft
            ? wa.Left + _offX
            : wa.Right - _offX - w;
        double top = _anchor is Anchor.TopLeft or Anchor.TopRight
            ? wa.Top + _offY
            : wa.Bottom - _offY - h;

        // Keep at least a sliver on the virtual desktop.
        double vL = SystemParameters.VirtualScreenLeft, vT = SystemParameters.VirtualScreenTop;
        double vR = vL + SystemParameters.VirtualScreenWidth, vB = vT + SystemParameters.VirtualScreenHeight;
        const double keep = 60;
        left = Math.Clamp(left, vL, Math.Max(vL, vR - keep));
        top = Math.Clamp(top, vT, Math.Max(vT, vB - keep));

        _applying = true;
        _window.Left = left;
        _window.Top = top;
        _applying = false;
    }

    private void SaveFromCurrent()
    {
        double w = _window.ActualWidth, h = _window.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var wa = SystemParameters.WorkArea;
        _offX = _anchor is Anchor.TopLeft or Anchor.BottomLeft
            ? _window.Left - wa.Left
            : wa.Right - (_window.Left + w);
        _offY = _anchor is Anchor.TopLeft or Anchor.TopRight
            ? _window.Top - wa.Top
            : wa.Bottom - (_window.Top + h);

        _config.SavePlacement(_key, _anchor, _offX, _offY);
    }
}
