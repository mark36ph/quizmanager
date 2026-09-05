using Microsoft.Data.Sqlite;
using QuizManager.Core.Models;

namespace QuizManager.Infrastructure.Data;

public sealed class PublishingService
{
    private readonly QuizDatabase _database;

    public PublishingService(QuizDatabase database) => _database = database;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS publish_jobs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                video_path TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                thumbnail_path TEXT NOT NULL DEFAULT '',
                platform TEXT NOT NULL,
                status TEXT NOT NULL,
                remote_id TEXT NOT NULL DEFAULT '',
                remote_url TEXT NOT NULL DEFAULT '',
                error_message TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                completed_utc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_publish_jobs_created ON publish_jobs(created_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> QueueAsync(string videoPath, string title, string description, string thumbnailPath, string platform = "YouTube", CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("The rendered video could not be found.", videoPath);
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A title is required.", nameof(title));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO publish_jobs(video_path,title,description,thumbnail_path,platform,status,created_utc)
            VALUES($video,$title,$description,$thumbnail,$platform,'Queued',$created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$video", videoPath.Trim());
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$description", description?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$thumbnail", thumbnailPath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$platform", platform.Trim());
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<PublishJob>> GetJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,video_path,title,description,thumbnail_path,platform,status,remote_id,remote_url,error_message,created_utc,completed_utc FROM publish_jobs ORDER BY id DESC;";
        var result = new List<PublishJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PublishJob(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    public async Task MarkPublishedAsync(int id, string remoteId = "", string remoteUrl = "", CancellationToken cancellationToken = default)
        => await SetStatusAsync(id, "Published", string.Empty, remoteId, remoteUrl, true, cancellationToken);

    public async Task MarkFailedAsync(int id, string errorMessage, CancellationToken cancellationToken = default)
        => await SetStatusAsync(id, "Failed", errorMessage, "", "", true, cancellationToken);

    public async Task MarkQueuedAsync(int id, CancellationToken cancellationToken = default)
        => await SetStatusAsync(id, "Queued", string.Empty, "", "", false, cancellationToken);

    private async Task SetStatusAsync(int id, string status, string error, string remoteId, string remoteUrl, bool completed, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE publish_jobs SET status=$status,error_message=$error,remote_id=$remoteId,remote_url=$remoteUrl,completed_utc=$completed WHERE id=$id;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$remoteId", remoteId);
        command.Parameters.AddWithValue("$remoteUrl", remoteUrl);
        command.Parameters.AddWithValue("$completed", completed ? DateTime.UtcNow.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_database.DatabasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
