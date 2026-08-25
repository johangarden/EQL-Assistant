using System.Windows.Media;

namespace EQLOverlay.ViewModels;

/// <summary>A small secondary repop timer shown under the main watch.</summary>
public sealed class SecondaryTimerViewModel : ViewModelBase
{
    public SecondaryTimerViewModel(string name) => Name = name;

    public string Name { get; }

    private string _remainingText = "";
    public string RemainingText
    {
        get => _remainingText;
        set => SetField(ref _remainingText, value);
    }

    private Brush _foreground = Brushes.White;
    public Brush Foreground
    {
        get => _foreground;
        set => SetField(ref _foreground, value);
    }

    /// <summary>Zone-scoping: a row for a mob in another zone collapses —
    /// the clock keeps running, only the display follows the player.</summary>
    private System.Windows.Visibility _rowVis = System.Windows.Visibility.Visible;
    public System.Windows.Visibility RowVis
    {
        get => _rowVis;
        set => SetField(ref _rowVis, value);
    }
}
