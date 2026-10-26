using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ValheimServerGUI.App.Controls;

/// <summary>
/// A small, axis-less trend line: the latest <see cref="Values"/> drawn left (oldest) to right (newest) across the
/// control, over a faint filled area. The vertical scale runs from 0 to <see cref="Maximum"/>, or to the largest
/// value when <see cref="Maximum"/> is not set (NaN). Replace <see cref="Values"/> with a new list to redraw.
/// </summary>
public class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), double.NaN);

    /// <summary>How many points the line spans; fewer values are drawn against the right edge.</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 60);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, MaximumProperty, CapacityProperty, StrokeProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width < 2 || bounds.Height < 2) return;

        var stroke = Stroke ?? Brushes.Gray;
        var color = (stroke as ISolidColorBrush)?.Color ?? Colors.Gray;

        // Baseline, so an idle server still shows a chart rather than an empty box.
        var baseline = new Pen(new SolidColorBrush(color, 0.25), 1);
        context.DrawLine(baseline, new Point(0, bounds.Height - 0.5), new Point(bounds.Width, bounds.Height - 0.5));

        var values = Values;
        if (values is null || values.Count < 2) return;

        var capacity = Math.Max(Capacity, values.Count);
        var max = double.IsNaN(Maximum) ? 0 : Maximum;
        if (double.IsNaN(Maximum))
            foreach (var v in values) max = Math.Max(max, v);
        if (max <= 0) max = 1;

        var step = bounds.Width / (capacity - 1);
        var startX = bounds.Width - step * (values.Count - 1);
        var top = 1.0;
        var height = bounds.Height - top - 1;

        Point PointAt(int i)
        {
            var ratio = Math.Clamp(values[i] / max, 0, 1);
            return new Point(startX + step * i, top + height * (1 - ratio));
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineCtx = line.Open())
        using (var areaCtx = area.Open())
        {
            var first = PointAt(0);
            lineCtx.BeginFigure(first, false);
            areaCtx.BeginFigure(new Point(first.X, bounds.Height), true);
            areaCtx.LineTo(first);
            for (var i = 1; i < values.Count; i++)
            {
                var p = PointAt(i);
                lineCtx.LineTo(p);
                areaCtx.LineTo(p);
            }
            lineCtx.EndFigure(false);
            areaCtx.LineTo(new Point(bounds.Width, bounds.Height));
            areaCtx.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(color, 0.18), null, area);
        context.DrawGeometry(null, new Pen(stroke, 1.5, lineJoin: PenLineJoin.Round), line);
    }
}
