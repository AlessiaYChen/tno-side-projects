using AudioVideoEditing.App.Configuration;
using AudioVideoEditing.App.Pipeline;
using AudioVideoEditing.App.Services;

try
{
    var settings = AppSettings.Load(AppContext.BaseDirectory);
    var options = AppOptions.Parse(args, settings);

    var viToken = settings.ResolveVideoIndexerAccessToken();
    using var viHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
    var videoIndexer = new VideoIndexerClient(viHttpClient, settings.VideoIndexer, viToken);

    var openAiKey = settings.ResolveOpenAiApiKey();
    using var openAiHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
    var storyLocator = new OpenAiStoryLocator(openAiHttpClient, settings.OpenAi, openAiKey);

    var cutter = new FfmpegClipCutter(settings.Processing.FfmpegPath);
    var pipeline = new AutomatedClipPipeline(videoIndexer, storyLocator, cutter);

    await pipeline.RunAsync(options, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    Environment.ExitCode = 1;
}
