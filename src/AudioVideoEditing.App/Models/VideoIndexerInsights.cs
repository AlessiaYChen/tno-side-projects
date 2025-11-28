using System.Text.Json.Serialization;

namespace AudioVideoEditing.App.Models;

internal sealed class VideoIndexerInsights
{
    [JsonPropertyName("videos")]
    public List<VideoIndexerVideo> Videos { get; init; } = new();
}

internal sealed class VideoIndexerVideo
{
    [JsonPropertyName("insights")]
    public VideoIndexerVideoInsight Insights { get; init; } = new();
}

internal sealed class VideoIndexerVideoInsight
{
    [JsonPropertyName("transcript")]
    public List<VideoIndexerTranscriptEntry> Transcript { get; init; } = new();
}

internal sealed class VideoIndexerTranscriptEntry
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("instances")]
    public List<VideoIndexerTranscriptInstance> Instances { get; init; } = new();

    [JsonPropertyName("startTime")]
    public string? StartTime { get; init; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; init; }
}

internal sealed class VideoIndexerTranscriptInstance
{
    [JsonPropertyName("start")]
    public string? Start { get; init; }

    [JsonPropertyName("end")]
    public string? End { get; init; }

    [JsonPropertyName("words")]
    public List<VideoIndexerTranscriptWord> Words { get; init; } = new();
}

internal sealed class VideoIndexerTranscriptWord
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("word")]
    public string? Word { get; init; }

    [JsonPropertyName("startTime")]
    public string? StartTime { get; init; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; init; }

    [JsonPropertyName("start")]
    public string? Start { get; init; }

    [JsonPropertyName("end")]
    public string? End { get; init; }
}
