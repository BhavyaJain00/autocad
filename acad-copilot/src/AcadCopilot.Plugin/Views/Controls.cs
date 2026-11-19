using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AcadCopilot.Plugin.Views;

/// <summary>
/// Vector icon drawn from a geometry in a 16 x 16 design box, stroked (or filled) with the inherited text
/// foreground, so icons follow button/text colors and the light/dark theme.
/// </summary>
public sealed class Icon : FrameworkElement
{
    /// <summary>Geometry in 16 x 16 units.</summary>
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Icon color (inherits the text foreground).</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True to fill the geometry instead of stroking it.</summary>
    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Stroke thickness in 16-unit design space.</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(14.0));
        HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(14.0));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon), new UIPropertyMetadata(false));
    }

    /// <summary>Geometry in 16 x 16 units.</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Icon color.</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Fill instead of stroke.</summary>
    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    /// <summary>Stroke thickness (16-unit space).</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / 16.0;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - (16 * scale)) / 2, (ActualHeight - (16 * scale)) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        if (IsFilled)
        {
            drawingContext.DrawGeometry(Foreground, null, Data);
        }
        else
        {
            var pen = new Pen(Foreground, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            drawingContext.DrawGeometry(null, pen, Data);
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }
}

/// <summary>Small circular busy indicator; rotates only while visible.</summary>
public sealed class Spinner : FrameworkElement
{
    /// <summary>Arc color (inherits the text foreground).</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Spinner), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RotateTransform _rotation = new();

    static Spinner()
    {
        WidthProperty.OverrideMetadata(typeof(Spinner), new FrameworkPropertyMetadata(14.0));
        HeightProperty.OverrideMetadata(typeof(Spinner), new FrameworkPropertyMetadata(14.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Spinner), new UIPropertyMetadata(false));
    }

    /// <summary>Creates the spinner.</summary>
    public Spinner()
    {
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>Arc color.</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var thickness = Math.Max(1.5, size / 8);
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = new Pen(Foreground, thickness) { DashCap = PenLineCap.Round };
        drawingContext.PushOpacity(0.22);
        drawingContext.DrawEllipse(null, track, center, radius, radius);
        drawingContext.Pop();

        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius, center.Y);
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }

        arc.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Foreground, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }

    private void UpdateAnimation()
    {
        if (IsVisible && IsLoaded)
        {
            var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9))) { RepeatBehavior = RepeatBehavior.Forever };
            _rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}

/// <summary>
/// Forwards mouse-wheel scrolling from read-only text boxes (which swallow it) to the conversation's scroll viewer.
/// </summary>
public static class WheelForwarding
{
    /// <summary>Enables forwarding on a control.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelForwarding), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Gets the attached value.</summary>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Sets the attached value.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>Re-raises a wheel event on the parent of <paramref name="source"/>.</summary>
    public static void Forward(UIElement source, MouseWheelEventArgs e)
    {
        if (e.Handled || VisualTreeHelper.GetParent(source) is not UIElement parent)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source,
        });
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) => Forward((UIElement)sender, e);
}

/// <summary>bool -> Visibility (ConverterParameter "Invert" flips it). Null and empty strings count as false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}

/// <summary>0..1 fraction -> width of a progress bar fill, given the track width as the second binding.</summary>
public sealed class FractionToWidthConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double fraction && values[1] is double width && !double.IsNaN(width))
        {
            return Math.Max(0, Math.Min(1, fraction)) * width;
        }

        return 0.0;
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Helpers for <see cref="ItemsControl"/> scrolling in the conversation.</summary>
internal static class VisualTreeSearch
{
    /// <summary>Finds the first descendant of type <typeparamref name="T"/>.</summary>
    public static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
