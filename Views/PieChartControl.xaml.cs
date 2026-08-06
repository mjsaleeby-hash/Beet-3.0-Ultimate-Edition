using System.Collections.ObjectModel;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BeetsBackup.Models;
using Brush = System.Windows.Media.Brush;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyboardFocusChangedEventArgs = System.Windows.Input.KeyboardFocusChangedEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace BeetsBackup.Views;

/// <summary>
/// Custom donut-chart control that renders pie slices from a bound <see cref="PieSlice"/> collection.
/// Supports hover highlighting, click-to-navigate, and a synchronized legend.
/// </summary>
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

        // Slices is REPLACED wholesale, not mutated in place — BuildPieSlices hands the
        // control a brand-new ObservableCollection on every navigation, and the commonest
        // trigger is clicking a legend row. A stale _hoveredIndex/_focusedIndex pointing
        // into the OLD collection causes two distinct bugs against the new one: (1) if the
        // cursor is still sitting over the row that triggered the navigation, the new
        // Button's MouseEnter hits SetSourceIndex's "already this index" dedupe guard and
        // never calls ApplyHighlight, so the row under the cursor silently fails to
        // highlight until the user moves off and back; (2) if a focused Button was torn
        // out of the tree without a matching LostKeyboardFocus (WPF does not guarantee one
        // for a removed element), _focusedIndex stays stale forever and the same row index
        // in every SUBSEQUENT chart re-highlights on a bare hover-and-leave. Reset both
        // here — do not delete this as "redundant" with _slicePaths.Clear() above; paths
        // and highlight sources are different state with different staleness bugs.
        _hoveredIndex = -1;
        _focusedIndex = -1;

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
            Opacity = 0.5
        };
        bgCircle.SetResourceReference(Shape.FillProperty, "PanelBrush");
        Canvas.SetLeft(bgCircle, cx - radius);
        Canvas.SetTop(bgCircle, cy - radius);
        PieCanvas.Children.Add(bgCircle);

        var sliceBorderBrush = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;

        foreach (var slice in slices)
        {
            // A slice below MinVisiblePercentage draws nothing, so don't create a path
            // for it at all. The old code returned a Data-less Path that was still added
            // to _slicePaths, still added to the canvas, and still had three mouse
            // handlers wired to it — so hovering that item's legend row highlighted an
            // invisible shape. HighlightSlice looks paths up with FirstOrDefault and
            // tolerates the absence.
            if (!slice.IsRenderable)
                continue;

            var path = CreateSlicePath(slice, cx, cy, radius, sliceBorderBrush);
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
        };
        hole.SetResourceReference(Shape.FillProperty, "DonutCenterBrush");
        Canvas.SetLeft(hole, cx - 50);
        Canvas.SetTop(hole, cy - 50);
        PieCanvas.Children.Add(hole);
    }

    private static Path CreateSlicePath(PieSlice slice, double cx, double cy, double radius, Brush sliceBorderBrush)
    {
        var path = new Path
        {
            Fill = new SolidColorBrush(slice.FillColor),
            Stroke = sliceBorderBrush,
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            // Scale the slice about the PIE's center (cx,cy), not the slice's own
            // bounding-box center. ScaleTransform's 3rd/4th args are the fixed center
            // point in the same coordinate space the geometry uses, so the wedge now
            // grows radially outward on hover instead of drifting sideways.
            RenderTransform = new ScaleTransform(1, 1, cx, cy)
        };

        if (slice.SweepAngle >= 359.9)
        {
            // Full circle
            path.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
            return path;
        }

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
            SetHovered(slice.Index, true);
        }
    }

    private void Slice_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Path path && path.Tag is PieSlice slice)
        {
            SetHovered(slice.Index, false);
        }
    }

    private void Legend_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
        {
            SetHovered(slice.Index, true);
        }
    }

    private void Legend_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
        {
            SetHovered(slice.Index, false);
        }
    }

    private void Slice_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Path path && path.Tag is PieSlice slice)
            SliceClicked?.Invoke(this, slice);
    }

    private void Legend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
            SliceClicked?.Invoke(this, slice);
    }

    // Keyboard focus lights the matching wedge exactly as hover does. Without this the
    // legend would be merely reachable by keyboard rather than usable by it — a keyboard
    // user would have no idea which wedge the focused row corresponds to, which is the
    // whole point of the row carrying a colour swatch.
    //
    // Deliberately wired to GotKeyboardFocus/LostKeyboardFocus, NOT the plan's original
    // GotFocus/LostFocus. GotFocus/LostFocus track LOGICAL focus, which a mouse click
    // also grants — so clicking a legend row for a file (SliceClicked is a no-op, no
    // rebuild) and then moving the mouse away left the row highlighted and its wedge
    // scaled forever, with no focus ring shown (FocusVisualStyle only paints for real
    // keyboard focus), and Alt-Tabbing away from the app never cleared it either, since
    // logical focus is untouched by the window losing input focus. Keyboard focus does
    // not have that problem: it is cleared to null when the app deactivates, so the
    // highlight and the focus ring now appear and disappear together in every case,
    // exactly as this comment claims. Overridden per Wave 2.5 whole-branch review.
    private void Legend_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
            SetFocused(slice.Index, true);
    }

    private void Legend_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSlice slice)
            SetFocused(slice.Index, false);
    }

    // Hover (mouse, from either the wedge or the legend row) and keyboard focus (legend
    // row only) are tracked as two SEPARATE inputs, each holding at most one index at a
    // time — only one thing can be hovered or focused at once, so a plain int per source
    // is enough; no dictionary or ref-count needed. A slice is highlighted whenever EITHER
    // is pointing at it, and only drops when BOTH have moved off it.
    //
    // Before this, HighlightSlice(index, bool) wrote IsHighlighted as a single last-write-
    // wins boolean fed by three independent enter/leave pairs (wedge hover, legend hover,
    // legend focus). That let one source's "leave" clobber another source's still-active
    // "enter" — e.g. Tab to a row (focus on), then wave the mouse over it and off (hover
    // on, then hover off) dropped the highlight even though the row was still keyboard-
    // focused. Do NOT collapse _hoveredIndex/_focusedIndex back into one boolean; that
    // reintroduces exactly this desync. See Wave 2.5 Task 6 review, round 1.
    private int _hoveredIndex = -1;
    private int _focusedIndex = -1;

    private void SetHovered(int index, bool active) => SetSourceIndex(ref _hoveredIndex, index, active);

    private void SetFocused(int index, bool active) => SetSourceIndex(ref _focusedIndex, index, active);

    // Shared plumbing for SetHovered/SetFocused. `active` true means "this index just
    // became this source's target"; false means "this index just stopped being this
    // source's target". The false branch only clears the field (and re-derives that
    // index's highlight from the OTHER source) if this index is still the one the field
    // holds — a stale Leave/LostFocus for an index that a later Enter/GotFocus already
    // replaced must be a no-op, since WPF does not guarantee old-element-leaves-before-
    // new-element-enters ordering when focus/hover moves directly from row A to row B.
    private void SetSourceIndex(ref int sourceIndex, int index, bool active)
    {
        if (active)
        {
            if (sourceIndex == index)
                return;

            int previous = sourceIndex;
            sourceIndex = index;

            if (previous >= 0)
                ApplyHighlight(previous, IsHighlighted(previous));
            ApplyHighlight(index, true);
        }
        else
        {
            if (sourceIndex != index)
                return;

            sourceIndex = -1;
            ApplyHighlight(index, IsHighlighted(index));
        }
    }

    // True if either hover or focus currently claims this index.
    private bool IsHighlighted(int index) => index == _hoveredIndex || index == _focusedIndex;

    private void ApplyHighlight(int index, bool highlight)
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
