namespace EmailStat.Converters;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

/// <summary>Maps <c>bool true</c> → <c>Visibility.Visible</c> and <c>false</c> → <c>Visibility.Collapsed</c>.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}
