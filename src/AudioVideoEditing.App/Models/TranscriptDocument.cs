using System;
using System.Collections.Generic;

namespace AudioVideoEditing.App.Models;

internal sealed record TranscriptDocument(string Text, IReadOnlyList<TranscriptBlock> Blocks);

internal sealed record TranscriptBlock(
    int Id,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? Speaker = null,
    string? Sentiment = null,
    IReadOnlyList<string>? TopicHints = null);
