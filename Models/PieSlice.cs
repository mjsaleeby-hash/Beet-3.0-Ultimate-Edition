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

    /// <summary>Whether this slice is too small to render visibly (less than 0.05%).</summary>
    public bool IsNegligible => Percentage < 0.05;

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
