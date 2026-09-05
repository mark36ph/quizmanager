namespace QuizManager.Core.Models;

public sealed record PublishJob(
    int Id,
    string VideoPath,
    string Title,
    string Description,
    string ThumbnailPath,
    string Platform,
    string Status,
    string RemoteId,
    string RemoteUrl,
    string ErrorMessage,
    DateTime CreatedUtc,
    DateTime? CompletedUtc);
