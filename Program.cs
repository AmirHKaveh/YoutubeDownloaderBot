using HtmlAgilityPack;

using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

var botClient = new TelegramBotClient("8054511567:AAGo05bzhI1iGfyLbRt8VncK6S5ToSrK96k");
HttpClient _httpClient = new HttpClient();

using CancellationTokenSource cts = new();

// Start receiving updates
ReceiverOptions receiverOptions = new()
{
    AllowedUpdates = Array.Empty<UpdateType>()
};

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandlePollingErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

var me = await botClient.GetMe();
Console.WriteLine($"Bot {me.Username} is running!");
Console.ReadLine();

// Send cancellation request to stop bot
cts.Cancel();

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{

    if (update.Type == UpdateType.Message)
    {
        if (update.Message is not { } message)
            return;

        if (message.Text is not { } messageText)
            return;

        var chatId = message.Chat.Id;

        if (message.Text == "/start")
        {
            await SetButtons(botClient, message);
        }
        else if (messageText.Contains("youtube.com") || messageText.Contains("youtu.be"))
        {
            await YoutubeDownloader(message, chatId, cancellationToken);
        }
        else if (messageText.Contains("instagram.com/reel/") || messageText.Contains("instagram.com/p/"))
        {
            await InstagramDownloader(message, chatId, cancellationToken);
        }
    }
    else if (update.Type == UpdateType.CallbackQuery)
    {
        if (update.CallbackQuery is not { } callbackQuery)
            return;

        await HandleCallbackQuery(botClient, callbackQuery, cancellationToken);
    }


}
Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    var ErrorMessage = exception switch
    {
        ApiRequestException apiRequestException
            => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
        _ => exception.ToString()
    };

    Console.WriteLine(ErrorMessage);
    return Task.CompletedTask;
}

static async Task<bool> CheckFfmpegExists()
{
    try
    {
        var process = new Process
        {
            StartInfo = {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

async Task DownloadAndCombineSeparateStreams(
    ITelegramBotClient botClient,
    long chatId,
    YoutubeClient youtube,
    YoutubeExplode.Videos.Video video,
    IVideoStreamInfo videoStreamInfo,
    IStreamInfo audioStreamInfo,
    CancellationToken cancellationToken)
{
    if (!await CheckFfmpegExists())
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: "FFmpeg is not installed or not in PATH. Cannot combine streams.",
            cancellationToken: cancellationToken);
        return;
    }

    try
    {
        // Create unique temp directory for this download
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var videoTempPath = Path.Combine(tempDir, "video.mp4");
        var audioTempPath = Path.Combine(tempDir, "audio.mp4");
        var outputPath = Path.Combine(tempDir, "output.mp4");



        // Download video stream with progress reporting
        await DownloadWithProgress(botClient, chatId, youtube, videoStreamInfo, videoTempPath,
            "Downloading video stream...", cancellationToken);

        // Download audio stream with progress reporting
        await DownloadWithProgress(botClient, chatId, youtube, audioStreamInfo, audioTempPath,
            "Downloading audio stream...", cancellationToken);

        if (!File.Exists(videoTempPath))
        {
            Console.WriteLine($"Error: Video temp file not found at {videoTempPath}");
            await botClient.SendMessage(chatId, "Internal error: Video file not found after download.", cancellationToken: cancellationToken);
            return;
        }
        if (!File.Exists(audioTempPath))
        {
            Console.WriteLine($"Error: Audio temp file not found at {audioTempPath}");
            await botClient.SendMessage(chatId, "Internal error: Audio file not found after download.", cancellationToken: cancellationToken);
            return;
        }


        // Combine streams
        await botClient.SendMessage(
            chatId: chatId,
            text: "Combining audio and video streams...",
            cancellationToken: cancellationToken);



        var ffmpegArgs = $"-y -i \"{videoTempPath}\" -i \"{audioTempPath}\" -c:v copy -c:a aac -map 0:v:0 -map 1:a:0 -shortest \"{outputPath}\"";

        //var ffmpegArgs = $"-y -i \"{videoTempPath}\" -c copy \"{outputPath}\"";


        var processStartTime = DateTime.Now;
        var ffmpegProcess = new Process
        {
            StartInfo = {
                FileName = "ffmpeg",
                Arguments = ffmpegArgs,
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        var errorOutput = new StringBuilder();
        ffmpegProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                errorOutput.AppendLine(e.Data);
                // Optional: Parse progress from ffmpeg output and send updates
            }
        };

        ffmpegProcess.Start();
        Task<string> outputTask = ffmpegProcess.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = ffmpegProcess.StandardError.ReadToEndAsync();

        // Wait for the process to exit.
        ffmpegProcess.WaitForExit();


        if (ffmpegProcess.ExitCode != 0)
        {
            throw new Exception($"FFmpeg error (exit code {ffmpegProcess.ExitCode}): {errorOutput.ToString()}");
        }

        // Check if output file was created
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new Exception("FFmpeg failed to create output file");
        }

        // Send combined video
        await using (var fileStream = File.OpenRead(outputPath))
        {
            await botClient.SendVideo(
                chatId: chatId,
                video: InputFile.FromStream(fileStream, $"{video.Title}.mp4"),
                caption: video.Title,
                duration: (int)(video.Duration?.TotalSeconds ?? 0),
                cancellationToken: cancellationToken);
        }
    }
    catch (Exception ex)
    {
        string errorMessage = $"Error combining streams: {ex.Message}";
        if (!string.IsNullOrEmpty(errorMessage.ToString())) // Check if errorOutput has content
        {
            errorMessage += $"\nFFmpeg Output:\n{errorMessage.ToString()}";
        }
        await botClient.SendMessage(
            chatId: chatId,
            text: errorMessage,
            cancellationToken: cancellationToken);
    }
    finally
    {
    }
}


async Task DownloadAndSendStream(
    ITelegramBotClient botClient,
    long chatId,
    YoutubeClient youtube,
    YoutubeExplode.Videos.Video video,
    IStreamInfo streamInfo,
    CancellationToken cancellationToken)
{
    try
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: $"Downloading: {video.Title}...",
            cancellationToken: cancellationToken);

        var stream = await youtube.Videos.Streams.GetAsync(streamInfo);
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"{video.Id}.mp4");

        await using (var fileStream = File.Create(tempFilePath))
        {
            await stream.CopyToAsync(fileStream);
        }

        await using (var fileStream = File.OpenRead(tempFilePath))
        {
            await botClient.SendVideo(
                chatId: chatId,
                video: InputFile.FromStream(fileStream, $"{video.Title}.mp4"),
                caption: video.Title,
                cancellationToken: cancellationToken);
        }

        File.Delete(tempFilePath);
    }
    catch (Exception ex)
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: $"Error: {ex.Message}",
            cancellationToken: cancellationToken);
    }
}

