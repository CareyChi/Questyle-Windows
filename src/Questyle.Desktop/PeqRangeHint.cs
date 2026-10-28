using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Questyle.Desktop;

internal sealed class PeqRangeHint : Adorner
{
    private readonly Border label;
    public AdornerLayer Layer { get; }

    public PeqRangeHint(TextBox input, string range) : base(input)
    {
        Layer = AdornerLayer.GetAdornerLayer(input);
        IsHitTestVisible = false;
        Opacity = 0;
        label = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 40, 50)),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock { Text = range, Foreground = Brushes.White, FontFamily = input.FontFamily, FontSize = 11 }
        };
        AddVisualChild(label);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => label;
    protected override Size MeasureOverride(Size constraint)
    {
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return AdornedElement.RenderSize;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        label.Arrange(new Rect(new Point((finalSize.Width - label.DesiredSize.Width) / 2, -label.DesiredSize.Height - 5), label.DesiredSize));
        return finalSize;
    }
}
