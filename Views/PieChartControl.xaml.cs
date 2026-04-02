using System.Collections.ObjectModel;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BeetsBackup.Models;

namespace BeetsBackup.Views;

public partial class PieChartControl : UserControl
{
    private readonly List<Path> _slicePaths = new();

    /// <summary>
    /// Raised when a pie slice or legend item is clicked.
    /// </summary>
    public event EventHandler<PieSlice>? SliceClicked;

    public static readonly DependencyProperty SlicesProperty =
        DependencyProperty.Register(nameof(Slices), typeof(ObservableCollection<PieSlice>),
            typeof(PieChartControl), new PropertyMetadata(null, OnSlicesChanged));

    public ObservableCollection<PieSlice>? Slices
    {
        get => (ObservableCollection<PieSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public static readonly DependencyProperty TotalSizeProperty =
        DependencyProperty.Register(nameof(TotalSize), typeof(string),
            typeof(PieChartControl), new PropertyMetadata("", OnTotalSizeChanged));

    public string TotalSize
    {
        get => (string)GetValue(TotalSizeProperty);
        set => SetValue(TotalSizeProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string),
            typeof(PieChartControl), new PropertyMetadata("", OnSubtitleChanged));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty IsCalculatingProperty =
        DependencyProperty.Register(nameof(IsCalculating), typeof(bool),
            typeof(PieChartControl), new PropertyMetadata(false, OnIsCalculatingChanged));

    public bool IsCalculating
    {
        get => (bool)GetValue(IsCalculatingProperty);
        set => SetValue(IsCalculatingProperty, value);
    }

    private static void OnIsCalculatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PieChartControl)d;
        var calculating = (bool)e.NewValue;
        var hasSlices = control.Slices != null && control.Slices.Count > 0;
        if (calculating && !hasSlices)
        {
            control.ChartContent.Visibility = Visibility.Visible;
            control.SelectDriveOverlay.Visibility = Visibility.Collapsed;
            control.CalculatingOverlay.Visibility = Visibility.Visible;
        }
        else if (!calculating && !hasSlices)
        {
            control.ChartContent.Visibility = Visibility.Collapsed;
            control.SelectDriveOverlay.Visibility = Visibility.Visible;
            control.CalculatingOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            control.CalculatingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    public PieChartControl()
    {
        InitializeComponent();
    }

    private static void OnSlicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PieChartControl)d;
        control.RebuildChart();
    }

    private static void OnTotalSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PieChartControl)d;
        control.TotalSizeText.Text = e.NewValue as string ?? "";
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PieChartControl)d;
        control.SubtitleText.Text = e.NewValue as string ?? "";
    }

    private void RebuildChart()
    {
        // Unsubscribe event handlers from old paths to avoid leaks
        foreach (var oldPath in _slicePaths)
        {
            oldPath.MouseEnter -= Slice_MouseEnter;
            oldPath.MouseLeave -= Slice_MouseLeave;
            oldPath.MouseLeftButtonUp -= Slice_Click;
        }

        PieCanvas.Children.Clear();
        _slicePaths.Clear();

        var slices = Slices;
        if (slices == null || slices.Count == 0)
        {
            LegendList.ItemsSource = null;
            if (IsCalculating)
            {
                ChartContent.Visibility = Visibility.Visible;
                SelectDriveOverlay.Visibility = Visibility.Collapsed;
                CalculatingOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                ChartContent.Visibility = Visibility.Collapsed;
                SelectDriveOverlay.Visibility = Visibility.Visible;
                CalculatingOverlay.Visibility = Visibility.Collapsed;
            }
            return;
        }

        ChartContent.Visibility = Visibility.Visible;
        SelectDriveOverlay.Visibility = Visibility.Collapsed;
        CalculatingOverlay.Visibility = Visibility.Collapsed;
        LegendList.ItemsSource = slices;

        const double cx = 140, cy = 140, radius = 120;

        // Draw a subtle background circle
        var bgCircle = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = TryFindResource("PanelBrush") as Brush ?? Brushes.DarkGray,
            Opacity = 0.5
        };
        Canvas.SetLeft(bgCircle, cx - radius);
        Canvas.SetTop(bgCircle, cy - radius);
        PieCanvas.Children.Add(bgCircle);

        foreach (var slice in slices)
        {
            var path = CreateSlicePath(slice, cx, cy, radius);
            path.Tag = slice;
            path.MouseEnter += Slice_MouseEnter;
            path.MouseLeave += Slice_MouseLeave;
            path.MouseLeftButtonUp += Slice_Click;
            _slicePaths.Add(path);
            PieCanvas.Children.Add(path);
        }

        // Draw a center hole to make it a donut chart
        var hole = new Ellipse
        {
            Width = 100,
            Height = 100,
            Fill = TryFindResource("SurfaceBrush") as Brush ?? Brushes.Black
        };
        Canvas.SetLeft(hole, cx - 50);
        Canvas.SetTop(hole, cy - 50);
        PieCanvas.Children.Add(hole);
    }

    private static Path CreateSlicePath(PieSlice slice, double cx, double cy, double radius)
    {
        var path = new Path
        {
            Fill = new SolidColorBrush(slice.FillColor),
            Stroke = Brushes.Transparent,
            StrokeThickness = 1,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };

        if (slice.SweepAngle >= 359.9)
        {
            // Full circle
            path.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
            return path;
        }

        if (slice.SweepAngle < 0.1)
            return path;

        double startRad = (slice.StartAngle - 90) * Math.PI / 180.0;
        double endRad = (slice.StartAngle + slice.SweepAngle - 90) * Math.PI / 180.0;

        double x1 = cx + radius * Math.Cos(startRad);
        double y1 = cy + radius * Math.Sin(startRad);
        double x2 = cx + radius * Math.Cos(endRad);
        double y2 = cy + radius * Math.Sin(endRad);

        bool isLargeArc = slice.SweepAngle > 180;

        var fig = new PathFigure
        {
            StartPoint = new Point(cx, cy),
            IsClosed = true
        };
        fig.Segments.Add(new LineSegment(new Point(x1, y1), false));
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(x2, y2),
            Size = new Size(radius, radius),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        path.Data = geo;
        return path;
    }

    private void Slice_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Path path && path.Tag is PieSlice slice)
        {
            HighlightSlice(slice.Index, true);
        }
    }

    private void Slice_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Path path && path.Tag is PieSlice slice)
        {
            HighlightSlice(slice.Index, false);
        }
    }

    private void Legend_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
        {
            HighlightSlice(slice.Index, true);
        }
    }

    private void Legend_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
        {
            HighlightSlice(slice.Index, false);
        }
    }

    private void Slice_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Path path && path.Tag is PieSlice slice)
            SliceClicked?.Invoke(this, slice);
    }

    private void Legend_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
            SliceClicked?.Invoke(this, slice);
    }

    private void HighlightSlice(int index, bool highlight)
    {
        // Update the model (drives legend highlight via DataTrigger)
        if (Slices != null && index >= 0 && index < Slices.Count)
            Slices[index].IsHighlighted = highlight;

        // Scale the pie slice path (lookup by tag for robustness)
        var target = _slicePaths.FirstOrDefault(p => p.Tag is PieSlice s && s.Index == index);
        if (target?.RenderTransform is ScaleTransform transform)
        {
            double scale = highlight ? 1.05 : 1.0;
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
    }
}
