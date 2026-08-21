using System.Windows;
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

    /// <summary>Optional tooltip line (used by the skill tracker rows).</summary>
    private string _detail = "";
    public string Detail { get => _detail; set => SetField(ref _detail, value); }

    /// <summary>Row margin — the pet's fold-out rows indent through it.</summary>
    private Thickness _margin = new(0, 0, 0, 3);
    public Thickness Margin { get => _margin; set => SetField(ref _margin, value); }

    /// <summary>Bar height: the TOTAL bars (you + pet, the pet's ranked row)
    /// stand taller than ability rows — the stay-alive-bars rule.</summary>
    private double _barHeight = 17;
    public double BarHeight { get => _barHeight; set => SetField(ref _barHeight, value); }

    /// <summary>True on the pet's ranked row: clicking it folds the pet's
    /// per-ability split in and out.</summary>
    private bool _isFold;
    public bool IsFold { get => _isFold; set => SetField(ref _isFold, value); }
}
