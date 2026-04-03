using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace BeetsBackup.Models;

public class PieSlice : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public required long SizeBytes { get; init; }
    public required string SizeDisplay { get; init; }
    public required double Percentage { get; init; }
    public required double StartAngle { get; init; }
    public required double SweepAngle { get; init; }
    public required Color FillColor { get; init; }
    public int Index { get; init; }
    public string? FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsNegligible => Percentage < 0.05;

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted != value) { _isHighlighted = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
