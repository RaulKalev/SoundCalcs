using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SoundCalcs.UI.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            bool invert = parameter is string s && s == "Invert";
            if (invert) flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility v && v == Visibility.Visible;
        }
    }

    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }
    }

    public class EnumToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "";
            return value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || targetType == null) return null;
            string str = value.ToString();
            if (Enum.IsDefined(targetType, str))
                return Enum.Parse(targetType, str);
            return null;
        }
    }

    public class SizeToRectConverter : IMultiValueConverter
    {
        public static readonly SizeToRectConverter Instance = new SizeToRectConverter();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 2 && values[0] is double w && values[1] is double h)
                return new Rect(0, 0, w, h);
            return new Rect(0, 0, 0, 0);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
