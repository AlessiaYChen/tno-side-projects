# Audio/Video Editing POC

This proof of concept now focuses entirely on an automated **Analyze -> Decide -> Act** loop built with Azure Video Indexer (VI), Azure OpenAI, and FFmpeg. The flow:

1. **Analyze** – Upload each raw newscast to Video Indexer and wait for transcripts.
2. **Decide** – Ask GPT-4/GPT-4o (via Azure OpenAI) to identify timestamps for a desired topic.
3. **Act** – Run FFmpeg on the local raw file to trim only the requested story.

There is no dependency on Microsoft Foundry / Azure Media Services anymore.

## Repository Layout
```
audio-video-editing/
+- audio-video-editing.sln
+- README.md (this file)
+- src/
   +- AudioVideoEditing.App/
      +- appsettings.json
      +- Configuration/
      +- Models/
      +- Pipeline/
      +- Services/
      +- Utilities/
      +- Program.cs
```

## Prerequisites
1. .NET 9 SDK (`dotnet --version` should report 9.x).
2. Azure resources and keys:
   - **Video Indexer** account ID, location (e.g., `canadacentral`), and an access token or ARM token exchange.
   - **Azure OpenAI** resource (endpoint + deployment/model + API key).
3. FFmpeg available on your `PATH` (or provide a custom path in config).
4. Environment variables (set securely; do not commit secrets):
   - `VIDEO_INDEXER_ACCOUNT_ID`, `VIDEO_INDEXER_LOCATION`
   - `VIDEO_INDEXER_ACCESS_TOKEN` *(optional if `VideoIndexer.AccessToken` is set in appsettings)*
   - `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT`
   - `AZURE_OPENAI_KEY` *(optional if `OpenAi.ApiKey` is set in appsettings)*
   - Optional overrides: `AZURE_OPENAI_API_VERSION`, `AUDIO_VIDEO_EDITING_INPUT_ROOT`, `AUDIO_VIDEO_EDITING_OUTPUT_ROOT`, `AUDIO_VIDEO_EDITING_EXTENSIONS`, `AUDIO_VIDEO_EDITING_FFMPEG`

## Configuration
`src/AudioVideoEditing.App/appsettings.json` provides defaults that can be overridden via env vars. Set `VideoIndexer.AccessToken` and/or `OpenAi.ApiKey` to bake in static secrets (the tool skips the matching environment variables when these values are present):
```jsonc
{
  "Processing": {
    "InputRoot": "C:/data/media/raw",
    "OutputRoot": "C:/data/media/edited",
    "ParallelJobs": 2,
    "FileExtensions": [".mp4", ".mp3", ".wav"],
    "SkipAdsByDefault": true,
    "FfmpegPath": "ffmpeg"
  },
  "VideoIndexer": {
    "AccountId": "<azure-video-indexer-account-id>",
    "Location": "canadacentral",
    "BaseUrl": "https://api.videoindexer.ai",
    "ProjectName": "tno-media",
    "AccessToken": "<video-indexer-access-token>",
    "PollingIntervalSeconds": 10
  },
  "OpenAi": {
    "Endpoint": "https://your-openai-resource.openai.azure.com/",
    "DeploymentName": "gpt-4o-mini",
    "ApiVersion": "2024-02-15-preview",
    "ApiKey": "<azure-openai-api-key>",
    "Temperature": 0.1
  }
}
```

## Running the Automated Clip Pipeline
From the repo root:
```powershell
cd audio-video-editing
dotnet run --project src/AudioVideoEditing.App -- `
  --input C:/data/media/raw `
  --output C:/data/media/edited `
  --topic "segment about the wild fires near Kelowna" `
  --openai-deployment gpt-4o-mini
```
Flags:
- `--topic` *(required unless `--news-clips` is set)* - description of the story to locate in the transcript.
- `--openai-deployment` - override for the Azure OpenAI deployment/model name (defaults to config/env vars).
- `--extensions .mp4,.wmv` - filter which files are scanned.
- `--dry-run` - log upload + cut plans without calling Video Indexer or FFmpeg.
- `--label <slug>` - changes the slug used for Video Indexer upload names and output file suffixes.
- `--news-clips` - automatically split the transcript into sequential news stories and cut each one locally.

Every clip currently includes an automatic +2 second tail padding before FFmpeg runs so that anchors don't get cut off mid-sentence.

Per file the program will:
1. Upload the raw clip to VI via `POST https://api.videoindexer.ai/{location}/Accounts/{accountId}/Videos` and poll `.../Videos/{videoId}/Index` until `state == "Processed"`.
2. Flatten `videos[0].insights.transcript` into `[start-end] text` lines and send it to Azure OpenAI with a JSON-only prompt.
3. Parse the `{ "start": "00:01:23", "end": "00:03:45" }` response and cut the original file locally with FFmpeg `-ss`/`-to`/`-c copy`.

If FFmpeg is unavailable the run will fail fast; set `AUDIO_VIDEO_EDITING_FFMPEG` to point at a custom binary if needed.

## Prompt Strategy Tips
- Keep the transcript manageable by pre-filtering (future improvement: search keywords locally before sending to GPT).
- The system prompt already forces JSON output; tweak `OpenAi.Temperature` for more deterministic or creative responses.
- Add guardrails such as "return `{\"start\": null, \"end\": null}` if unsure" by editing `OpenAiStoryLocator`.

## Extensibility Ideas
- Use the same VI/GPT timestamps to instruct FFmpeg to remove commercials or sequence multiple clips.
- Replace FFmpeg with another NLE CLI (GStreamer, Adobe Media Encoder) in `FfmpegClipCutter`.
- Persist outputs + provenance into Cosmos DB/PostgreSQL for auditing or analytics.
- Integrate VI project rendering APIs if you ever need Azure to stitch intros/outros.

## Troubleshooting
- **Access token expired:** refresh Video Indexer tokens regularly or add a helper endpoint that exchanges ARM tokens automatically.
- **OpenAI throttling:** consider exponential back-off or chunking transcripts into smaller sections.
- **FFmpeg errors:** confirm the binary path and ensure the input containers support `-c copy` (use transcode fallback when necessary).
