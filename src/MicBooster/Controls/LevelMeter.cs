using System.Globalization;
using System.Windows;
using System.Windows.Media;

// The project enables both WPF and WinForms, so the implicit usings pull in two types for
// each of these names. Alias them explicitly rather than fully qualifying every use.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MicBooster.Controls;

/// <summary>
/// Horizontal level meter: an RMS bar coloured by position on the dB scale, a bright peak
/// tick, an optional dB scale, and a clip indicator at the far right.
/// </summary>
/// <remarks>
/// Drawn directly in <see cref="OnRender"/> instead of composed from shapes, because this
/// redraws at the metering rate (~30 Hz) and a shape tree would mean a layout pass per frame.
/// Every brush, pen and typeface is static and frozen; the size-dependent objects (clip
/// geometry, gradient, scale labels) are cached and only rebuilt when the size or DPI changes.
/// </remarks>
public sealed class LevelMeter : FrameworkElement
{
    private const double ScaleStripHeight = 12;
    private const double LabelEmSize = 9;
    private const double LabelGap = 6;
    private const double ClipCellWidth = 9;
    private const double ClipCellGap = 2;
    private const double MinWidthForScale = 130;
    private const double MinWidthForClipCell = 70;
    private const double DefaultHeight = 18;

    /// <summary>Gridline/label positions. Uneven on purpose: the top of the range matters most.</summary>
    private static readonly double[] ScaleDbValues = { -60, -48, -36, -24, -18, -12, -6, 0 };

    private static readonly Brush TrackBrush = Frozen(0x0D, 0x0F, 0x14);
    private static readonly Brush ClipIdleBrush = Frozen(0x1E, 0x22, 0x2B);
    private static readonly Brush ClipActiveBrush = Frozen(0xF2, 0x55, 0x5A);
    private static readonly Brush PeakBrush = Frozen(0xF2, 0xF5, 0xF9);
    private static readonly Brush GridBrush = Frozen(0x5E, 0x67, 0x73, 0x66);
    private static readonly Brush LabelBrush = Frozen(0x6E, 0x78, 0x86);
    private static readonly Pen OutlinePen = FrozenPen(Frozen(0x2A, 0x30, 0x38), 1);

    /// <summary>Vertical sheen over the bar so it reads as a lit strip rather than flat paint.</summary>
    private static readonly Brush GlossBrush = CreateGloss();

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Consolas, Cascadia Mono"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Geometry? _trackClip;
    private double _clipWidth = -1;
    private double _clipHeight = -1;
    private double _clipRadius = -1;

    private Brush? _spanBrush;
    private double _spanWidth = -1;

    private FormattedText[]? _labels;
    private double _labelDpi = -1;

