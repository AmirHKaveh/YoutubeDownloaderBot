using Microsoft.Extensions.Configuration;

using System.Diagnostics;
using System.Text;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

var builder = new ConfigurationBuilder()
    .AddUserSecrets<Program>();
var configuration = builder.Build();

string apiKey = configuration["ApiKeys:MyApiKey"];

if (apiKey is null)
{
    return;
}
Console.WriteLine($"API Key: {apiKey}");

var botClient = new TelegramBotClient(apiKey);

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
    if (update.Message is not { } message)
        return;

    if (message.Text is not { } messageText)
        return;

    var chatId = message.Chat.Id;

    Console.WriteLine($"Received a '{messageText}' message in chat {chatId}.");

    // Check if message contains YouTube URL
    if (messageText.Contains("youtube.com") || messageText.Contains("youtu.be"))
    {
        try
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Processing your YouTube video...",
                cancellationToken: cancellationToken);

            var youtube = new YoutubeClient();
            var video = await youtube.Videos.GetAsync(messageText);

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
        }
        catch (Exception ex)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: $"Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }
    else
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: "Please send me a YouTube URL to download the video.",
            cancellationToken: cancellationToken);
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