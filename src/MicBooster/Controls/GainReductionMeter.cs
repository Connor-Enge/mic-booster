using System.Windows;
using System.Windows.Media;

// The project enables both WPF and WinForms, so the implicit usings pull in two types for
// each of these names. Alias them explicitly rather than fully qualifying every use.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MicBooster.Controls;

/// <summary>
/// Shows how hard a dynamics stage is working. The bar grows from the right edge leftwards,
/// because gain reduction is something being taken away rather than a level being reached.
/// </summary>
/// <remarks>
/// Same drawing strategy as <see cref="LevelMeter"/>: everything is rendered in
/// <see cref="OnRender"/> from static frozen brushes, with the only size-dependent object
/// (the rounded clip geometry) cached until the size changes.
/// </remarks>
public sealed class GainReductionMeter : FrameworkElement
{
    private const double TickSpacingDb = 6;
    private const double DefaultHeight = 10;
    private const double MinWidthForTicks = 40;

    private static readonly Brush TrackBrush = Frozen(0x0D, 0x0F, 0x14);
    private static readonly Brush TickBrush = Frozen(0x5E, 0x67, 0x73, 0x66);
    private static readonly Pen OutlinePen = FrozenPen(Frozen(0x2A, 0x30, 0x38), 1);
    private static readonly Brush GlossBrush = CreateGloss();

    /// <summary>Amber, matching the warn colour in the theme.</summary>
    private static readonly Brush DefaultBarBrush = Frozen(0xF5, 0xA5, 0x24);

    private Geometry? _trackClip;
    private double _clipWidth = -1;
    private double _clipHeight = -1;
    private double _clipRadius = -1;

    /// <summary>Current gain reduction in dB. Positive values mean the stage is reducing gain.</summary>
    public static readonly DependencyProperty ReductionDbProperty = DependencyProperty.Register(
        nameof(ReductionDb), typeof(double), typeof(GainReductionMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Reduction that fills the whole width, in dB.</summary>
    public static readonly DependencyProperty MaxReductionDbProperty = DependencyProperty.Register(
        nameof(MaxReductionDb), typeof(double), typeof(GainReductionMeter),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether to draw a faint tick every 6 dB.</summary>
    public static readonly DependencyProperty ShowScaleProperty = DependencyProperty.Register(
        nameof(ShowScale), typeof(bool), typeof(GainReductionMeter),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fill of the reduction bar.</summary>
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(GainReductionMeter),
        new FrameworkPropertyMetadata(DefaultBarBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Corner rounding of the meter track, in device-independent pixels.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GainReductionMeter),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Current gain reduction in dB. Positive values mean the stage is reducing gain.</summary>
    public double ReductionDb
    {
        get => (double)GetValue(ReductionDbProperty);
        set => SetValue(ReductionDbProperty, value);
    }

    /// <summary>Reduction that fills the whole width, in dB.</summary>
    public double MaxReductionDb
    {
        get => (double)GetValue(MaxReductionDbProperty);
        set => SetValue(MaxReductionDbProperty, value);
    }

    /// <summary>Whether to draw a faint tick every 6 dB.</summary>
    public bool ShowScale
    {
        get => (bool)GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <summary>Fill of the reduction bar.</summary>
    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>Corner rounding of the meter track, in device-independent pixels.</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height;
        if (double.IsNaN(width) || width < 0) width = 0;
        if (double.IsNaN(height) || height < 0) height = 0;
        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        // The comparisons also reject NaN, which is what a not-yet-arranged element reports.
        if (!(width > 2) || !(height > 2)) return;

        double radius = CornerRadius;
        if (!(radius > 0)) radius = 0;
        radius = Math.Min(radius, Math.Min(width, height) * 0.5);

        double maxDb = MaxReductionDb;
        if (double.IsNaN(maxDb) || double.IsInfinity(maxDb) || maxDb <= 0) maxDb = 24;

        double reduction = ReductionDb;
        if (double.IsNaN(reduction) || reduction < 0) reduction = 0;
        else if (reduction > maxDb) reduction = maxDb;

        drawingContext.PushClip(GetTrackClip(width, height, radius));
        drawingContext.DrawRectangle(TrackBrush, null, new Rect(0, 0, width, height));

        if (ShowScale && width >= MinWidthForTicks)
        {
            for (double db = TickSpacingDb; db < maxDb; db += TickSpacingDb)
            {
                double x = Math.Round(width - db / maxDb * width);
                if (x < 1 || x > width - 1) continue;
                drawingContext.DrawRectangle(TickBrush, null, new Rect(x, 0, 1, height));
            }
        }

        double barWidth = reduction / maxDb * width;
        if (barWidth > 0.5)
        {
            var barRect = new Rect(width - barWidth, 0, barWidth, height);
            drawingContext.DrawRectangle(BarBrush ?? DefaultBarBrush, null, barRect);
            drawingContext.DrawRectangle(GlossBrush, null, barRect);
        }

        drawingContext.Pop();

        drawingContext.DrawRoundedRectangle(null, OutlinePen,
            new Rect(0.5, 0.5, Math.Max(width - 1, 0), Math.Max(height - 1, 0)), radius, radius);
    }

    private Geometry GetTrackClip(double width, double height, double radius)
    {
        if (_trackClip is null || _clipWidth != width || _clipHeight != height || _clipRadius != radius)
        {
            var geometry = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
            geometry.Freeze();
            _trackClip = geometry;
            _clipWidth = width;
            _clipHeight = height;
            _clipRadius = radius;
        }
        return _trackClip;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateGloss()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1E, 0x00, 0x00, 0x00), 1.0));
        brush.Freeze();
        return brush;
    }
}
