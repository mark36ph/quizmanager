using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuizManager.Core.Models;

namespace QuizManager.Infrastructure.Data;

public sealed record YouTubePublishResult(string VideoId, string Url);

public sealed class YouTubePublisher
{
    private const string VideosEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos";
    private const string ThumbnailEndpoint = "https://www.googleapis.com/upload/youtube/v3/thumbnails/set";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromHours(4) };

    public async Task<YouTubePublishResult> UploadAsync(
        string accessToken,
        PublishJob job,
        string privacyStatus = "private",
        DateTimeOffset? publishAt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("A YouTube access token is required.");
        if (!File.Exists(job.VideoPath)) throw new FileNotFoundException("The publishing video no longer exists.", job.VideoPath);
        if (string.IsNullOrWhiteSpace(job.Title)) throw new ArgumentException("A YouTube title is required.");
        if (job.Title.Length > 100) throw new ArgumentException("The YouTube title cannot exceed 100 characters.");
        if (privacyStatus is not ("private" or "unlisted" or "public")) throw new ArgumentException("YouTube privacy must be private, unlisted, or public.");
        if (publishAt is not null && privacyStatus != "private") throw new ArgumentException("Scheduled YouTube publication must remain private until the scheduled time.");

        var file = new FileInfo(job.VideoPath);
        var status = new Dictionary<string, object?> { ["privacyStatus"] = privacyStatus, ["selfDeclaredMadeForKids"] = false };
        if (publishAt is { } scheduled) status["publishAt"] = scheduled.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var metadata = JsonSerializer.Serialize(new
        {
            snippet = new { title = job.Title.Trim(), description = job.Description?.Trim() ?? string.Empty, categoryId = "27" },
            status
        });

        var initiateUrl = VideosEndpoint + "?uploadType=resumable&part=snippet%2Cstatus";
        using var initiate = new HttpRequestMessage(HttpMethod.Post, initiateUrl) { Content = new StringContent(metadata, Encoding.UTF8, "application/json") };
        initiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Length", file.Length.ToString(CultureInfo.InvariantCulture));
        initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Type", VideoMimeType(job.VideoPath));
        using var initResponse = await Client.SendAsync(initiate, cancellationToken);
        if (!initResponse.IsSuccessStatusCode) throw await ReadErrorAsync(initResponse, cancellationToken);
        if (initResponse.Headers.Location is not Uri uploadUri || uploadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("YouTube did not return a valid resumable upload address.");

        await using var stream = file.OpenRead();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(VideoMimeType(job.VideoPath));
        content.Headers.ContentLength = file.Length;
        using var upload = new HttpRequestMessage(HttpMethod.Put, uploadUri) { Content = content };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        using var response = await Client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw ParseError(response.StatusCode, responseText);
        using var document = JsonDocument.Parse(responseText);
        var videoId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(videoId)) throw new InvalidOperationException("YouTube completed the upload without returning a video ID.");
        return new YouTubePublishResult(videoId, $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}");
    }

    public async Task SetThumbnailAsync(string accessToken, string videoId, string thumbnailPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("A YouTube access token is required.");
        if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("A YouTube video ID is required.");
        if (!File.Exists(thumbnailPath)) throw new FileNotFoundException("The thumbnail no longer exists.", thumbnailPath);
        var extension = Path.GetExtension(thumbnailPath).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png") throw new ArgumentException("YouTube thumbnails must be JPG or PNG files.");
        if (new FileInfo(thumbnailPath).Length > 2L * 1024 * 1024) throw new ArgumentException("The YouTube thumbnail must be 2 MB or smaller.");

        await using var stream = File.OpenRead(thumbnailPath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(extension == ".png" ? "image/png" : "image/jpeg");
        content.Headers.ContentLength = stream.Length;
        using var request = new HttpRequestMessage(HttpMethod.Post, ThumbnailEndpoint + "?videoId=" + Uri.EscapeDataString(videoId) + "&uploadType=media") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        using var response = await Client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ReadErrorAsync(response, cancellationToken);
    }

    private static string VideoMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime",
        ".m4v" => "video/x-m4v",
        _ => "video/mp4"
    };

    private static async Task<InvalidOperationException> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ParseError(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));

    private static InvalidOperationException ParseError(System.Net.HttpStatusCode statusCode, string text)
    {
        var message = "YouTube request failed";
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var detail))
                message = detail.GetString()?.Trim() ?? message;
        }
        catch (JsonException) { }
        return new InvalidOperationException($"{message} (HTTP {(int)statusCode}).");
    }
}
