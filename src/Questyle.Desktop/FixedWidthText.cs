using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Questyle.Desktop;

// Draws a number and unit in fixed cells: every digit uses the widest digit's
// advance and every unit the widest unit's, so changing values never move glyphs.
internal sealed class FixedWidthText(Typeface typeface, double size, Brush brush, bool alignRight, string[] units) : FrameworkElement
{
    private const double UnitGap = 4;
    private string number = "", unit = "";

    public string Number => number;
    public string Unit => unit;

    public void Set(string value, string valueUnit)
    {
        if (value == number && valueUnit == unit) return;
        number = value; unit = valueUnit; InvalidateVisual();
    }

    private FormattedText Format(string text) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private double CellWidth(char character) => char.IsDigit(character) ? "0123456789".Max(digit => Format(digit.ToString()).WidthIncludingTrailingWhitespace) : Format(character.ToString()).WidthIncludingTrailingWhitespace;
    private double UnitWidth => units.Max(value => Format(value).WidthIncludingTrailingWhitespace);
    public double ContentWidth => number.Sum(CellWidth) + UnitGap + UnitWidth;

    protected override Size MeasureOverride(Size availableSize) => new(0, Format("0").Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        double x = alignRight ? ActualWidth - ContentWidth : 0;
        foreach (char character in number)
        {
            var glyph = Format(character.ToString()); double cell = CellWidth(character);
            drawingContext.DrawText(glyph, new Point(x + (cell - glyph.WidthIncludingTrailingWhitespace) / 2, 0));
            x += cell;
        }
        drawingContext.DrawText(Format(unit), new Point(x + UnitGap, 0));
    }
}
