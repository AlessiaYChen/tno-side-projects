using System.Collections.Generic;
using System.Linq;

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
            await GenerateNewsClipsAsync(filePath, transcript, options, llmOutputPath, cancellationToken);
            return;
        }

        await GenerateTopicClipAsync(filePath, transcript, options, outputPath, cancellationToken);
    }

    private async Task GenerateTopicClipAsync(string filePath, TranscriptDocument transcript, AppOptions options, string outputPath, CancellationToken cancellationToken)
    {
        Console.WriteLine("Calling Azure OpenAI to locate the requested topic...");
        var clipRange = await _storyLocator.LocateClipAsync(transcript.Text, options.TopicQuery, options.OpenAiDeployment, cancellationToken);
        var paddedRange = ApplyTailPadding(clipRange);
        Console.WriteLine($"Topic '{options.TopicQuery}' located between {clipRange.Start:c} and {clipRange.End:c}. Cutting {paddedRange.Start:c}-{paddedRange.End:c} locally (includes +{TailPadding.TotalSeconds:0.#}s tail padding).");

        await _clipCutter.CutAsync(filePath, paddedRange, outputPath, options.DryRun, cancellationToken);
        Console.WriteLine($"[SUCCESS] {Path.GetFileName(filePath)} -> {outputPath}");
    }

    private async Task GenerateNewsClipsAsync(string filePath, TranscriptDocument transcript, AppOptions options, string llmOutputPath, CancellationToken cancellationToken)
    {
        Console.WriteLine("Calling Azure OpenAI to segment the transcript into news clips...");
        var clips = await _storyLocator.PlanNewsClipsAsync(transcript, options.OpenAiDeployment, llmOutputPath, cancellationToken);

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