async Task DownloadWithProgress(
    ITelegramBotClient botClient,
    long chatId,
    YoutubeClient youtube,
    IStreamInfo streamInfo,
    string outputPath,
    string progressMessage,
    CancellationToken cancellationToken)
{
    var lastUpdate = DateTime.MinValue;
    var progress = new Progress<double>(p =>
    {
        if ((DateTime.Now - lastUpdate).TotalSeconds >= 5) // Update every 5 seconds
        {
            lastUpdate = DateTime.Now;
            botClient.SendMessage(
                chatId: chatId,
                text: $"{progressMessage} {p:P0}",
                cancellationToken: cancellationToken).Wait();
        }
    });

    var stream = await youtube.Videos.Streams.GetAsync(streamInfo);
    await using (var fileStream = File.Create(outputPath))
    {
        await stream.CopyToAsync(fileStream);
    }
}

async Task SetButtons(ITelegramBotClient botClient, Message message)
{

    var inlineKeyboard = new InlineKeyboardMarkup(new[]
    {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Youtube", "youtube_btn"),
                InlineKeyboardButton.WithCallbackData("Instagram", "instagram_btn")
            }
        });

    await botClient.SendMessage(
        chatId: message.Chat.Id,
        text: "Please choose an option:",
        replyMarkup: inlineKeyboard);

    // Handle other message types (text, photo, etc.)
}

