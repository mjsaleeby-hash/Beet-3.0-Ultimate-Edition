using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

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
