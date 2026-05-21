using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.UI.Converters
{
    public sealed class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is AccountState))
                return new SolidColorBrush(Color.FromRgb(90, 90, 106));

            AccountState state = (AccountState)value;
            switch (state)
            {
                case AccountState.Active:
                    return new SolidColorBrush(Color.FromRgb(0, 212, 170));
                case AccountState.Warning:
                case AccountState.GraceWindow:
                case AccountState.NewsLocked:
                    return new SolidColorBrush(Color.FromRgb(245, 158, 11));
                case AccountState.Flattening:
                case AccountState.Locked:
                    return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                case AccountState.HardLocked:
                    return new SolidColorBrush(Color.FromRgb(153, 27, 27));
                default:
                    return new SolidColorBrush(Color.FromRgb(90, 90, 106));
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
