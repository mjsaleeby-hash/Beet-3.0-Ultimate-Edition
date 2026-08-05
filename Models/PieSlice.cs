using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace BeetsBackup.Models;

/// <summary>
/// Data model for a single slice of the pie/donut chart in visual mode.
/// Contains both the geometric layout and display metadata.
/// </summary>
public sealed class PieSlice : INotifyPropertyChanged
{
    /// <summary>Display name (file or folder name, or "Other").</summary>
    public required string Name { get; init; }

    /// <summary>Emoji icon representing the item type.</summary>
    public required string Icon { get; init; }

    /// <summary>Absolute size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Human-readable size string (e.g. "1.5 GB").</summary>
    public required string SizeDisplay { get; init; }

    /// <summary>Percentage of total folder size this slice represents.</summary>
    public required double Percentage { get; init; }

    /// <summary>Starting angle in degrees (0 = 12 o'clock, clockwise).</summary>
    public required double StartAngle { get; init; }

    /// <summary>Arc sweep angle in degrees.</summary>
    public required double SweepAngle { get; init; }

    /// <summary>Fill color for the pie slice and legend swatch.</summary>
    public required Color FillColor { get; init; }

    /// <summary>Zero-based index in the slice collection, used for color cycling and hover matching.</summary>
    public int Index { get; init; }

    /// <summary>Full path to the file or folder, if navigable.</summary>
    public string? FullPath { get; init; }

    /// <summary>Whether this slice represents a directory (enables click-to-navigate).</summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// The smallest share of the total that still earns a drawn wedge.
    /// ONE constant feeds both the render decision (<see cref="IsRenderable"/>) and the
    /// legend's dimmed styling (<see cref="IsNegligible"/>). They were previously two
    /// separate numbers — a 0.1-degree sweep guard (0.028%) in PieChartControl and 0.05%
    /// here — so slices between them got a legend row with no wedge behind it.
    /// </summary>
    public const double MinVisiblePercentage = 0.05;

    /// <summary>Whether this slice is too small to render visibly.</summary>
    public bool IsNegligible => Percentage < MinVisiblePercentage;

    /// <summary>
    /// Whether the chart should draw a wedge for this slice. Deliberately trivial: its
    /// value is that the render decision has exactly one home, so the control cannot
    /// invent its own threshold again.
    /// </summary>
    public bool IsRenderable => !IsNegligible;

    private bool _isHighlighted;

    /// <summary>Whether the slice is currently highlighted via mouse hover.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted != value) { _isHighlighted = value; OnPropertyChanged(); } }
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
