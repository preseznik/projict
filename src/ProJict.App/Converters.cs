using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ProJict.Core.Model;

namespace ProJict.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Collapsed for null, empty strings and empty collections (or the reverse with Invert).</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            ICollection c => c.Count == 0,
            _ => false,
        };
        return empty ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ProjectStatus.Active => "SystemFillColorSuccessBrush",
            ProjectStatus.Released => "AccentFillColorDefaultBrush",
            ProjectStatus.Paused => "SystemFillColorCautionBrush",
            ProjectStatus.Idea => "SystemFillColorAttentionBrush",
            _ => "SystemFillColorNeutralBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps the tag of an item to a chip colour: bugs red, additions green, the rest neutral.</summary>
public sealed class TagToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tag = (value as string)?.ToUpperInvariant() ?? "";
        var key = tag.StartsWith("BUG") || tag.StartsWith("ISSUE") || tag.StartsWith("FIX") ? "SystemFillColorCriticalBrush"
            : tag.StartsWith("ADD") || tag.StartsWith("FEATURE") ? "SystemFillColorSuccessBrush"
            : tag.StartsWith("CHANGE") ? "SystemFillColorCautionBrush"
            : tag.StartsWith("IDEA") ? "SystemFillColorAttentionBrush"
            : "SystemFillColorNeutralBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Whether a sidebar group is open, from [group name, collapsed group names, search text]; a search opens every group.
/// Returns Visibility, a chevron glyph or a bool, depending on the target property.
/// </summary>
public sealed class GroupExpandedConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = values.ElementAtOrDefault(0) as string;
        var collapsed = values.ElementAtOrDefault(1) as IEnumerable<string> ?? [];
        var searching = !string.IsNullOrWhiteSpace(values.ElementAtOrDefault(2) as string);
        var expanded = searching || name == null || !collapsed.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (targetType == typeof(Visibility)) return expanded ? Visibility.Visible : Visibility.Collapsed;
        if (targetType == typeof(string)) return expanded ? "" : "";
        return expanded;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
