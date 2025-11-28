using System.Text;

namespace AudioVideoEditing.App.Utilities;

internal static class JobNameHelper
{
    public static string BuildOutputPath(string inputFile, string outputRoot)
    {
        var fileName = Path.GetFileNameWithoutExtension(inputFile);
        var extension = Path.GetExtension(inputFile);
        return Path.Combine(outputRoot, $"{fileName}_edited{extension}");
    }

    public static string BuildOutputPath(string inputFile, string outputRoot, string clipLabel, int clipIndex)
    {
        var fileName = Path.GetFileNameWithoutExtension(inputFile);
        var extension = Path.GetExtension(inputFile);
        var slug = Slugify(string.IsNullOrWhiteSpace(clipLabel) ? $"clip-{clipIndex:00}" : clipLabel);
        return Path.Combine(outputRoot, $"{fileName}_{clipIndex:00}_{slug}{extension}");
    }

    public static string BuildJobName(string label, string clipName)
    {
        var slug = Slugify(label);
        var clipSlug = Slugify(clipName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var raw = $"{slug}-{clipSlug}-{timestamp}";
        return raw.Length > 64 ? raw[..64] : raw;
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "job";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "job" : slug;
    }
}
