namespace AudioVideoEditing.App.Models;

internal sealed record ClipRange(TimeSpan Start, TimeSpan End)
{
    public static ClipRange FromStrings(string? start, string? end)
    {
        if (!TimeSpan.TryParse(start, out var startSpan))
        {
            throw new FormatException($"Unable to parse start timestamp '{start}'.");
        }

        if (!TimeSpan.TryParse(end, out var endSpan))
        {
            throw new FormatException($"Unable to parse end timestamp '{end}'.");
        }

        if (endSpan <= startSpan)
        {
            throw new ArgumentException("End timestamp must be after start timestamp.");
        }

        return new ClipRange(startSpan, endSpan);
    }
}
