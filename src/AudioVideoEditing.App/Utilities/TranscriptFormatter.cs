using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AudioVideoEditing.App.Models;

namespace AudioVideoEditing.App.Utilities;

internal static class TranscriptFormatter
{
    private static readonly TimeSpan BlockDuration = TimeSpan.FromSeconds(20);

    public static TranscriptDocument Flatten(VideoIndexerInsights insights)
    {
        if (insights.Videos.Count == 0)
        {
            throw new InvalidOperationException("Video Indexer insights payload does not contain any videos.");
        }

        var transcript = insights.Videos[0].Insights.Transcript;
        if (transcript.Count == 0)
        {
            throw new InvalidOperationException("Transcript entries are empty.");
        }

        var timeline = ExtractWordTimeline(transcript);
        if (timeline.Count == 0)
        {
            throw new InvalidOperationException("Transcript could not be flattened because all entries were empty.");
        }

        return BuildFixedBlocks(timeline);
    }

    private static IReadOnlyList<WordTiming> ExtractWordTimeline(IReadOnlyList<VideoIndexerTranscriptEntry> transcript)
    {
        var words = new List<WordTiming>();

        foreach (var entry in transcript)
        {
            var entryStart = ParseTimestamp(entry.StartTime) ?? TimeSpan.Zero;
            var entryEnd = ParseTimestamp(entry.EndTime) ?? entryStart;
            var entryText = entry.Text?.Trim();

            if (entry.Instances.Count == 0)
            {
                AddWord(words, entryStart, entryEnd, entryText);
                continue;
            }

            foreach (var instance in entry.Instances)
            {
                var instanceStart = ParseTimestamp(instance.Start) ?? entryStart;
                var instanceEnd = ParseTimestamp(instance.End) ?? entryEnd;

                if (instance.Words.Count == 0)
                {
                    AddWord(words, instanceStart, instanceEnd, entryText);
                    continue;
                }

                foreach (var word in instance.Words)
                {
                    var text = string.IsNullOrWhiteSpace(word.Text) ? word.Word : word.Text;
                    var wordStart = ParseTimestamp(word.StartTime ?? word.Start) ?? instanceStart;
                    var wordEnd = ParseTimestamp(word.EndTime ?? word.End) ?? instanceEnd;
                    AddWord(words, wordStart, wordEnd, text);
                }
            }
        }

        words.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return words;
    }

    private static TranscriptDocument BuildFixedBlocks(IReadOnlyList<WordTiming> timeline)
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

        var formatted = FormatBlocks(blocks);
        return new TranscriptDocument(formatted, blocks);
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

    private static string FormatBlocks(IReadOnlyList<TranscriptBlock> blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            builder.Append("[ID:");
            builder.Append(block.Id);
            builder.Append("][");
            builder.Append(FormatTimestamp(block.Start));
            builder.Append('-');
            builder.Append(FormatTimestamp(block.End));
            builder.Append("] ");
            builder.AppendLine(block.Text);
        }

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

    private static void AddWord(List<WordTiming> words, TimeSpan start, TimeSpan end, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var normalized = text.Trim();
        var safeEnd = end > start ? end : start;
        words.Add(new WordTiming(start, safeEnd, normalized));
    }

    private static TimeSpan? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static string FormatTimestamp(TimeSpan timestamp)
    {
        var totalHours = (int)Math.Floor(timestamp.TotalHours);
        return $"{totalHours:D2}:{timestamp.Minutes:D2}:{timestamp.Seconds:D2}";
    }

    private sealed record WordTiming(TimeSpan Start, TimeSpan End, string Text);
}
