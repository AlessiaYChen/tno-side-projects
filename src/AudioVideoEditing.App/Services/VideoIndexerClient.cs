using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AudioVideoEditing.App.Configuration;
using AudioVideoEditing.App.Models;

namespace AudioVideoEditing.App.Services;

internal sealed class VideoIndexerClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly HttpClient _httpClient;
    private readonly VideoIndexerSettings _settings;
    private readonly string _accessToken;

    public VideoIndexerClient(HttpClient httpClient, VideoIndexerSettings settings, string accessToken)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
    }

    public async Task<VideoIndexerInsightsResult> UploadAndIndexAsync(string videoPath, string videoName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            throw new ArgumentException("Video path is required.", nameof(videoPath));
        }

        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Unable to locate video file for upload.", videoPath);
        }

        var requestUri = BuildVideosUri();
        var uploadUri = AppendQuery(requestUri, new Dictionary<string, string>
        {
            ["name"] = videoName,
            ["privacy"] = "Private",
            ["accessToken"] = _accessToken
        });

        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(videoPath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(videoPath));

        using var uploadResponse = await _httpClient.PostAsync(uploadUri, form, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();
        var uploadPayload = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
        using var uploadDocument = JsonDocument.Parse(uploadPayload);
        var videoId = uploadDocument.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(videoId))
        {
            throw new InvalidOperationException("Video Indexer did not return a video id.");
        }

        var insights = await PollForInsightsAsync(videoId, cancellationToken);
        return new VideoIndexerInsightsResult(videoId, insights);
    }

    private async Task<VideoIndexerInsights> PollForInsightsAsync(string videoId, CancellationToken cancellationToken)
    {
        var indexUri = AppendQuery(BuildVideosUri(videoId, suffix: "Index"), new Dictionary<string, string>
        {
            ["accessToken"] = _accessToken,
            ["language"] = "en-US",
            ["includeStreamingUrls"] = "true"
        });

        while (true)
        {
            using var response = await _httpClient.GetAsync(indexUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(payload);
            var state = document.RootElement.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString()
                : null;

            if (string.Equals(state, "Processed", StringComparison.OrdinalIgnoreCase))
            {
                var insights = JsonSerializer.Deserialize<VideoIndexerInsights>(payload, SerializerOptions)
                    ?? throw new InvalidOperationException("Video Indexer returned an empty insight payload.");
                return insights;
            }

            if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Video Indexer failed to process the video: " + payload);
            }

            Console.WriteLine($"Video Indexer state = {state ?? "Unknown"}. Waiting {_settings.PollingIntervalSeconds}s...");
            await Task.Delay(TimeSpan.FromSeconds(_settings.PollingIntervalSeconds), cancellationToken);
        }
    }

    private Uri BuildVideosUri(string? videoId = null, string? suffix = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.AccountId) || string.IsNullOrWhiteSpace(_settings.Location))
        {
            throw new InvalidOperationException("Video Indexer account id and location are required.");
        }

        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        var builder = new UriBuilder($"{baseUrl}/{_settings.Location}/Accounts/{_settings.AccountId}/Videos");
        if (!string.IsNullOrWhiteSpace(videoId))
        {
            builder.Path += "/" + videoId;
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                builder.Path += "/" + suffix;
            }
        }

        return builder.Uri;
    }

    private static Uri AppendQuery(Uri baseUri, IReadOnlyDictionary<string, string> query)
    {
        var separator = string.IsNullOrWhiteSpace(baseUri.Query) ? '?' : '&';
        var builder = new StringBuilder(baseUri.ToString());
        foreach (var kvp in query)
        {
            builder.Append(separator);
            builder.Append(Uri.EscapeDataString(kvp.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(kvp.Value));
            separator = '&';
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }
}

internal sealed record VideoIndexerInsightsResult(string VideoId, VideoIndexerInsights Insights);
