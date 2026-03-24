using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SoundCalcs.UI.ViewModels;

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

    /// <summary>
    /// Converts a VisualizationMode enum value to a user-friendly display name
    /// for the ComboBox in the Results tab.
    /// </summary>
    public class VisualizationModeToDisplayNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is VisualizationMode mode)
            {
                switch (mode)
                {
                    case VisualizationMode.SPL:     return "SPL  (Broadband)";
                    case VisualizationMode.SPL_A:   return "SPL  (A-weighted, dBA)";
                    case VisualizationMode.STI:     return "STI  (Intelligibility)";
                    case VisualizationMode.C80:     return "C80  (Clarity)";
                    case VisualizationMode.SPL_125: return "125 Hz";
                    case VisualizationMode.SPL_250: return "250 Hz";
                    case VisualizationMode.SPL_500: return "500 Hz";
                    case VisualizationMode.SPL_1k:  return "1 kHz";
                    case VisualizationMode.SPL_2k:  return "2 kHz";
                    case VisualizationMode.SPL_4k:  return "4 kHz";
                    case VisualizationMode.SPL_8k:  return "8 kHz";
                    default: return value.ToString();
                }
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
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
