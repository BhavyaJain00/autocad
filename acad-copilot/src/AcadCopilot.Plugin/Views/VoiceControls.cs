using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AcadCopilot.Plugin.Views;

/// <summary>
/// A soft disc that grows and fades out again and again while visible: the "listening" pulse behind the red mic button
/// and next to "Listening...". Animates only while visible and loaded (like <see cref="Spinner"/>).
/// </summary>
public sealed class PulseRing : FrameworkElement
{
    /// <summary>Disc color (inherits the text foreground).</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(PulseRing), new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far the disc grows (1.6 = 60 % larger than the element).</summary>
    public static readonly DependencyProperty GrowthProperty = DependencyProperty.Register(
        nameof(Growth), typeof(double), typeof(PulseRing), new FrameworkPropertyMetadata(1.6));

    private readonly ScaleTransform _scale = new(1, 1);

    static PulseRing()
    {
        IsHitTestVisibleProperty.OverrideMetadata(typeof(PulseRing), new UIPropertyMetadata(false));
    }

    /// <summary>Creates the pulse.</summary>
    public PulseRing()
    {
        RenderTransform = _scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Opacity = 0.35;
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    /// <summary>Disc color.</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>How far the disc grows.</summary>
    public double Growth
    {
        get => (double)GetValue(GrowthProperty);
        set => SetValue(GrowthProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) / 2;
        if (radius > 0)
        {
            drawingContext.DrawEllipse(Foreground, null, new Point(ActualWidth / 2, ActualHeight / 2), radius, radius);
        }
    }

    private void UpdateAnimation()
    {
        if (!IsVisible || !IsLoaded)
        {
            StopAnimation();
            return;
        }

        var duration = new Duration(TimeSpan.FromSeconds(1.2));
        var grow = new DoubleAnimation(1, Growth, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.45, 0, duration) { RepeatBehavior = RepeatBehavior.Forever };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        BeginAnimation(OpacityProperty, fade);
    }

    private void StopAnimation()
    {
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BeginAnimation(OpacityProperty, null);
    }
}
