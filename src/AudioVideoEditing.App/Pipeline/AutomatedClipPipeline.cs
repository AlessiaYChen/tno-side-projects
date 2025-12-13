using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using AudioVideoEditing.App.Configuration;
using AudioVideoEditing.App.Models;
using AudioVideoEditing.App.Services;
using AudioVideoEditing.App.Utilities;

namespace AudioVideoEditing.App.Pipeline;

internal sealed class AutomatedClipPipeline
{
    private readonly VideoIndexerClient _videoIndexerClient;
    private readonly OpenAiStoryLocator _storyLocator;
    private readonly FfmpegClipCutter _clipCutter;
    private static readonly TimeSpan TailPadding = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions TranscriptWindowSerializerOptions = new()
    {
        WriteIndented = true
    };

    public AutomatedClipPipeline(VideoIndexerClient videoIndexerClient, OpenAiStoryLocator storyLocator, FfmpegClipCutter clipCutter)
    {
        _videoIndexerClient = videoIndexerClient ?? throw new ArgumentNullException(nameof(videoIndexerClient));
        _storyLocator = storyLocator ?? throw new ArgumentNullException(nameof(storyLocator));
        _clipCutter = clipCutter ?? throw new ArgumentNullException(nameof(clipCutter));
    }

    public async Task RunAsync(AppOptions options, CancellationToken cancellationToken)
    {
        var files = EnumerateFiles(options);
        if (files.Count == 0)
        {
            Console.WriteLine($"No files matching {string.Join(',', options.FileExtensions)} were found under {options.InputRoot}.");
            return;
        }

        foreach (var file in files)
        {
            try
            {
                await ProcessFileAsync(file, options, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private IReadOnlyList<string> EnumerateFiles(AppOptions options)
    {
        var extensions = options.FileExtensions.Count == 0 ? new[] { ".mp4" } : options.FileExtensions;
        return Directory.EnumerateFiles(options.InputRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task ProcessFileAsync(string filePath, AppOptions options, CancellationToken cancellationToken)
    {
        var outputPath = JobNameHelper.BuildOutputPath(filePath, options.OutputRoot);
        var jobName = JobNameHelper.BuildJobName(options.JobLabel, Path.GetFileNameWithoutExtension(filePath));

        VideoIndexerInsights insights;
        if (options.SkipVideoIndexer)
        {
            Console.WriteLine("Skipping Video Indexer and loading cached insights...");
            insights = await InsightsCache.LoadAsync(options.InsightsCacheRoot, filePath, cancellationToken);
        }
        else
        {
            Console.WriteLine($"Uploading {Path.GetFileName(filePath)} to Azure Video Indexer...");
            var viResult = await _videoIndexerClient.UploadAndIndexAsync(filePath, jobName, cancellationToken);
            await InsightsCache.SaveAsync(options.InsightsCacheRoot, filePath, viResult.Insights, cancellationToken);
            insights = viResult.Insights;
        }

        var transcript = TranscriptFormatter.Flatten(insights);

        if (options.GenerateNewsClips)
        {
            var llmOutputPath = LlmOutputStore.BuildPath(options.LlmOutputRoot, filePath, ".news.json");
            await GenerateNewsClipsAsync(filePath, transcript, insights, options, llmOutputPath, cancellationToken);
            return;
        }

        await GenerateTopicClipAsync(filePath, transcript, insights, options, outputPath, cancellationToken);
    }

    private async Task GenerateTopicClipAsync(string filePath, TranscriptDocument transcript, VideoIndexerInsights insights, AppOptions options, string outputPath, CancellationToken cancellationToken)
    {
        Console.WriteLine("Calling Azure OpenAI to locate the requested topic...");
        var clipRange = await _storyLocator.LocateClipAsync(transcript.Text, options.TopicQuery, options.OpenAiDeployment, cancellationToken);
        await SaveTopicTranscriptWindowAsync(filePath, insights, options, clipRange, cancellationToken);
        var paddedRange = ApplyTailPadding(clipRange);
        Console.WriteLine($"Topic '{options.TopicQuery}' located between {clipRange.Start:c} and {clipRange.End:c}. Cutting {paddedRange.Start:c}-{paddedRange.End:c} locally (includes +{TailPadding.TotalSeconds:0.#}s tail padding).");

        await _clipCutter.CutAsync(filePath, paddedRange, outputPath, options.DryRun, cancellationToken);
        Console.WriteLine($"[SUCCESS] {Path.GetFileName(filePath)} -> {outputPath}");
    }

    private async Task GenerateNewsClipsAsync(string filePath, TranscriptDocument transcript, VideoIndexerInsights insights, AppOptions options, string llmOutputPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<NewsClipPlan> clips;
        if (options.UseVideoIndexerNewsClips)
        {
            Console.WriteLine("Using Video Indexer topic appearances to plan news clips...");
            clips = PlanVideoIndexerNewsClips(insights);
        }
        else
        {
            Console.WriteLine("Calling Azure OpenAI to segment the transcript into news clips...");
            clips = await _storyLocator.PlanNewsClipsAsync(transcript, options.OpenAiDeployment, llmOutputPath, cancellationToken);
        }

        await SaveNewsTranscriptWindowsAsync(filePath, clips, insights, options, cancellationToken);

        var clipIndex = 1;
        foreach (var clip in clips)
        {
            var output = JobNameHelper.BuildOutputPath(filePath, options.OutputRoot, clip.Title, clipIndex);
            var paddedRange = ApplyTailPadding(clip.Range);
            Console.WriteLine($"Cutting '{clip.Title}' between {clip.Range.Start:c} and {clip.Range.End:c} -> {output} (tail padding applied).");
            await _clipCutter.CutAsync(filePath, paddedRange, output, options.DryRun, cancellationToken);
            clipIndex++;
        }

        Console.WriteLine($"[SUCCESS] Produced {clips.Count} clips from {Path.GetFileName(filePath)}.");
    }

    private static async Task SaveTopicTranscriptWindowAsync(string filePath, VideoIndexerInsights insights, AppOptions options, ClipRange clipRange, CancellationToken cancellationToken)
    {
        var windowPath = LlmOutputStore.BuildPath(options.LlmOutputRoot, filePath, ".topic.window.json");
        var record = BuildTranscriptWindowRecord(1, options.TopicQuery, clipRange, insights);
        var json = JsonSerializer.Serialize(record, TranscriptWindowSerializerOptions);
        await LlmOutputStore.SaveAsync(windowPath, json, cancellationToken);
        Console.WriteLine($"Saved VI transcript window -> {windowPath}");
    }

    private static async Task SaveNewsTranscriptWindowsAsync(string filePath, IReadOnlyList<NewsClipPlan> clips, VideoIndexerInsights insights, AppOptions options, CancellationToken cancellationToken)
    {
        if (clips.Count == 0)
        {
            return;
        }

        var windowsPath = LlmOutputStore.BuildPath(options.LlmOutputRoot, filePath, ".news.windows.json");
        var payload = new List<TranscriptWindowRecord>(clips.Count);
        var clipIndex = 1;
        foreach (var clip in clips)
        {
            payload.Add(BuildTranscriptWindowRecord(clipIndex, clip.Title, clip.Range, insights));
            clipIndex++;
        }

        var json = JsonSerializer.Serialize(payload, TranscriptWindowSerializerOptions);
        await LlmOutputStore.SaveAsync(windowsPath, json, cancellationToken);
        Console.WriteLine($"Saved VI transcript windows -> {windowsPath}");
    }

    private static TranscriptWindowRecord BuildTranscriptWindowRecord(int clipIndex, string? title, ClipRange range, VideoIndexerInsights insights)
    {
        var transcript = TranscriptFormatter.BuildWindow(insights, range.Start, range.End);
        return new TranscriptWindowRecord(clipIndex, title, TimestampHelper.Format(range.Start), TimestampHelper.Format(range.End), transcript);
    }

    private sealed record TranscriptWindowRecord(int ClipIndex, string? Title, string Start, string End, string Transcript);

    private static IReadOnlyList<NewsClipPlan> PlanVideoIndexerNewsClips(VideoIndexerInsights insights)
    {
        if (insights.Videos.Count == 0)
        {
            throw new InvalidOperationException("Video Indexer insights payload does not contain any videos.");
        }

        var topicSegments = new List<NewsClipPlan>();
        var videoInsights = insights.Videos[0].Insights;
        foreach (var topic in videoInsights.Topics)
        {
            if (topic.Appearances.Count == 0)
            {
                continue;
            }

            foreach (var appearance in topic.Appearances)
            {
                var range = BuildClipRange(appearance);
                if (range is null)
                {
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(topic.Name) ? string.Empty : topic.Name.Trim();
                topicSegments.Add(new NewsClipPlan(title, range));
            }
        }

        if (topicSegments.Count == 0)
        {
            throw new InvalidOperationException("Video Indexer topics did not contain usable clip segments.");
        }

        var ordered = topicSegments
            .OrderBy(plan => plan.Range.Start)
            .Select((plan, index) =>
            {
                var title = string.IsNullOrWhiteSpace(plan.Title) ? $"vi-story-{index + 1:00}" : plan.Title;
                return plan with { Title = title };
            })
            .ToList();

        return ordered;
    }

    private static ClipRange? BuildClipRange(VideoIndexerAppearance appearance)
    {
        var start = ResolveAppearanceTime(appearance.StartSeconds, appearance.StartTime, appearance.Start);
        var end = ResolveAppearanceTime(appearance.EndSeconds, appearance.EndTime, appearance.End);
        if (start is null || end is null)
        {
            return null;
        }

        var startValue = start.Value < TimeSpan.Zero ? TimeSpan.Zero : start.Value;
        var endValue = end.Value < startValue ? startValue : end.Value;
        if (endValue <= startValue)
        {
            endValue = startValue + TimeSpan.FromSeconds(1);
        }

        return new ClipRange(startValue, endValue);
    }

    private static TimeSpan? ResolveAppearanceTime(double? seconds, string? primary, string? fallback)
    {
        if (seconds.HasValue)
        {
            var value = seconds.Value;
            if (!double.IsNaN(value) && !double.IsInfinity(value))
            {
                if (value < 0)
                {
                    value = 0;
                }

                return TimeSpan.FromSeconds(value);
            }
        }

        return TimestampHelper.Parse(primary) ?? TimestampHelper.Parse(fallback);
    }

    private static ClipRange ApplyTailPadding(ClipRange range)
    {
        if (TailPadding <= TimeSpan.Zero)
        {
            return range;
        }

        var paddedEnd = range.End + TailPadding;
        if (paddedEnd <= range.Start)
        {
            return range;
        }

        return range with { End = paddedEnd };
    }
}
