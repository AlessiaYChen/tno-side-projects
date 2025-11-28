using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AudioVideoEditing.App.Utilities;

internal static class LlmOutputStore
{
    public static string BuildPath(string root, string inputFile, string suffix)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("LLM output root cannot be empty.", nameof(root));
        }

        var fileName = Path.GetFileName(inputFile);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "llm-output";
        }

        var extension = string.IsNullOrWhiteSpace(suffix) ? ".txt" : (suffix.StartsWith('.') ? suffix : $".{suffix}");
        return Path.Combine(root, fileName + extension);
    }

    public static async Task SaveAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || content is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken);
    }
}
