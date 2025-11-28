using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioVideoEditing.App.Configuration;
using AudioVideoEditing.App.Models;
using AudioVideoEditing.App.Utilities;

namespace AudioVideoEditing.App.Services;

internal sealed class OpenAiStoryLocator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiSettings _settings;
    private readonly string _apiKey;

    private const string TopicSystemPrompt = "You are a video editor assistant. I will give you a transcript with timestamps. Identify the start and end timestamps for the given topic and return JSON only.";
    private const string NewsSystemPrompt = @"You segment radio news transcripts into separate news stories.
A ""story"" is a self-contained news item on a single topic.
Do not split a story just because the speaker changes if they are still on the same topic.
Do split when the topic clearly changes.";

    private const string NewsUserPromptTemplate = @"You are given blocks of transcript with IDs and timestamps.

Each block:
[ID:<int>][<start>-<end>] <text>

Your task:
1. Assign a story_id to each block. Blocks with the same story_id belong to the same story.
2. story_id must be integers starting at 1 and increasing; do not skip numbers.
3. A story must contain at least 2 blocks unless the broadcast is extremely short.

Output JSON in this format only:

{{
  ""blocks"": [
    {{""id"": 1, ""story_id"": 1}},
    {{""id"": 2, ""story_id"": 1}},
    {{""id"": 3, ""story_id"": 2}}
  ]
}}

