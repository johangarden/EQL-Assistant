using System.Windows.Media;

namespace EQLOverlay.ViewModels;

/// <summary>One ranked attacker/healer row in the DPS meter.</summary>
public sealed class MeterRowViewModel : ViewModelBase
{
    private string _name = "";
    public string Name { get => _name; set => SetField(ref _name, value); }

    /// <summary>"dps (total, %)" right-hand label.</summary>
    private string _valueText = "";
    public string ValueText { get => _valueText; set => SetField(ref _valueText, value); }

    /// <summary>Bar length relative to the top source (0..1).</summary>
    private double _fraction;
    public double Fraction { get => _fraction; set => SetField(ref _fraction, value); }

    private Brush _fill = Brushes.DodgerBlue;
    public Brush Fill { get => _fill; set => SetField(ref _fill, value); }
}
