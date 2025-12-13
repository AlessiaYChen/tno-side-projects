using System.Collections.Generic;
using System.Linq;

namespace AudioVideoEditing.App.Configuration;

internal sealed class AppOptions
{
    private AppOptions()
    {
    }

    public string InputRoot { get; private set; } = string.Empty;
    public string OutputRoot { get; private set; } = string.Empty;
    public bool DryRun { get; private set; }
    public IReadOnlyList<string> FileExtensions { get; private set; } = Array.Empty<string>();
    public string TopicQuery { get; private set; } = string.Empty;
    public string OpenAiDeployment { get; private set; } = string.Empty;
    public string JobLabel { get; private set; } = "tno-media";
    public bool GenerateNewsClips { get; private set; }
    public bool UseVideoIndexerNewsClips { get; private set; }
    public bool SkipVideoIndexer { get; private set; }
    public string InsightsCacheRoot { get; private set; } = string.Empty;
    public string LlmOutputRoot { get; private set; } = string.Empty;

    public static AppOptions Parse(string[] args, AppSettings settings)
    {
        var options = new AppOptions
        {
            InputRoot = settings.Processing.InputRoot,
            OutputRoot = settings.Processing.OutputRoot,
            FileExtensions = settings.Processing.FileExtensions.Count > 0
                ? settings.Processing.FileExtensions.Select(NormalizeExtension).ToArray()
                : new[] { ".mp4" },
            OpenAiDeployment = settings.OpenAi.DeploymentName
        };

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "-i":
                case "--input":
                    options.InputRoot = RequireValue(args, ++index, token);
                    break;
                case "-o":
                case "--output":
                    options.OutputRoot = RequireValue(args, ++index, token);
                    break;
                case "--extensions":
                    options.FileExtensions = SplitList(RequireValue(args, ++index, token))
                        .Select(NormalizeExtension)
                        .ToArray();
                    break;
                case "--topic":
                    options.TopicQuery = RequireValue(args, ++index, token);
                    break;
                case "--openai-deployment":
                    options.OpenAiDeployment = RequireValue(args, ++index, token);
                    break;
                case "--label":
                    options.JobLabel = RequireValue(args, ++index, token);
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--news-clips":
                    options.GenerateNewsClips = true;
                    break;
                case "--news-clips-from-vi":
                    options.GenerateNewsClips = true;
                    options.UseVideoIndexerNewsClips = true;
                    break;
                case "--skip-video-indexer":
                    options.SkipVideoIndexer = true;
                    break;
                case "--insights-cache":
                    options.InsightsCacheRoot = RequireValue(args, ++index, token);
                    break;
                case "--llm-output":
                    options.LlmOutputRoot = RequireValue(args, ++index, token);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{token}'.");
            }
        }

        if (!options.GenerateNewsClips && string.IsNullOrWhiteSpace(options.TopicQuery))
        {
            throw new ArgumentException("--topic is required unless --news-clips is specified.");
        }

        options.InputRoot = EnsureDirectory(options.InputRoot, nameof(options.InputRoot));
        options.OutputRoot = EnsureDirectory(options.OutputRoot, nameof(options.OutputRoot), createIfMissing: true);

        if (options.FileExtensions.Count == 0)
        {
            options.FileExtensions = new[] { ".mp4" };
        }

        if (string.IsNullOrWhiteSpace(options.InsightsCacheRoot))
        {
            options.InsightsCacheRoot = Path.Combine(options.OutputRoot, "insights");
        }

        options.InsightsCacheRoot = EnsureDirectory(options.InsightsCacheRoot, nameof(options.InsightsCacheRoot), createIfMissing: true);

        if (string.IsNullOrWhiteSpace(options.LlmOutputRoot))
        {
            options.LlmOutputRoot = Path.Combine(options.OutputRoot, "llm");
        }

        options.LlmOutputRoot = EnsureDirectory(options.LlmOutputRoot, nameof(options.LlmOutputRoot), createIfMissing: true);

        return options;
    }

    private static string RequireValue(string[] args, int index, string token)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {token}.");
        }

        return args[index];
    }

    private static string EnsureDirectory(string path, string name, bool createIfMissing = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{name} cannot be empty.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            if (createIfMissing)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                throw new DirectoryNotFoundException($"Unable to locate {name} folder: {fullPath}");
            }
        }

        return fullPath;
    }

    private static IEnumerable<string> SplitList(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeExtension(string ext)
    {
        var trimmed = ext.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : $".{trimmed.ToLowerInvariant()}";
    }
}