Blocks:
{0}";

    public OpenAiStoryLocator(HttpClient httpClient, OpenAiSettings settings, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public async Task<ClipRange> LocateClipAsync(string transcript, string topic, string? deploymentOverride, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new ArgumentException("Transcript cannot be empty.", nameof(transcript));
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("Topic cannot be empty.", nameof(topic));
        }

        var userPrompt = $"Topic: {topic}\n\nTranscript:\n{transcript}";
        var text = await ExecuteChatAsync(deploymentOverride, TopicSystemPrompt, userPrompt, cancellationToken);
        var parsed = JsonSerializer.Deserialize<ClipRangeWire>(text, SerializerOptions)
            ?? throw new InvalidOperationException("Unable to parse GPT response as JSON.");

        return ClipRange.FromStrings(parsed.Start, parsed.End);
    }

    public async Task<IReadOnlyList<NewsClipPlan>> PlanNewsClipsAsync(TranscriptDocument transcript, string? deploymentOverride, string? llmOutputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (string.IsNullOrWhiteSpace(transcript.Text))
        {
            throw new ArgumentException("Transcript cannot be empty.", nameof(transcript));
        }

        var prompt = string.Format(CultureInfo.InvariantCulture, NewsUserPromptTemplate, transcript.Text.Trim());
        var text = await ExecuteChatAsync(deploymentOverride, NewsSystemPrompt, prompt, cancellationToken);
        if (!string.IsNullOrWhiteSpace(llmOutputPath))
        {
            await LlmOutputStore.SaveAsync(llmOutputPath, text, cancellationToken);
        }
        var envelope = JsonSerializer.Deserialize<StoryAssignmentEnvelope>(text, SerializerOptions)
            ?? throw new InvalidOperationException("Unable to parse GPT response for news clips.");

        var plans = BuildNewsClipPlans(transcript, envelope);
        if (plans.Count == 0)
        {
            throw new InvalidOperationException("Azure OpenAI did not return any clips.");
        }

        return plans;
    }

    private IReadOnlyList<NewsClipPlan> BuildNewsClipPlans(TranscriptDocument transcript, StoryAssignmentEnvelope envelope)
    {
        if (envelope.Blocks.Count == 0)
        {
            throw new InvalidOperationException("Azure OpenAI returned an empty block assignment list.");
        }

        var blockLookup = transcript.Blocks.ToDictionary(block => block.Id);
        var seenBlockIds = new HashSet<int>();
        foreach (var assignment in envelope.Blocks)
        {
            if (!blockLookup.ContainsKey(assignment.Id))
            {
                throw new InvalidOperationException($"Azure OpenAI referenced unknown block id {assignment.Id}.");
            }

            if (!seenBlockIds.Add(assignment.Id))
            {
                throw new InvalidOperationException($"Azure OpenAI returned duplicate assignment for block id {assignment.Id}.");
            }
        }

        if (seenBlockIds.Count != blockLookup.Count)
        {
            throw new InvalidOperationException("Azure OpenAI did not assign every block to a story.");
        }

        var orderedGroups = envelope.Blocks
            .GroupBy(block => block.StoryId)
            .OrderBy(group => blockLookup[group.First().Id].Start)
            .ToList();

        if (orderedGroups.Count == 0)
        {
            return Array.Empty<NewsClipPlan>();
        }

        var plans = new List<NewsClipPlan>(orderedGroups.Count);
        var normalizedStoryId = 1;
        foreach (var group in orderedGroups)
        {
            var orderedBlocks = group
                .Select(assignment => blockLookup[assignment.Id])
                .OrderBy(block => block.Start)
                .ToList();

            if (orderedBlocks.Count == 0)
            {
                continue;
            }

            var start = orderedBlocks.First().Start;
            var end = orderedBlocks.Last().End;
            if (end <= start)
            {
                end = start + TimeSpan.FromSeconds(1);
            }

            var title = BuildClipTitle(orderedBlocks, normalizedStoryId);
            plans.Add(new NewsClipPlan(title, new ClipRange(start, end)));
            normalizedStoryId++;
        }

        return plans;
    }

    private static string BuildClipTitle(IReadOnlyList<TranscriptBlock> blocks, int storyId)
    {
        var source = string.Join(' ', blocks.Select(block => block.Text));
        if (string.IsNullOrWhiteSpace(source))
        {
            return $"story-{storyId:00}";
        }

        var candidates = source
            .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim().Trim('"', '\'', '.', ',', ';', ':', '!', '?'))
            .Where(word => word.Length > 0)
            .Take(6)
            .ToList();

        if (candidates.Count == 0)
        {
            return $"story-{storyId:00}";
        }

        return string.Join(' ', candidates);
    }

    private async Task<string> ExecuteChatAsync(string? deploymentOverride, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var deployment = string.IsNullOrWhiteSpace(deploymentOverride)
            ? _settings.DeploymentName
            : deploymentOverride;

        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException("Azure OpenAI deployment name must be provided.");
        }

        var requestUri = BuildChatCompletionsUri(deployment);
        var payload = new
        {
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = _settings.Temperature,
            response_format = new { type = "json_object" }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("api-key", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractContent(json);
    }

    private string BuildChatCompletionsUri(string deployment)
    {
        if (string.IsNullOrWhiteSpace(_settings.Endpoint))
        {
            throw new InvalidOperationException("OpenAi:Endpoint configuration is required.");
        }

        var apiVersion = string.IsNullOrWhiteSpace(_settings.ApiVersion) ? "2023-05-15" : _settings.ApiVersion;
        var baseUrl = _settings.Endpoint.TrimEnd('/');
        return $"{baseUrl}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
    }

    private static string ExtractContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Azure OpenAI response does not contain choices.");
        }

        var message = choices[0].GetProperty("message");
        if (message.TryGetProperty("content", out var content))
        {
            return content.ValueKind switch
            {
                JsonValueKind.Array => string.Join(Environment.NewLine, content.EnumerateArray().Select(chunk => chunk.TryGetProperty("text", out var textElement) ? textElement.GetString() : chunk.GetString())),
                _ => content.GetString() ?? string.Empty
            };
        }

        if (message.TryGetProperty("contentFilterResults", out _))
        {
            throw new InvalidOperationException("Azure OpenAI blocked the response via content filtering.");
        }

        throw new InvalidOperationException("Azure OpenAI did not return message content.");
    }

    private sealed class ClipRangeWire
    {
        public string? Start { get; init; }
        public string? End { get; init; }
    }

    private sealed class StoryAssignmentEnvelope
    {
        [JsonPropertyName("blocks")]
        public List<StoryBlockAssignment> Blocks { get; init; } = new();
    }

    private sealed class StoryBlockAssignment
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("story_id")]
        public int StoryId { get; init; }
    }
}




