using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AudioVideoEditing.App.Models;

namespace AudioVideoEditing.App.Utilities;

internal static class TranscriptFormatter
{
    private static readonly TimeSpan BlockDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisplayOverlap = TimeSpan.FromSeconds(3);

    public static TranscriptDocument Flatten(VideoIndexerInsights insights)
    {
        if (insights.Videos.Count == 0)
        {
            throw new InvalidOperationException("Video Indexer insights payload does not contain any videos.");
        }

        var video = insights.Videos[0];
        var videoInsights = video.Insights;
        var transcript = videoInsights.Transcript;
        if (transcript.Count == 0)
        {
            throw new InvalidOperationException("Transcript entries are empty.");
        }

        var timeline = ExtractWordTimeline(transcript);
        if (timeline.Count == 0)
        {
            throw new InvalidOperationException("Transcript could not be flattened because all entries were empty.");
        }

        var blocks = BuildFixedBlocks(timeline);
        var enrichedBlocks = AttachMetadata(blocks, timeline, videoInsights);
        var formatted = FormatBlocks(enrichedBlocks, timeline);
        return new TranscriptDocument(formatted, enrichedBlocks);
    }

    public static string BuildWindow(VideoIndexerInsights insights, TimeSpan windowStart, TimeSpan windowEnd)
    {
        if (insights is null)
        {
            throw new ArgumentNullException(nameof(insights));
        }

        if (insights.Videos.Count == 0)
        {
            throw new InvalidOperationException("Video Indexer insights payload does not contain any videos.");
        }

        var transcriptEntries = insights.Videos[0].Insights.Transcript;
        if (transcriptEntries.Count == 0)
        {
            return string.Empty;
        }

        var timeline = ExtractWordTimeline(transcriptEntries);
        if (timeline.Count == 0)
        {
            return string.Empty;
        }

        if (windowEnd < windowStart)
        {
            (windowStart, windowEnd) = (windowEnd, windowStart);
        }

        var normalizedStart = windowStart < TimeSpan.Zero ? TimeSpan.Zero : windowStart;
        var normalizedEnd = windowEnd < normalizedStart ? normalizedStart : windowEnd;
        var lastEnd = timeline[^1].End;
        if (normalizedStart >= lastEnd)
        {
            return string.Empty;
        }

        if (normalizedEnd > lastEnd)
        {
            normalizedEnd = lastEnd;
        }

        if (normalizedEnd <= normalizedStart)
        {
            normalizedEnd = normalizedStart + TimeSpan.FromSeconds(1);
            if (normalizedEnd > lastEnd)
            {
                normalizedEnd = lastEnd;
            }
        }

        if (normalizedEnd <= normalizedStart)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("Window start: ");
        builder.AppendLine(TimestampHelper.FormatWithMilliseconds(normalizedStart));
        builder.AppendLine();

        var hasLines = AppendEntryLines(builder, transcriptEntries, normalizedStart, normalizedEnd);
        if (!hasLines)
        {
            var fallback = BuildWindowFromTimeline(timeline, normalizedStart, normalizedEnd);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                builder.Append("[+00.0s] \"");
                builder.Append(fallback.Trim());
                builder.AppendLine("\"");
                hasLines = true;
            }
        }

        builder.AppendLine();
        builder.Append("Window end: ");
        builder.AppendLine(TimestampHelper.FormatWithMilliseconds(normalizedEnd));

