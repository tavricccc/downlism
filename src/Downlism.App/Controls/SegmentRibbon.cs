using Downlism.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Downlism.App.Controls;

/// <summary>
/// Shows where each connection has reached, as one bar divided into the transfer's real
/// segments.
/// </summary>
/// <remarks>
/// This is the one place in the app allowed to be loud. A single averaged progress bar hides
/// the thing a download manager exists to do; showing the actual segment boundaries, each
/// filling from its own offset, makes the parallelism legible and makes a stalled connection
/// obvious at a glance. The geometry comes straight from the resume sidecar, so what is drawn
/// is exactly what would survive a crash.
/// </remarks>
public sealed partial class SegmentRibbon : Canvas
{
    private const double Gap = 3;
    private const double MinimumSegmentWidth = 2;

    public SegmentRibbon()
    {
        Height = 6;
        SizeChanged += (_, _) => Redraw();
    }

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<Segment>),
        typeof(SegmentRibbon),
        new PropertyMetadata(null, (d, _) => ((SegmentRibbon)d).Redraw()));

    public IReadOnlyList<Segment>? Segments
    {
        get => (IReadOnlyList<Segment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Drives the fill colour; a failed transfer keeps its shape but changes tone.</summary>
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(string),
        typeof(SegmentRibbon),
        new PropertyMetadata("running", (d, _) => ((SegmentRibbon)d).Redraw()));

    public string Tone
    {
        get => (string)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>
    /// Used when there are no segments: a server that refuses ranges still deserves a bar.
    /// </summary>
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction),
        typeof(double),
        typeof(SegmentRibbon),
        new PropertyMetadata(0d, (d, _) => ((SegmentRibbon)d).Redraw()));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    private void Redraw()
    {
        Children.Clear();

        var width = ActualWidth;
        if (width <= 0) return;

        var fill = Resolve(Tone switch
        {
            "failed" => "SystemFillColorCriticalBrush",
            "completed" => "SystemFillColorSuccessBrush",
            "paused" => "TextFillColorDisabledBrush",
            _ => "AccentFillColorDefaultBrush",
        });

        // The unfilled remainder stays very quiet: emptiness is not information worth shouting.
        var track = Resolve("ControlAltFillColorSecondaryBrush");

        var segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            Draw(0, width, Math.Clamp(Fraction, 0, 1), track, fill);
            return;
        }

        var totalBytes = segments.Sum(segment => segment.Length);
        if (totalBytes <= 0) return;

        var gaps = Gap * (segments.Count - 1);
        var usable = Math.Max(width - gaps, width * 0.5);
        var offset = 0d;

        foreach (var segment in segments)
        {
            var segmentWidth = Math.Max(MinimumSegmentWidth, usable * segment.Length / totalBytes);
            var filled = segment.Length > 0 ? Math.Clamp((double)segment.Completed / segment.Length, 0, 1) : 0;

            Draw(offset, segmentWidth, filled, track, fill);
            offset += segmentWidth + Gap;
        }
    }

    private void Draw(double left, double width, double filled, Brush track, Brush fill)
    {
        Children.Add(Bar(left, width, track));

        var filledWidth = width * filled;
        if (filledWidth >= 0.5) Children.Add(Bar(left, filledWidth, fill));
    }

    private Rectangle Bar(double left, double width, Brush brush)
    {
        var bar = new Rectangle
        {
            Width = width,
            Height = Height,
            Fill = brush,
            RadiusX = Height / 2,
            RadiusY = Height / 2,
        };

        SetLeft(bar, left);
        SetTop(bar, 0);
        return bar;
    }

    private Brush Resolve(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
