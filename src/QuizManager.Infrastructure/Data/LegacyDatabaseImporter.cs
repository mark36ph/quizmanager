using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuizManager.Infrastructure.Data;

public sealed record LegacyImportResult(
    int ImportedQuestions,
    int SkippedDuplicateQuestions,
    int ImportedCategories,
    int ImportedProjects,
    int ImportedHistory,
    int ImportedNotes,
    int LegacyTablesPreserved,
    string BackupPath,
    string SourceDatabasePath);

/// <summary>
/// Imports a legacy FactVault Manager database without modifying the source file.
/// The complete legacy schema is retained under legacy_* tables so no legacy data is silently discarded.
/// Questions are additionally copied into the native V2 question library.
/// </summary>
public sealed class LegacyDatabaseImporter
{
    private readonly string _destinationPath;
    private readonly string _dataDirectory;

    public LegacyDatabaseImporter(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        _destinationPath = Path.GetFullPath(destinationPath);
        _dataDirectory = Path.GetDirectoryName(_destinationPath) ?? AppContext.BaseDirectory;
    }

    public async Task<LegacyImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected database could not be found.", sourcePath);

        sourcePath = Path.GetFullPath(sourcePath);
        if (string.Equals(sourcePath, _destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is already the current Quiz Manager database.");

        await ValidateLegacyDatabaseAsync(sourcePath, cancellationToken);
        var backupPath = await CreateBackupAsync(cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_destinationPath}");
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var attached = false;
        try
        {
            await EnsureMigrationTablesAsync(connection, transaction, cancellationToken);
            await AttachSourceAsync(connection, transaction, sourcePath, cancellationToken);
            attached = true;

            var tables = await GetSourceTablesAsync(connection, transaction, cancellationToken);
            await PreserveLegacyTablesAsync(connection, transaction, tables, cancellationToken);

            var questionImport = await ImportQuestionsAsync(connection, transaction, sourcePath, cancellationToken);
            var categories = await CountSourceRowsAsync(connection, transaction, "categories", cancellationToken);
            var projects = await CountSourceRowsAsync(connection, transaction, "projects", cancellationToken);
            var history = await CountSourceRowsAsync(connection, transaction, "quiz_history", cancellationToken);
            var notes = await CountSourceRowsAsync(connection, transaction, "fact_notes", cancellationToken);

            await RecordImportAsync(connection, transaction, sourcePath, questionImport, categories, projects, history, notes, tables.Count, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // SQLite cannot reliably DETACH an attached database while the transaction
            // that created/copied from it is still active. Commit first, then detach.
            await DetachSourceAsync(connection, sourcePath, cancellationToken);
            attached = false;

            return new LegacyImportResult(
                questionImport.Imported,
                questionImport.Skipped,
                categories,
                projects,
                history,
                notes,
                tables.Count,
                backupPath,
                sourcePath);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch { }

            if (attached)
            {
                try
                {
                    await DetachSourceAsync(connection, sourcePath, CancellationToken.None);
                }
                catch { }
            }

            throw;
        }
    }

    private static async Task AttachSourceAsync(SqliteConnection connection, SqliteTransaction transaction, string sourcePath, CancellationToken cancellationToken)
    {
        await using var attach = connection.CreateCommand();
        attach.Transaction = transaction;
        attach.CommandText = "ATTACH DATABASE $source AS legacy_source;";
        attach.Parameters.AddWithValue("$source", sourcePath);
        await attach.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DetachSourceAsync(SqliteConnection connection, string sourcePath, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        await using var detach = connection.CreateCommand();
        detach.CommandText = "DETACH DATABASE legacy_source;";
        await detach.ExecuteNonQueryAsync(cancellationToken);
        SqliteConnection.ClearAllPools();
    }

    private async Task<string> CreateBackupAsync(CancellationToken cancellationToken)
    {
        var backupDirectory = Path.Combine(_dataDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"quizmanager-before-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");

        if (!File.Exists(_destinationPath))
            return backupPath;

        SqliteConnection.ClearAllPools();
        await using var source = new FileStream(_destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using var destination = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await source.CopyToAsync(destination, cancellationToken);
        return backupPath;
    }

    private static async Task ValidateLegacyDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='quiz_questions';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
            throw new InvalidDataException("This is not a compatible FactVault Manager database. The quiz_questions table was not found.");
    }

    private static async Task<List<string>> GetSourceTablesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM legacy_source.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        await using var tableReader = await command.ExecuteReaderAsync(cancellationToken);
        while (await tableReader.ReadAsync(cancellationToken))
            tables.Add(tableReader.GetString(0));
        return tables;
    }

    private static async Task PreserveLegacyTablesAsync(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> tables, CancellationToken cancellationToken)
    {
        foreach (var table in tables)
        {
            var legacyName = "legacy_" + table;
            await using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(legacyName)}; CREATE TABLE {QuoteIdentifier(legacyName)} AS SELECT * FROM legacy_source.{QuoteIdentifier(table)};";
            await copy.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<(int Imported, int Skipped)> ImportQuestionsAsync(SqliteConnection connection, SqliteTransaction transaction, string sourcePath, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT question, answers_json, correct_answer_index, category FROM questions;";
            await using var existingReader = await current.ExecuteReaderAsync(cancellationToken);
            while (await existingReader.ReadAsync(cancellationToken))
            {
                var answers = JsonSerializer.Deserialize<string[]>(existingReader.GetString(1)) ?? [];
                if (answers.Length == 4)
                    existing.Add(Fingerprint(existingReader.GetString(0), answers, existingReader.GetInt32(2), existingReader.GetString(3)));
            }
        }

        var assetsDirectory = Path.Combine(_dataDirectory, "imported-assets");
        Directory.CreateDirectory(assetsDirectory);
        var imported = 0;
        var skipped = 0;

        await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
        await source.OpenAsync(cancellationToken);
        await using var command = source.CreateCommand();
        command.CommandText = "SELECT id, question, option_a, option_b, option_c, option_d, correct_index, explanation, category, difficulty, source, times_used, enabled, image_path FROM quiz_questions ORDER BY id;";
        await using var questionReader = await command.ExecuteReaderAsync(cancellationToken);

        while (await questionReader.ReadAsync(cancellationToken))
        {
            var oldId = GetInt(questionReader, 0);
            var question = GetString(questionReader, 1);
            var answers = new[] { GetString(questionReader, 2), GetString(questionReader, 3), GetString(questionReader, 4), GetString(questionReader, 5) };
            var correct = Math.Clamp(GetInt(questionReader, 6), 0, 3);
            var category = GetString(questionReader, 8);
            var key = Fingerprint(question, answers, correct, category);

            if (existing.Contains(key))
            {
                skipped++;
                continue;
            }

            var imagePath = CopyAssetIfAvailable(GetString(questionReader, 13), sourcePath, assetsDirectory);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO questions(question, answers_json, correct_answer_index, category, explanation, difficulty, source, times_used, is_enabled, image_path) VALUES($question, $answers, $correct, $category, $explanation, $difficulty, $source, $timesUsed, $enabled, $imagePath); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$question", question);
            insert.Parameters.AddWithValue("$answers", JsonSerializer.Serialize(answers));
            insert.Parameters.AddWithValue("$correct", correct);
            insert.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(category) ? "General" : category);
            insert.Parameters.AddWithValue("$explanation", GetString(questionReader, 7));
            insert.Parameters.AddWithValue("$difficulty", string.IsNullOrWhiteSpace(GetString(questionReader, 9)) ? "medium" : GetString(questionReader, 9));
            insert.Parameters.AddWithValue("$source", GetString(questionReader, 10));
            insert.Parameters.AddWithValue("$timesUsed", Math.Max(0, GetInt(questionReader, 11)));
            insert.Parameters.AddWithValue("$enabled", GetInt(questionReader, 12) == 0 ? 0 : 1);
            insert.Parameters.AddWithValue("$imagePath", imagePath);
            var newId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));

            await using var map = connection.CreateCommand();
            map.Transaction = transaction;
            map.CommandText = "INSERT OR REPLACE INTO legacy_question_map(legacy_id, quiz_manager_id) VALUES($old, $new);";
            map.Parameters.AddWithValue("$old", oldId);
            map.Parameters.AddWithValue("$new", newId);
            await map.ExecuteNonQueryAsync(cancellationToken);

            existing.Add(key);
            imported++;
        }

