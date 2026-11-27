using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Playnite.Converters
{
    /// <summary>
    /// A percent (0 to 100) as the width of one of the two columns the button fill is drawn with:
    /// the fill column gets the percent and the column left of it, asked with the parameter "rest",
    /// gets what's left. Stars only share space by their ratio, so both halves are needed for the
    /// fill to reach the real percent. Anything unknown is no fill at all.
    /// </summary>
    [ValueConversion(typeof(object), typeof(GridLength))]
    public class PercentToFillConverter : MarkupExtension, IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var percent = value is double number ? number : value is int whole ? whole : double.NaN;
            var fill = double.IsNaN(percent) ? 0 : Math.Max(0, Math.Min(100, percent));
            var rest = string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
            if (!rest && double.IsNaN(percent))
            {
                return new GridLength(0);
            }

            return new GridLength(rest ? 100 - fill : fill, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return this;
        }
    }
}
