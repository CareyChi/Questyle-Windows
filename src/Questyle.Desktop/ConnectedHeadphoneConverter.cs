using System.Globalization;
using System.Windows.Data;

namespace Questyle.Desktop;

public sealed class ConnectedHeadphoneConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is string address && values[1] is string connected && address.Equals(connected, StringComparison.OrdinalIgnoreCase);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
