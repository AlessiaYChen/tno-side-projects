using System.Text.Json;
using AudioVideoEditing.App.Models;

namespace AudioVideoEditing.App.Utilities;

internal static class InsightsCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string GetCachePath(string cacheRoot, string inputFile)
    {
        var fileName = Path.GetFileName(inputFile);
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "insights" : fileName;
        return Path.Combine(cacheRoot, safeName + ".insights.json");
    }

    public static async Task<VideoIndexerInsights> LoadAsync(string cacheRoot, string inputFile, CancellationToken cancellationToken)
    {
        var path = GetCachePath(cacheRoot, inputFile);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Cached Video Indexer insights not found for {Path.GetFileName(inputFile)}. Run without --skip-video-indexer to generate the cache first.", path);
        }

        await using var stream = File.OpenRead(path);
        var insights = await JsonSerializer.DeserializeAsync<VideoIndexerInsights>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Cached insights file '{path}' is empty or invalid.");
        return insights;
    }

    public static async Task SaveAsync(string cacheRoot, string inputFile, VideoIndexerInsights insights, CancellationToken cancellationToken)
    {
        if (insights is null)
        {
            throw new ArgumentNullException(nameof(insights));
        }

        Directory.CreateDirectory(cacheRoot);
        var path = GetCachePath(cacheRoot, inputFile);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, insights, SerializerOptions, cancellationToken);
    }
}