async Task HandleCallbackQuery(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
{
    if (callbackQuery.Data == "youtube_btn")
    {
        await YoutubeCallback(callbackQuery.Message.Chat.Id, cancellationToken);
    }
    else if (callbackQuery.Data == "instagram_btn")
    {
        await InstagramCallback(callbackQuery.Message.Chat.Id, cancellationToken);
    }

    // Always answer callback queries to remove the loading state
    await botClient.AnswerCallbackQuery(
        callbackQueryId: callbackQuery.Id);
}

async Task YoutubeCallback(long chatId, CancellationToken cancellationToken)
{
    await botClient.SendMessage(
        chatId: chatId,
        text: "Please send me a YouTube URL to download the video.",
        cancellationToken: cancellationToken);
}

async Task InstagramCallback(long chatId, CancellationToken cancellationToken)
{
    await botClient.SendMessage(
        chatId: chatId,
        text: "I received an Instagram URL! Attempting to process...",
        cancellationToken: cancellationToken
    );
}

async Task YoutubeDownloader(Message message, long chatId, CancellationToken cancellationToken)
{
    try
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: "Processing your YouTube video...",
            cancellationToken: cancellationToken);

        var youtube = new YoutubeClient();
        var video = await youtube.Videos.GetAsync(message.Text);

        // Get available streams
        var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);

        var muxedStream = streamManifest.GetMuxedStreams().TryGetWithHighestVideoQuality();

        if (muxedStream != null)
        {
            // Handle muxed stream (simpler case)
            await DownloadAndSendStream(botClient, chatId, youtube, video, muxedStream, cancellationToken);
        }
        else
        {
            // Handle separate video and audio streams
            var videoStream = streamManifest.GetVideoOnlyStreams().TryGetWithHighestVideoQuality();
            var audioStream = streamManifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate)
            .FirstOrDefault();

            if (videoStream != null && audioStream != null)
            {
                await DownloadAndCombineSeparateStreams(
                    botClient,
                    chatId,
                    youtube,
                    video,
                    videoStream,
                audioStream,
                    cancellationToken);
            }
            else
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "No suitable video stream found.",
                    cancellationToken: cancellationToken);
            }
        }

        await SetButtons(botClient, message);
    }
    catch (Exception ex)
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: $"Error: {ex.Message}",
            cancellationToken: cancellationToken);

        await SetButtons(botClient, message);

    }
}

async Task InstagramDownloader(Message message, long chatId, CancellationToken cancellationToken)
{
    try
    {
        await botClient.SendMessage(
                    chatId: chatId,
                    text: "Processing Instagram link...",
                    cancellationToken: cancellationToken);

        var videoUrl = await GetInstagramVideoUrl(message.Text);

        if (videoUrl != null)
        {
            // Download the video
            var videoBytes = await _httpClient.GetByteArrayAsync(videoUrl);
            var fileName = $"instagram_video_{DateTime.Now:yyyyMMddHHmmss}.mp4";

            // Send the video back to the user
            await using (var stream = new MemoryStream(videoBytes))
            {
                await botClient.SendVideo(
                    chatId: chatId,
                    video: InputFile.FromStream(stream, fileName),
                    caption: "Here's your Instagram video",
                    cancellationToken: cancellationToken);
            }
        }
        else
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Failed to download the Instagram video.",
                cancellationToken: cancellationToken);
        }

        await SetButtons(botClient, message);
    }
    catch (Exception ex)
    {
        await botClient.SendMessage(
                   chatId: chatId,
                   text: $"Error: {ex.Message}",
                   cancellationToken: cancellationToken);

        await SetButtons(botClient, message);
    }
}



async Task<string> GetInstagramVideoUrl(string instagramUrl)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp", // or "python" and then add arguments for yt-dlp.py
            Arguments = $"--dump-json \"{instagramUrl}\"", // Dumps metadata as JSON
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(psi))
        {
            if (process == null)
            {
                Console.WriteLine("Error: yt-dlp process could not be started.");
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                // yt-dlp dumps a lot of info, we need to parse the JSON output
                // Look for the 'url' or 'webpage_url' in the JSON, or better, 'url' from 'formats'
                // This parsing might need to be more robust depending on the exact JSON structure.

                using (JsonDocument doc = JsonDocument.Parse(output))
                {
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("url", out JsonElement videoUrlElement) && videoUrlElement.ValueKind == JsonValueKind.String)
                    {
                        // This might be the direct URL for a single video post
                        return videoUrlElement.GetString();
                    }
                    else if (root.TryGetProperty("entries", out JsonElement entriesElement) && entriesElement.ValueKind == JsonValueKind.Array)
                    {
                        // For carousels, it might be in 'entries'
                        if (entriesElement.EnumerateArray().FirstOrDefault().TryGetProperty("url", out videoUrlElement) && videoUrlElement.ValueKind == JsonValueKind.String)
                        {
                            return videoUrlElement.GetString();
                        }
                    }
                    // Fallback for cases where 'url' isn't directly at the root or first entry,
                    // and we need to find the best format.
                    if (root.TryGetProperty("formats", out JsonElement formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
                    {
                        // Find the best quality video format
                        foreach (JsonElement format in formatsElement.EnumerateArray())
                        {
                            if (format.TryGetProperty("url", out JsonElement formatUrlElement) && formatUrlElement.ValueKind == JsonValueKind.String &&
                                format.TryGetProperty("vcodec", out JsonElement vcodecElement) && vcodecElement.ValueKind == JsonValueKind.String && vcodecElement.GetString() != "none")
                            {
                                // Prioritize video formats, you might want to add more logic for best quality
                                return formatUrlElement.GetString();
                            }
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"yt-dlp Error (Exit Code {process.ExitCode}): {error}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error running yt-dlp: {ex.Message}");
    }
    return null;
}