        return (imported, skipped);
    }

    private static async Task<int> CountSourceRowsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "legacy_source", table, cancellationToken))
            return 0;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM legacy_source.{QuoteIdentifier(table)};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task RecordImportAsync(SqliteConnection connection, SqliteTransaction transaction, string sourcePath, (int Imported, int Skipped) questions, int categories, int projects, int history, int notes, int tables, CancellationToken cancellationToken)
    {
        await using var meta = connection.CreateCommand();
        meta.Transaction = transaction;
        meta.CommandText = "INSERT INTO legacy_imports(source_database, imported_at_utc, imported_questions, skipped_duplicates, imported_categories, imported_projects, imported_history, imported_notes, preserved_tables) VALUES($source, $stamp, $questions, $skipped, $categories, $projects, $history, $notes, $tables);";
        meta.Parameters.AddWithValue("$source", sourcePath);
        meta.Parameters.AddWithValue("$stamp", DateTime.UtcNow.ToString("O"));
        meta.Parameters.AddWithValue("$questions", questions.Imported);
        meta.Parameters.AddWithValue("$skipped", questions.Skipped);
        meta.Parameters.AddWithValue("$categories", categories);
        meta.Parameters.AddWithValue("$projects", projects);
        meta.Parameters.AddWithValue("$history", history);
        meta.Parameters.AddWithValue("$notes", notes);
        meta.Parameters.AddWithValue("$tables", tables);
        await meta.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureMigrationTablesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS legacy_imports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_database TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL,
                imported_questions INTEGER NOT NULL,
                skipped_duplicates INTEGER NOT NULL,
                imported_categories INTEGER NOT NULL,
                imported_projects INTEGER NOT NULL,
                imported_history INTEGER NOT NULL,
                imported_notes INTEGER NOT NULL,
                preserved_tables INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS legacy_question_map (
                legacy_id INTEGER PRIMARY KEY,
                quiz_manager_id INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string schema, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(schema)}.sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static string GetString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";
    private static int GetInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static string CopyAssetIfAvailable(string imagePath, string sourceDb, string assetsDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return "";

        var candidates = new[]
        {
            imagePath,
            Path.Combine(Path.GetDirectoryName(sourceDb) ?? "", imagePath)
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (!File.Exists(full)) continue;
                var destination = Path.Combine(assetsDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(full)}");
                File.Copy(full, destination, overwrite: false);
                return destination;
            }
            catch { }
        }

        return imagePath;
    }

    private static string Fingerprint(string question, IReadOnlyList<string> answers, int correct, string category) =>
        string.Join("\u001f", new[]
        {
            question.Trim(), category.Trim(), correct.ToString(),
            answers.ElementAtOrDefault(0)?.Trim() ?? "",
            answers.ElementAtOrDefault(1)?.Trim() ?? "",
            answers.ElementAtOrDefault(2)?.Trim() ?? "",
            answers.ElementAtOrDefault(3)?.Trim() ?? ""
        }).ToUpperInvariant();

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