    /// <summary>Instantaneous peak level in dBFS.</summary>
    public static readonly DependencyProperty PeakDbProperty = DependencyProperty.Register(
        nameof(PeakDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Short-term RMS level in dBFS; this is what the bar shows.</summary>
    public static readonly DependencyProperty RmsDbProperty = DependencyProperty.Register(
        nameof(RmsDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Left end of the displayed scale, in dBFS.</summary>
    public static readonly DependencyProperty MinDbProperty = DependencyProperty.Register(
        nameof(MinDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Right end of the displayed scale, in dBFS.</summary>
    public static readonly DependencyProperty MaxDbProperty = DependencyProperty.Register(
        nameof(MaxDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether to draw gridlines and dB labels beneath the bar.</summary>
    public static readonly DependencyProperty ShowScaleProperty = DependencyProperty.Register(
        nameof(ShowScale), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Lights the clip cell at the far right.</summary>
    public static readonly DependencyProperty ClippingProperty = DependencyProperty.Register(
        nameof(Clipping), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Corner rounding of the meter track, in device-independent pixels.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Instantaneous peak level in dBFS.</summary>
    public double PeakDb
    {
        get => (double)GetValue(PeakDbProperty);
        set => SetValue(PeakDbProperty, value);
    }

    /// <summary>Short-term RMS level in dBFS.</summary>
    public double RmsDb
    {
        get => (double)GetValue(RmsDbProperty);
        set => SetValue(RmsDbProperty, value);
    }

    /// <summary>Left end of the displayed scale, in dBFS.</summary>
    public double MinDb
    {
        get => (double)GetValue(MinDbProperty);
        set => SetValue(MinDbProperty, value);
    }

    /// <summary>Right end of the displayed scale, in dBFS.</summary>
    public double MaxDb
    {
        get => (double)GetValue(MaxDbProperty);
        set => SetValue(MaxDbProperty, value);
    }

    /// <summary>Whether to draw gridlines and dB labels beneath the bar.</summary>
    public bool ShowScale
    {
        get => (bool)GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <summary>Lights the clip cell at the far right.</summary>
    public bool Clipping
    {
        get => (bool)GetValue(ClippingProperty);
        set => SetValue(ClippingProperty, value);
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
        double width = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
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

        double minDb = MinDb;
        double maxDb = MaxDb;
        if (double.IsNaN(minDb) || double.IsInfinity(minDb)) minDb = -60;
        if (double.IsNaN(maxDb) || double.IsInfinity(maxDb)) maxDb = 0;
        if (maxDb - minDb < 1) maxDb = minDb + 1;

        bool showScale = ShowScale && width >= MinWidthForScale && height >= ScaleStripHeight + 10;
        bool showGrid = ShowScale && width >= 60;
        double barHeight = showScale ? height - ScaleStripHeight : height;

        double clipCell = width >= MinWidthForClipCell ? ClipCellWidth : 0;
        double barWidth = width - (clipCell > 0 ? clipCell + ClipCellGap : 0);
        if (barWidth < 8)
        {
            clipCell = 0;
            barWidth = width;
        }

        var trackRect = new Rect(0, 0, width, barHeight);
        drawingContext.PushClip(GetTrackClip(width, barHeight, radius));
        drawingContext.DrawRectangle(TrackBrush, null, trackRect);

        if (showGrid)
        {
            DrawGridlines(drawingContext, minDb, maxDb, barWidth, barHeight);
        }

        double rmsX = MapDb(RmsDb, minDb, maxDb, barWidth);
        if (rmsX > 0.5)
        {
            var barRect = new Rect(0, 0, rmsX, barHeight);
            drawingContext.DrawRectangle(GetSpanBrush(barWidth), null, barRect);
            drawingContext.DrawRectangle(GlossBrush, null, barRect);
        }

        double peakDb = PeakDb;
        if (!double.IsNaN(peakDb) && peakDb > minDb)
        {
            double peakX = MapDb(peakDb, minDb, maxDb, barWidth);
            double tickX = Math.Round(Math.Min(Math.Max(peakX, 1), barWidth - 1)) - 1;
            drawingContext.DrawRectangle(PeakBrush, null,
                new Rect(tickX, 1, 2, Math.Max(barHeight - 2, 1)));
        }

        if (clipCell > 0)
        {
            var cell = new Rect(width - clipCell, 1.5, clipCell, Math.Max(barHeight - 3, 1));
            drawingContext.DrawRoundedRectangle(
                Clipping ? ClipActiveBrush : ClipIdleBrush, null, cell, 2, 2);
        }

        drawingContext.Pop();

        drawingContext.DrawRoundedRectangle(null, OutlinePen,
            new Rect(0.5, 0.5, Math.Max(width - 1, 0), Math.Max(barHeight - 1, 0)), radius, radius);

        if (showScale)
        {
            DrawScaleLabels(drawingContext, minDb, maxDb, barWidth, width, barHeight);
        }
    }

    private static void DrawGridlines(DrawingContext dc, double minDb, double maxDb, double barWidth, double barHeight)
    {
        for (int i = 0; i < ScaleDbValues.Length; i++)
        {
            double db = ScaleDbValues[i];
            if (db < minDb || db > maxDb) continue;
            double x = Math.Round(MapDb(db, minDb, maxDb, barWidth));
            if (x < 1 || x > barWidth - 1) continue;
            dc.DrawRectangle(GridBrush, null, new Rect(x, 0, 1, barHeight));
        }
    }

    private void DrawScaleLabels(DrawingContext dc, double minDb, double maxDb,
                                 double barWidth, double width, double barHeight)
    {
        FormattedText[] labels = GetLabels();
        int stride = SelectLabelStride(labels, minDb, maxDb, barWidth);
        if (stride <= 0) return;

        double y = barHeight + 1;
        for (int i = 0; i < ScaleDbValues.Length; i += stride)
        {
            double db = ScaleDbValues[i];
            if (db < minDb || db > maxDb) continue;

            FormattedText label = labels[i];
            double x = MapDb(db, minDb, maxDb, barWidth) - label.Width * 0.5;
            if (x < 0) x = 0;
            if (x + label.Width > width) x = width - label.Width;
            dc.DrawText(label, new Point(x, y));
        }
    }

    /// <summary>
    /// Picks the coarsest label spacing that fits, or 0 when even every fourth label collides.
    /// </summary>
    private static int SelectLabelStride(FormattedText[] labels, double minDb, double maxDb, double barWidth)
    {
        for (int stride = 1; stride <= 4; stride++)
        {
            bool fits = true;
            double previousRight = double.NegativeInfinity;

            for (int i = 0; i < ScaleDbValues.Length; i += stride)
            {
                double db = ScaleDbValues[i];
                if (db < minDb || db > maxDb) continue;

                double labelWidth = labels[i].Width;
                double left = MapDb(db, minDb, maxDb, barWidth) - labelWidth * 0.5;
                if (left < previousRight + LabelGap)
                {
                    fits = false;
                    break;
                }
                previousRight = left + labelWidth;
            }

            if (fits) return stride;
        }
        return 0;
    }

    private static double MapDb(double db, double minDb, double maxDb, double barWidth)
    {
        if (double.IsNaN(db)) return 0;
        double t = (db - minDb) / (maxDb - minDb);
        if (double.IsNaN(t) || t < 0) t = 0;
        else if (t > 1) t = 1;
        return t * barWidth;
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

    /// <summary>
    /// Gradient anchored to the whole bar span in absolute coordinates, so the colour at a
    /// given level never changes with how far the bar happens to have filled.
    /// </summary>
    private Brush GetSpanBrush(double barWidth)
    {
        if (_spanBrush is null || _spanWidth != barWidth)
        {
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(barWidth, 0)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x17, 0x9E, 0x54), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x9C, 0xD2, 0x3C), 0.7));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0xA5, 0x24), 0.82));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0x55, 0x5A), 0.96));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0x55, 0x5A), 1.0));
            brush.Freeze();
            _spanBrush = brush;
            _spanWidth = barWidth;
        }
        return _spanBrush;
    }

    private FormattedText[] GetLabels()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_labels is null || _labelDpi != dpi)
        {
            var labels = new FormattedText[ScaleDbValues.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = new FormattedText(
                    ScaleDbValues[i].ToString("0", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    LabelEmSize,
                    LabelBrush,
                    dpi);
            }
            _labels = labels;
            _labelDpi = dpi;
        }
        return _labels;
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
