using System.Collections.Generic;
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

    [JsonPropertyName("speakers")]
    public List<VideoIndexerSpeaker> Speakers { get; init; } = new();

    [JsonPropertyName("keywords")]
    public List<VideoIndexerKeyword> Keywords { get; init; } = new();

    [JsonPropertyName("topics")]
    public List<VideoIndexerTopic> Topics { get; init; } = new();
}

internal sealed class VideoIndexerTranscriptEntry
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;


    [JsonPropertyName("speakerId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SpeakerId { get; init; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; init; }

    [JsonPropertyName("sentiment")]
    public string? Sentiment { get; init; }
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
internal sealed class VideoIndexerSpeaker
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed class VideoIndexerTopic
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("appearances")]
    public List<VideoIndexerAppearance> Appearances { get; init; } = new();
}

internal sealed class VideoIndexerKeyword
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("appearances")]
    public List<VideoIndexerAppearance> Appearances { get; init; } = new();
}

internal sealed class VideoIndexerAppearance
{
    [JsonPropertyName("startTime")]
    public string? StartTime { get; init; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; init; }

    [JsonPropertyName("start")]
    public string? Start { get; init; }

    [JsonPropertyName("end")]
    public string? End { get; init; }

    [JsonPropertyName("startSeconds")]
    public double? StartSeconds { get; init; }

    [JsonPropertyName("endSeconds")]
    public double? EndSeconds { get; init; }
}
