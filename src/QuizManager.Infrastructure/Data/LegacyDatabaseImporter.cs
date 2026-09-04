using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuizManager.Infrastructure.Data;

public sealed record LegacyImportResult(
    int ImportedQuestions,
    int SkippedDuplicateQuestions,
    int LegacyTablesPreserved,
    string BackupPath,
    string SourceDatabasePath);

public sealed class LegacyDatabaseImporter
{
    private readonly string _destinationPath;
    private readonly string _dataRoot;

    public LegacyDatabaseImporter(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        _destinationPath = Path.GetFullPath(destinationPath);
        _dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(_destinationPath) ?? _destinationPath) ?? AppContext.BaseDirectory;
    }

    public async Task<LegacyImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected database could not be found.", sourcePath);
        sourcePath = Path.GetFullPath(sourcePath);
        if (string.Equals(sourcePath, _destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is already the current Quiz Manager database.");

        await ValidateLegacyDatabaseAsync(sourcePath, cancellationToken);

        var backupDirectory = Path.Combine(_dataRoot, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"quizmanager-before-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        if (File.Exists(_destinationPath))
            File.Copy(_destinationPath, backupPath, overwrite: false);

        await using var connection = new SqliteConnection($"Data Source={_destinationPath}");
        await connection.OpenAsync(cancellationToken);
        await using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $source AS legacy_source;";
        attach.Parameters.AddWithValue("$source", sourcePath);
        await attach.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await EnsureImportMetadataAsync(connection, cancellationToken);
            var stamp = DateTime.UtcNow.ToString("O");
            var tables = await GetSourceTablesAsync(connection, cancellationToken);
            foreach (var table in tables)
            {
                var legacyName = "legacy_" + table;
                await using var copy = connection.CreateCommand();
                copy.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(legacyName)}; CREATE TABLE {QuoteIdentifier(legacyName)} AS SELECT * FROM legacy_source.{QuoteIdentifier(table)};";
                await copy.ExecuteNonQueryAsync(cancellationToken);
            }

            var (imported, skipped) = await ImportQuestionsAsync(connection, sourcePath, cancellationToken);
            await using var meta = connection.CreateCommand();
            meta.CommandText = "INSERT INTO legacy_imports(source_database, imported_at_utc, imported_questions, skipped_duplicates, preserved_tables) VALUES($source, $stamp, $imported, $skipped, $tables);";
            meta.Parameters.AddWithValue("$source", sourcePath);
            meta.Parameters.AddWithValue("$stamp", stamp);
            meta.Parameters.AddWithValue("$imported", imported);
            meta.Parameters.AddWithValue("$skipped", skipped);
            meta.Parameters.AddWithValue("$tables", tables.Count);
            await meta.ExecuteNonQueryAsync(cancellationToken);
            return new LegacyImportResult(imported, skipped, tables.Count, backupPath, sourcePath);
        }
        finally
        {
            await using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE legacy_source;";
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }
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

    private static async Task<List<string>> GetSourceTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM legacy_source.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<(int Imported, int Skipped)> ImportQuestionsAsync(SqliteConnection connection, string sourcePath, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var current = connection.CreateCommand())
        {
            current.CommandText = "SELECT question, answers_json, correct_answer_index, category FROM questions;";
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existing.Add(Fingerprint(reader.GetString(0), JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [], reader.GetInt32(2), reader.GetString(3)));
        }

        var assetsDirectory = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(connection.DataSource) ?? connection.DataSource) ?? AppContext.BaseDirectory, "data", "imported-assets");
        Directory.CreateDirectory(assetsDirectory);
        var imported = 0;
        var skipped = 0;

        await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
        await source.OpenAsync(cancellationToken);
        await using var readerCommand = source.CreateCommand();
        readerCommand.CommandText = "SELECT question, option_a, option_b, option_c, option_d, correct_index, explanation, category, difficulty, source, times_used, enabled, image_path FROM quiz_questions ORDER BY id;";
        await using var reader = await readerCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var question = reader.GetString(0);
            var answers = new[] { reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4) };
            var correct = reader.GetInt32(5);
            var category = reader.GetString(7);
            var key = Fingerprint(question, answers, correct, category);
            if (!existing.Add(key))
            {
                skipped++;
                continue;
            }

            var imagePath = reader.IsDBNull(12) ? "" : reader.GetString(12);
            imagePath = CopyAssetIfAvailable(imagePath, sourcePath, assetsDirectory);
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO questions(question, answers_json, correct_answer_index, category, explanation, difficulty, source, times_used, is_enabled, image_path) VALUES($question, $answers, $correct, $category, $explanation, $difficulty, $source, $timesUsed, $enabled, $imagePath);";
            insert.Parameters.AddWithValue("$question", question);
            insert.Parameters.AddWithValue("$answers", JsonSerializer.Serialize(answers));
            insert.Parameters.AddWithValue("$correct", correct);
            insert.Parameters.AddWithValue("$category", category);
            insert.Parameters.AddWithValue("$explanation", reader.GetString(6));
            insert.Parameters.AddWithValue("$difficulty", reader.GetString(8));
            insert.Parameters.AddWithValue("$source", reader.GetString(9));
            insert.Parameters.AddWithValue("$timesUsed", reader.GetInt32(10));
            insert.Parameters.AddWithValue("$enabled", reader.GetInt32(11));
            insert.Parameters.AddWithValue("$imagePath", imagePath);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            imported++;
        }
        return (imported, skipped);
    }

    private static string CopyAssetIfAvailable(string imagePath, string sourceDb, string assetsDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return "";
        var candidates = new[] { imagePath, Path.Combine(Path.GetDirectoryName(sourceDb) ?? "", imagePath) };
        var source = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (source is null)
            return imagePath;
        var destination = Path.Combine(assetsDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(source)}");
        File.Copy(source, destination, overwrite: false);
        return destination;
    }

    private static string Fingerprint(string question, IReadOnlyList<string> answers, int correct, string category) =>
        string.Join("\u001f", new[] { question, category, correct.ToString(), answers[0], answers[1], answers[2], answers[3] }).Trim().ToUpperInvariant();

    private static async Task EnsureImportMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS legacy_imports(id INTEGER PRIMARY KEY AUTOINCREMENT, source_database TEXT NOT NULL, imported_at_utc TEXT NOT NULL, imported_questions INTEGER NOT NULL, skipped_duplicates INTEGER NOT NULL, preserved_tables INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
