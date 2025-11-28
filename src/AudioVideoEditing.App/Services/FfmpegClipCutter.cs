using System.Diagnostics;
using System.Globalization;
using AudioVideoEditing.App.Models;

namespace AudioVideoEditing.App.Services;

internal sealed class FfmpegClipCutter
{
    private readonly string _ffmpegPath;

    public FfmpegClipCutter(string ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    public async Task CutAsync(string inputPath, ClipRange clip, string outputPath, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Would cut {inputPath} from {clip.Start:c} to {clip.End:c} -> {outputPath} using {_ffmpegPath}.");
            return;
        }

        var duration = clip.End - clip.Start;
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Clip end must be after clip start.", nameof(clip));
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        var startText = FormatTimestamp(clip.Start);
        var durationText = FormatTimestamp(duration);

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(startText);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(durationText);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdErrorTask = process.StandardError.ReadToEndAsync();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);

        var stderr = await stdErrorTask;
        var stdout = await stdOutTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine(stdout.Trim());
        }
    }

    private static string FormatTimestamp(TimeSpan value)
        => value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}

