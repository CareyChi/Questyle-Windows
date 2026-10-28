using System.Windows;
using System.Windows.Media;

namespace Questyle.Desktop;

public sealed class PeqCurveVisual : FrameworkElement
{
    private readonly DrawingVisual drawing = new();
    private readonly Pen stroke;
    private Point[] segments = [];
    public PointCollection Points { get; private set; } = [];

    public PeqCurveVisual(Brush brush)
    {
        IsHitTestVisible = false;
        stroke = new Pen(brush.CloneCurrentValue(), 2); stroke.Freeze();
        AddVisualChild(drawing);
    }

    public void Draw(PointCollection points)
    {
        Points = points;
        if (segments.Length != points.Count - 1) segments = new Point[points.Count - 1];
        for (int i = 1; i < points.Count; i++) segments[i - 1] = points[i];
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(segments, true, true);
        }
        geometry.Freeze();
        using var canvas = drawing.RenderOpen();
        canvas.DrawGeometry(null, stroke, geometry);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => drawing;
}
