using Microsoft.Data.Sqlite;

namespace QuizManager.Infrastructure.Data;

/// <summary>
/// Promotes legacy projects preserved by the database importer into the native V2
/// Quiz Projects list. The original legacy row remains untouched and can continue
/// to be used later for full project-feature parity.
/// </summary>
public sealed class LegacyProjectMigrationService
{
    private readonly string _databasePath;

    public LegacyProjectMigrationService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "legacy_projects", cancellationToken))
            return 0;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureMapTableAsync(connection, transaction, cancellationToken);

            var imported = 0;
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT p.id, p.title, COALESCE(p.category, ''), COALESCE(p.description, ''),
                       COALESCE(p.created, ''), COALESCE(p.status, ''), COALESCE(p.pinned, 0)
                FROM legacy_projects p
                LEFT JOIN legacy_project_map m ON m.legacy_id = p.id
                WHERE m.legacy_id IS NULL
                ORDER BY p.id;
                """;

            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            var rows = new List<(int Id, string Title, string Category, string Description, string Created, string Status, bool Pinned)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt32(0),
                    GetString(reader, 1),
                    GetString(reader, 2),
                    GetString(reader, 3),
                    GetString(reader, 4),
                    GetString(reader, 5),
                    GetInt(reader, 6) != 0));
            }

            foreach (var row in rows)
            {
                var nativeId = await FindExistingAsync(connection, transaction, row.Title, row.Category, cancellationToken);
                if (nativeId == 0)
                {
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO quiz_projects
                            (name, category, question_count, description, created_at_utc, last_generated_at_utc, is_enabled)
                        VALUES
                            ($name, $category, 10, $description, $created, NULL, $enabled);
                        SELECT last_insert_rowid();
                        """;
                    insert.Parameters.AddWithValue("$name", row.Title.Trim());
                    insert.Parameters.AddWithValue("$category", row.Category.Trim());
                    insert.Parameters.AddWithValue("$description", row.Description.Trim());
                    insert.Parameters.AddWithValue("$created", ParseCreated(row.Created));
                    insert.Parameters.AddWithValue("$enabled", IsEnabledStatus(row.Status) ? 1 : 0);
                    nativeId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
                    imported++;
                }

                await using var map = connection.CreateCommand();
                map.Transaction = transaction;
                map.CommandText = "INSERT OR REPLACE INTO legacy_project_map(legacy_id, quiz_manager_id) VALUES($legacy, $native);";
                map.Parameters.AddWithValue("$legacy", row.Id);
                map.Parameters.AddWithValue("$native", nativeId);
                await map.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return imported;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private static async Task EnsureMapTableAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS legacy_project_map (legacy_id INTEGER PRIMARY KEY, quiz_manager_id INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> FindExistingAsync(SqliteConnection connection, SqliteTransaction transaction, string title, string category, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM quiz_projects WHERE name = $name AND category = $category ORDER BY id LIMIT 1;";
        command.Parameters.AddWithValue("$name", title.Trim());
        command.Parameters.AddWithValue("$category", category.Trim());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static string GetString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";
    private static int GetInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static string ParseCreated(string value) =>
        DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime().ToString("O") : DateTime.UtcNow.ToString("O");

    private static bool IsEnabledStatus(string status) =>
        !status.Equals("Archived", StringComparison.OrdinalIgnoreCase) &&
        !status.Equals("Deleted", StringComparison.OrdinalIgnoreCase);
}