        return hasLines ? builder.ToString() : string.Empty;
    }

    private static IReadOnlyList<WordTiming> ExtractWordTimeline(IReadOnlyList<VideoIndexerTranscriptEntry> transcript)
    {
        var words = new List<WordTiming>();

        foreach (var entry in transcript)
        {
            var entryStart = TimestampHelper.Parse(entry.StartTime) ?? TimeSpan.Zero;
            var entryEnd = TimestampHelper.Parse(entry.EndTime) ?? entryStart;
            var entryText = entry.Text?.Trim();
            var speakerId = entry.SpeakerId;
            var speakerName = entry.Speaker;
            var sentiment = entry.Sentiment;

            if (entry.Instances.Count == 0)
            {
                AddWord(words, entryStart, entryEnd, entryText, speakerId, speakerName, sentiment);
                continue;
            }

            foreach (var instance in entry.Instances)
            {
                var instanceStart = TimestampHelper.Parse(instance.Start) ?? entryStart;
                var instanceEnd = TimestampHelper.Parse(instance.End) ?? entryEnd;

                if (instance.Words.Count == 0)
                {
                    AddWord(words, instanceStart, instanceEnd, entryText, speakerId, speakerName, sentiment);
                    continue;
                }

                foreach (var word in instance.Words)
                {
                    var text = string.IsNullOrWhiteSpace(word.Text) ? word.Word : word.Text;
                    var wordStart = TimestampHelper.Parse(word.StartTime ?? word.Start) ?? instanceStart;
                    var wordEnd = TimestampHelper.Parse(word.EndTime ?? word.End) ?? instanceEnd;
                    AddWord(words, wordStart, wordEnd, text, speakerId, speakerName, sentiment);
                }
            }
        }

        words.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return words;
    }

    private static List<TranscriptBlock> BuildFixedBlocks(IReadOnlyList<WordTiming> timeline)
    {
        var blocks = new List<TranscriptBlock>();
        var blockText = new StringBuilder();
        var nextBlockId = 1;

        var blockStart = AlignBlockStart(timeline[0].Start);
        var blockEnd = blockStart + BlockDuration;
        var blockLastEnd = blockStart;

        foreach (var word in timeline)
        {
            while (word.Start >= blockEnd)
            {
                FlushBlock(blocks, blockText, blockStart, blockLastEnd, ref nextBlockId);
                blockStart = blockEnd;
                blockEnd = blockStart + BlockDuration;
                blockLastEnd = blockStart;
            }

            AppendWord(blockText, word.Text);
            if (word.End > blockLastEnd)
            {
                blockLastEnd = word.End;
            }
        }

        FlushBlock(blocks, blockText, blockStart, blockLastEnd, ref nextBlockId);

        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("Transcript could not be flattened because all entries were empty.");
        }

        return blocks;
    }

    private static IReadOnlyList<TranscriptBlock> AttachMetadata(IReadOnlyList<TranscriptBlock> blocks, IReadOnlyList<WordTiming> timeline, VideoIndexerVideoInsight insight)
    {
        if (blocks.Count == 0)
        {
            return blocks;
        }

        var speakerMap = insight.Speakers
            .Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name))
            .ToDictionary(s => s.Id!, s => s.Name!, StringComparer.OrdinalIgnoreCase);

        var hints = BuildTopicHints(insight);
        var enriched = new List<TranscriptBlock>(blocks.Count);

        foreach (var block in blocks)
        {
            var words = timeline.Where(word => word.Start < block.End && word.End > block.Start).ToList();
            var speaker = SelectSpeaker(words, speakerMap);
            var sentiment = SelectSentiment(words);
            var topicHints = SelectTopicHints(block, hints);

            enriched.Add(block with
            {
                Speaker = speaker,
                Sentiment = sentiment,
                TopicHints = topicHints
            });
        }

        return enriched;
    }

    private static List<HintOccurrence> BuildTopicHints(VideoIndexerVideoInsight insight)
    {
        var hints = new List<HintOccurrence>();

        foreach (var topic in insight.Topics)
        {
            AddHintOccurrences(topic.Name, topic.Appearances, hints);
        }

        foreach (var keyword in insight.Keywords)
        {
            AddHintOccurrences(keyword.Name, keyword.Appearances, hints);
        }

        return hints;
    }

    private static void AddHintOccurrences(string name, List<VideoIndexerAppearance> appearances, ICollection<HintOccurrence> target)
    {
        if (string.IsNullOrWhiteSpace(name) || appearances.Count == 0)
        {
            return;
        }

        foreach (var appearance in appearances)
        {
            var range = ResolveAppearanceRange(appearance);
            if (!range.HasValue)
            {
                continue;
            }

            target.Add(new HintOccurrence(range.Value.Start, range.Value.End, name.Trim()));
        }
    }

    private static (TimeSpan Start, TimeSpan End)? ResolveAppearanceRange(VideoIndexerAppearance appearance)
    {
        if (appearance is null)
        {
            return null;
        }

        var start = TimestampHelper.Parse(appearance.StartTime ?? appearance.Start)
            ?? (appearance.StartSeconds.HasValue ? TimeSpan.FromSeconds(appearance.StartSeconds.Value) : null);
        var end = TimestampHelper.Parse(appearance.EndTime ?? appearance.End)
            ?? (appearance.EndSeconds.HasValue ? TimeSpan.FromSeconds(appearance.EndSeconds.Value) : null);

        if (!start.HasValue)
        {
            return null;
        }

        var safeEnd = end ?? start.Value;
        if (safeEnd <= start.Value)
        {
            safeEnd = start.Value + TimeSpan.FromSeconds(1);
        }

        return (start.Value, safeEnd);
    }

    private static string? SelectSpeaker(IReadOnlyList<WordTiming> words, IReadOnlyDictionary<string, string> speakerMap)
    {
        if (words.Count == 0)
        {
            return null;
        }

        var buckets = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            var label = ResolveSpeakerLabel(word, speakerMap);
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var normalized = label.Trim();
            if (buckets.TryGetValue(normalized, out var aggregate))
            {
                buckets[normalized] = (aggregate.Display, aggregate.Count + 1);
            }
            else
            {
                buckets[normalized] = (normalized, 1);
            }
        }

        if (buckets.Count == 0)
        {
            return null;
        }

        return buckets
            .OrderByDescending(kvp => kvp.Value.Count)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .First().Value.Display;
    }

    private static string? ResolveSpeakerLabel(WordTiming word, IReadOnlyDictionary<string, string> speakerMap)
    {
        if (!string.IsNullOrWhiteSpace(word.SpeakerId) && speakerMap.TryGetValue(word.SpeakerId, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        if (!string.IsNullOrWhiteSpace(word.SpeakerName))
        {
            return word.SpeakerName;
        }

        if (!string.IsNullOrWhiteSpace(word.SpeakerId))
        {
            return $"Speaker {word.SpeakerId}";
        }

        return null;
    }

    private static string? SelectSentiment(IReadOnlyList<WordTiming> words)
    {
        if (words.Count == 0)
        {
            return null;
        }

        var buckets = new Dictionary<string, (string Value, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            var sentiment = NormalizeSentiment(word.Sentiment);
            if (sentiment is null)
            {
                continue;
            }

            if (buckets.TryGetValue(sentiment, out var aggregate))
            {
                buckets[sentiment] = (aggregate.Value, aggregate.Count + 1);
            }
            else
            {
                buckets[sentiment] = (sentiment, 1);
            }
        }

        if (buckets.Count == 0)
        {
            return null;
        }

        return buckets
            .OrderByDescending(kvp => kvp.Value.Count)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .First().Value.Value;
    }

    private static IReadOnlyList<string> SelectTopicHints(TranscriptBlock block, IReadOnlyList<HintOccurrence> hints)
    {
        if (hints.Count == 0)
        {
            return Array.Empty<string>();
        }

        var matches = hints
            .Where(hint => hint.Start < block.End && hint.End > block.Start)
            .Select(hint => hint.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(text => text.Trim())
            .ToList();

        return matches.Count == 0 ? Array.Empty<string>() : matches;
    }

    private static string? NormalizeSentiment(string? sentiment)
    {
        if (string.IsNullOrWhiteSpace(sentiment))
        {
            return null;
        }

        return sentiment.Trim();
    }

    private static string FormatBlocks(IReadOnlyList<TranscriptBlock> blocks, IReadOnlyList<WordTiming> timeline)
    {
        var builder = new StringBuilder();
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var lastTimelineEnd = timeline[^1].End;
        var timelineIndex = 0;
        var baseStart = blocks[0].Start;

        foreach (var block in blocks)
        {
            var overlapShift = TimeSpan.FromTicks(DisplayOverlap.Ticks * (block.Id - 1));
            var windowStart = block.Start - overlapShift;
            if (windowStart < TimeSpan.Zero)
            {
                windowStart = TimeSpan.Zero;
            }

            if (windowStart > lastTimelineEnd)
            {
                windowStart = lastTimelineEnd;
            }

            var windowEnd = windowStart + BlockDuration;
            if (windowEnd > lastTimelineEnd)
            {
                windowEnd = lastTimelineEnd;
            }

            if (windowEnd <= windowStart)
            {
                windowStart = block.Start;
                windowEnd = block.End;
            }

            var textLine = ExtractWindowText(timeline, ref timelineIndex, windowStart, windowEnd);
            if (textLine.Length == 0)
            {
                textLine = block.Text;
            }

            var displayStart = windowStart - baseStart;
            if (displayStart < TimeSpan.Zero)
            {
                displayStart = TimeSpan.Zero;
            }

            var displayEnd = displayStart + (windowEnd - windowStart);
            if (displayEnd < displayStart)
            {
                displayEnd = displayStart;
            }

            builder.Append("[ID:");
            builder.Append(block.Id);
            builder.Append("][");
            builder.Append(TimestampHelper.Format(displayStart));
            builder.Append('-');
            builder.Append(TimestampHelper.Format(displayEnd));
            builder.Append(']');

            var metadata = BuildMetadataSuffix(block);
            if (metadata.Length > 0)
            {
                builder.Append(' ');
                builder.Append(metadata);
            }

            builder.Append(' ');
            builder.AppendLine(textLine);
        }

        return builder.ToString();
    }

    private static string BuildMetadataSuffix(TranscriptBlock block)
    {
        var metadataParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(block.Speaker))
        {
            metadataParts.Add($"speaker={block.Speaker}");
        }

        var sentiment = NormalizeSentiment(block.Sentiment);
        if (!string.IsNullOrWhiteSpace(sentiment))
        {
            metadataParts.Add($"sentiment={sentiment}");
        }

        var hints = (block.TopicHints ?? Array.Empty<string>())
            .Where(hint => !string.IsNullOrWhiteSpace(hint))
            .Select(hint => hint.Trim())
            .Take(3)
            .ToList();
        if (hints.Count > 0)
        {
            var quoted = string.Join(", ", hints.Select(hint => $"\"{hint}\""));
            metadataParts.Add($"topic hints: {quoted}");
        }

        if (metadataParts.Count == 0)
        {
            return string.Empty;
        }

        return $"({string.Join(", ", metadataParts)})";
    }

    private static TimeSpan AlignBlockStart(TimeSpan timestamp)
    {
        if (timestamp <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var durationSeconds = BlockDuration.TotalSeconds;
        var multiplier = Math.Floor(timestamp.TotalSeconds / durationSeconds);
        return TimeSpan.FromSeconds(multiplier * durationSeconds);
    }

    private static void FlushBlock(List<TranscriptBlock> blocks, StringBuilder blockText, TimeSpan blockStart, TimeSpan blockLastEnd, ref int nextBlockId)
    {
        if (blockText.Length == 0)
        {
            return;
        }

        var trimmed = blockText.ToString().Trim();
        blockText.Clear();
        if (trimmed.Length == 0)
        {
            return;
        }

        var safeEnd = blockLastEnd > blockStart ? blockLastEnd : blockStart + BlockDuration;
        blocks.Add(new TranscriptBlock(nextBlockId++, blockStart, safeEnd, trimmed));
    }

    private static string BuildWindowFromTimeline(IReadOnlyList<WordTiming> timeline, TimeSpan start, TimeSpan end)
    {
        var builder = new StringBuilder();
        foreach (var word in timeline)
        {
            if (word.End <= start)
            {
                continue;
            }

            if (word.Start >= end)
            {
                break;
            }

            AppendWord(builder, word.Text);
        }

        return builder.ToString();
    }

    private static bool AppendEntryLines(StringBuilder builder, IReadOnlyList<VideoIndexerTranscriptEntry> entries, TimeSpan windowStart, TimeSpan windowEnd)

    {

        var hasLines = false;

        foreach (var entry in entries)

        {

            var textValue = entry.Text?.Trim();

            if (string.IsNullOrWhiteSpace(textValue))

            {

                continue;

            }



            if (!TryGetEntryRange(entry, out var entryStart, out var entryEnd))

            {

                entryStart = windowStart;

                entryEnd = entryStart;

            }



            if (entryEnd <= windowStart || entryStart >= windowEnd)

            {

                continue;

            }



            var relativeStart = entryStart <= windowStart ? TimeSpan.Zero : entryStart - windowStart;

            builder.Append("[+");

            builder.Append(relativeStart.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));

            builder.Append("s] \"");

            builder.Append(textValue);

            builder.AppendLine("\"");

            hasLines = true;

        }



        return hasLines;

    }



    private static bool TryGetEntryRange(VideoIndexerTranscriptEntry entry, out TimeSpan start, out TimeSpan end)

    {

        TimeSpan? startCandidate = TimestampHelper.Parse(entry.StartTime);

        TimeSpan? endCandidate = TimestampHelper.Parse(entry.EndTime);



        foreach (var instance in entry.Instances)

        {

            if (!startCandidate.HasValue)

            {

                startCandidate = TimestampHelper.Parse(instance.Start);

            }



            if (!endCandidate.HasValue)

            {

                endCandidate = TimestampHelper.Parse(instance.End);

            }



            foreach (var word in instance.Words)

            {

                if (!startCandidate.HasValue)

                {

                    startCandidate = TimestampHelper.Parse(word.StartTime ?? word.Start);

                }



                if (!endCandidate.HasValue)

                {

                    endCandidate = TimestampHelper.Parse(word.EndTime ?? word.End);

                }



                if (startCandidate.HasValue && endCandidate.HasValue)

                {

                    break;

                }

            }



            if (startCandidate.HasValue && endCandidate.HasValue)

            {

                break;

            }

        }



        if (!startCandidate.HasValue && endCandidate.HasValue)

        {

            startCandidate = endCandidate;

        }



        if (!endCandidate.HasValue && startCandidate.HasValue)

        {

            endCandidate = startCandidate;

        }



        if (!startCandidate.HasValue || !endCandidate.HasValue)

        {

            start = TimeSpan.Zero;

            end = TimeSpan.Zero;

            return false;

        }



        if (endCandidate.Value < startCandidate.Value)

        {

            endCandidate = startCandidate;

        }



        start = startCandidate.Value;

        end = endCandidate.Value;

        return true;

    }



    private static string ExtractWindowText(IReadOnlyList<WordTiming> timeline, ref int index, TimeSpan start, TimeSpan end)
    {
        var builder = new StringBuilder();
        var cursor = Math.Max(0, index);

        while (cursor > 0 && timeline[cursor - 1].End > start)
        {
            cursor--;
        }

        while (cursor < timeline.Count)
        {
            var word = timeline[cursor];
            if (word.Start >= end)
            {
                break;
            }

            if (word.End > start)
            {
                AppendWord(builder, word.Text);
            }

            cursor++;
        }

        index = Math.Max(0, cursor - 1);
        return builder.ToString();
    }

    private static void AppendWord(StringBuilder builder, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (builder.Length == 0)
        {
            builder.Append(trimmed);
            return;
        }

        if (IsPunctuationToken(trimmed))
        {
            builder.Append(trimmed);
            return;
        }

        builder.Append(' ');
        builder.Append(trimmed);
    }

    private static bool IsPunctuationToken(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        foreach (var ch in token)
        {
            if (!char.IsPunctuation(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddWord(List<WordTiming> words, TimeSpan start, TimeSpan end, string? text, string? speakerId, string? speakerName, string? sentiment)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var normalized = text.Trim();
        var safeEnd = end > start ? end : start;
        words.Add(new WordTiming(start, safeEnd, normalized, speakerId, speakerName, sentiment));
    }

    private sealed record WordTiming(TimeSpan Start, TimeSpan End, string Text, string? SpeakerId, string? SpeakerName, string? Sentiment);

    private sealed record HintOccurrence(TimeSpan Start, TimeSpan End, string Text);
}
