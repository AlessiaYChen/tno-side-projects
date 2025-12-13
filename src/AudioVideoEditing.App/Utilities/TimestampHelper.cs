using System;
using System.Globalization;

namespace AudioVideoEditing.App.Utilities;

internal static class TimestampHelper
{
    public static TimeSpan? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    public static string Format(TimeSpan timestamp)
    {
        var totalHours = (int)Math.Floor(timestamp.TotalHours);
        return $"{totalHours:D2}:{timestamp.Minutes:D2}:{timestamp.Seconds:D2}";
    }

    public static string FormatWithMilliseconds(TimeSpan timestamp)
    {
        var totalHours = (int)Math.Floor(timestamp.TotalHours);
        return $"{totalHours:D2}:{timestamp.Minutes:D2}:{timestamp.Seconds:D2}.{timestamp.Milliseconds:D3}";
    }
}