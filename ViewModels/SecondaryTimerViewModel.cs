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
}
