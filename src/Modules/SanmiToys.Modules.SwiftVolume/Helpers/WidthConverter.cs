using System;
using System.Globalization;
using System.Windows.Data;

namespace SanmiToys.Modules.SwiftVolume.Helpers;

public class WidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double totalWidth &&
            values[1] is float peakLevel)
        {
            double padding = 0;
            if (parameter != null && double.TryParse(parameter.ToString(), out double p))
            {
                padding = p;
            }

            double availableWidth = Math.Max(0, totalWidth - padding);
            return Math.Clamp(availableWidth * peakLevel, 0, availableWidth);
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
