namespace PortableDev.Core.Util;

public static class TimeUtil
{
    public static DateTimeOffset Now() => DateTimeOffset.Now;

    public static string NowIso() => Format(DateTimeOffset.Now);

    public static string Format(DateTimeOffset value)
        => value.ToString(Protocol.ProtocolConstants.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);

    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        var unit = value[^1];
        if (!char.IsLetter(unit))
        {
            return int.TryParse(value, out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        var numberPart = value[..^1];
        if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        if (!double.IsFinite(number))
        {
            return null;
        }

        try
        {
            var duration = unit switch
            {
                's' => TimeSpan.FromSeconds(number),
                'm' => TimeSpan.FromMinutes(number),
                'h' => TimeSpan.FromHours(number),
                'd' => TimeSpan.FromDays(number),
                _ => (TimeSpan?)null,
            };
            return duration is { } value2 && value2 >= TimeSpan.Zero ? value2 : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static string FormatDuration(TimeSpan value)
    {
        if (value.TotalSeconds < 60)
        {
            return $"{value.TotalSeconds:0}s";
        }

        if (value.TotalMinutes < 60)
        {
            return $"{value.TotalMinutes:0}m";
        }

        return $"{value.TotalHours:0}h";
    }
}
