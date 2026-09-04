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
        await using var attach = connection.CreateCommand();
        attach.Transaction = transaction;
        attach.CommandText = "ATTACH DATABASE $source AS legacy_source;";
        attach.Parameters.AddWithValue("$source", sourcePath);
        await attach.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await EnsureMigrationTablesAsync(connection, transaction, cancellationToken);
            var tables = await GetSourceTablesAsync(connection, transaction, cancellationToken);
            await PreserveLegacyTablesAsync(connection, transaction, tables, cancellationToken);

            var questionImport = await ImportQuestionsAsync(connection, transaction, sourcePath, cancellationToken);
            var categories = await ImportCategoriesAsync(connection, transaction, cancellationToken);
            var projects = await ImportProjectsAsync(connection, transaction, cancellationToken);
            var history = await ImportHistoryAsync(connection, transaction, cancellationToken);
            var notes = await ImportNotesAsync(connection, transaction, cancellationToken);

            await using var meta = connection.CreateCommand();
            meta.Transaction = transaction;
            meta.CommandText = "INSERT INTO legacy_imports(source_database, imported_at_utc, imported_questions, skipped_duplicates, imported_categories, imported_projects, imported_history, imported_notes, preserved_tables) VALUES($source, $stamp, $questions, $skipped, $categories, $projects, $history, $notes, $tables);";
            meta.Parameters.AddWithValue("$source", sourcePath);
            meta.Parameters.AddWithValue("$stamp", DateTime.UtcNow.ToString("O"));
            meta.Parameters.AddWithValue("$questions", questionImport.Imported);
            meta.Parameters.AddWithValue("$skipped", questionImport.Skipped);
            meta.Parameters.AddWithValue("$categories", categories);
            meta.Parameters.AddWithValue("$projects", projects);
            meta.Parameters.AddWithValue("$history", history);
            meta.Parameters.AddWithValue("$notes", notes);
            meta.Parameters.AddWithValue("$tables", tables.Count);
            await meta.ExecuteNonQueryAsync(cancellationToken);

            await using var detach = connection.CreateCommand();
            detach.Transaction = transaction;
            detach.CommandText = "DETACH DATABASE legacy_source;";
            await detach.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new LegacyImportResult(questionImport.Imported, questionImport.Skipped, categories, projects, history, notes, tables.Count, backupPath, sourcePath);
        }
        catch
        {
            try
            {
                await using var detach = connection.CreateCommand();
                detach.Transaction = transaction;
                detach.CommandText = "DETACH DATABASE legacy_source;";
                await detach.ExecuteNonQueryAsync(cancellationToken);
            }
            catch { }
            throw;
        }
    }

    private async Task<string> CreateBackupAsync(CancellationToken cancellationToken)
    {
        var backupDirectory = Path.Combine(_dataDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"quizmanager-before-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        if (File.Exists(_destinationPath))
        {
            SqliteConnection.ClearAllPools();
            await using var source = new FileStream(_destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await using var destination = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await source.CopyToAsync(destination, cancellationToken);
        }
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
        var existing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT id, question, answers_json, correct_answer_index, category FROM questions;";
            await using var existingReader = await current.ExecuteReaderAsync(cancellationToken);
            while (await existingReader.ReadAsync(cancellationToken))
                existing[Fingerprint(existingReader.GetString(1), JsonSerializer.Deserialize<string[]>(existingReader.GetString(2)) ?? [], existingReader.GetInt32(3), existingReader.GetString(4))] = existingReader.GetInt32(0);
        }

        var assetsDirectory = Path.Combine(Path.GetDirectoryName(_destinationPath) ?? _dataDirectory, "imported-assets");
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
            var oldId = questionReader.GetInt32(0);
            var question = questionReader.GetString(1);
            var answers = new[] { questionReader.GetString(2), questionReader.GetString(3), questionReader.GetString(4), questionReader.GetString(5) };
            var correct = questionReader.GetInt32(6);
            var category = questionReader.GetString(8);
            var key = Fingerprint(question, answers, correct, category);
            if (existing.ContainsKey(key)) { skipped++; continue; }

            var imagePath = questionReader.IsDBNull(13) ? "" : questionReader.GetString(13);
            imagePath = CopyAssetIfAvailable(imagePath, sourcePath, assetsDirectory);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO questions(question, answers_json, correct_answer_index, category, explanation, difficulty, source, times_used, is_enabled, image_path) VALUES($question, $answers, $correct, $category, $explanation, $difficulty, $source, $timesUsed, $enabled, $imagePath); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$question", question);
            insert.Parameters.AddWithValue("$answers", JsonSerializer.Serialize(answers));
            insert.Parameters.AddWithValue("$correct", correct);
            insert.Parameters.AddWithValue("$category", category);
            insert.Parameters.AddWithValue("$explanation", questionReader.GetString(7));
            insert.Parameters.AddWithValue("$difficulty", questionReader.GetString(9));
            insert.Parameters.AddWithValue("$source", questionReader.GetString(10));
            insert.Parameters.AddWithValue("$timesUsed", questionReader.GetInt32(11));
            insert.Parameters.AddWithValue("$enabled", questionReader.GetInt32(12));
            insert.Parameters.AddWithValue("$imagePath", imagePath);
            var newId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
            existing[key] = newId;
            await using var map = connection.CreateCommand();
            map.Transaction = transaction;
            map.CommandText = "INSERT OR REPLACE INTO legacy_question_map(legacy_id, quiz_manager_id) VALUES($old, $new);";
            map.Parameters.AddWithValue("$old", oldId);
            map.Parameters.AddWithValue("$new", newId);
            await map.ExecuteNonQueryAsync(cancellationToken);
            imported++;
        }
        return (imported, skipped);
    }

    private static async Task<int> ImportCategoriesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "legacy_source", "categories", cancellationToken)) return 0;
        var count = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM legacy_source.categories ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO categories(name) VALUES($name);";
            insert.Parameters.AddWithValue("$name", reader.GetString(0));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0) count++;
        }
        return count;
    }

    private static async Task<int> ImportProjectsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "legacy_source", "projects", cancellationToken)) return 0;
        var count = 0;
        await using var readerCommand = connection.CreateCommand();
        readerCommand.Transaction = transaction;
        readerCommand.CommandText = "SELECT id,title,category,description,created,status,pinned FROM legacy_source.projects ORDER BY id;";
        await using var reader = await readerCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO quiz_projects(name,category,question_count,description,created_at_utc,last_generated_at_utc,is_enabled) VALUES($name,$category,0,$description,$created,NULL,1); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$category", reader.GetString(2));
            insert.Parameters.AddWithValue("$description", reader.GetString(3));
            insert.Parameters.AddWithValue("$created", NormalizeDate(reader.GetString(4)));
            var newId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));

            await using var meta = connection.CreateCommand();
            meta.Transaction = transaction;
            meta.CommandText = "INSERT OR REPLACE INTO legacy_project_metadata(legacy_id,quiz_manager_id,status,folder,script,on_screen_text,visual_plan,pinned_comment,notes,views,likes,upload_date,youtube_url,pinned,updated,scheduled_for,search_terms,broll_plan,thumbnail_prompt,tags,sources,subtitle_text,narration_duration,research_complete,script_complete,voice_complete,subtitles_complete,broll_complete,graphics_complete,capcut_complete,export_complete,upload_complete) SELECT id,$newId,status,folder,script,on_screen_text,visual_plan,pinned_comment,notes,views,likes,upload_date,youtube_url,pinned,updated,scheduled_for,search_terms,broll_plan,thumbnail_prompt,tags,sources,subtitle_text,narration_duration,research_complete,script_complete,voice_complete,subtitles_complete,broll_complete,graphics_complete,capcut_complete,export_complete,upload_complete FROM legacy_source.projects WHERE id=$oldId;";
            meta.Parameters.AddWithValue("$newId", newId);
            meta.Parameters.AddWithValue("$oldId", reader.GetInt32(0));
            await meta.ExecuteNonQueryAsync(cancellationToken);
            count++;
        }
        return count;
    }

    private static async Task<int> ImportHistoryAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "legacy_source", "quiz_history", cancellationToken)) return 0;
        await EnsureImportedHistoryTablesAsync(connection, transaction, cancellationToken);
        var count = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM legacy_source.quiz_history ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO imported_quiz_history(legacy_id,title,created,question_count,categories,format,question_seconds,shuffle_answers,project_folder,series_name,episode_number,youtube_title,youtube_description,youtube_hashtags,pinned_comment,published_on_youtube,youtube_url,youtube_views,youtube_likes,youtube_upload_date,published_on_facebook,facebook_url,published_on_instagram,instagram_url,instagram_upload_date,youtube_first_comment_id,facebook_first_comment_id,youtube_privacy,youtube_scheduled_for,facebook_scheduled_for) VALUES($id,$title,$created,$count,$categories,$format,$seconds,$shuffle,$folder,$series,$episode,$ytTitle,$ytDesc,$hashtags,$comment,$ytPub,$ytUrl,$ytViews,$ytLikes,$ytDate,$fbPub,$fbUrl,$igPub,$igUrl,$igDate,$ytComment,$fbComment,$privacy,$ytSchedule,$fbSchedule);";
            AddReaderParameter(insert,"$id",reader,0); AddReaderParameter(insert,"$title",reader,1); AddReaderParameter(insert,"$created",reader,2); AddReaderParameter(insert,"$count",reader,3); AddReaderParameter(insert,"$categories",reader,4); AddReaderParameter(insert,"$format",reader,5); AddReaderParameter(insert,"$seconds",reader,6); AddReaderParameter(insert,"$shuffle",reader,7); AddReaderParameter(insert,"$folder",reader,8); AddReaderParameter(insert,"$series",reader,9); AddReaderParameter(insert,"$episode",reader,10); AddReaderParameter(insert,"$ytTitle",reader,11); AddReaderParameter(insert,"$ytDesc",reader,12); AddReaderParameter(insert,"$hashtags",reader,13); AddReaderParameter(insert,"$comment",reader,14); AddReaderParameter(insert,"$ytPub",reader,15); AddReaderParameter(insert,"$ytUrl",reader,16); AddReaderParameter(insert,"$ytViews",reader,17); AddReaderParameter(insert,"$ytLikes",reader,18); AddReaderParameter(insert,"$ytDate",reader,19); AddReaderParameter(insert,"$fbPub",reader,20); AddReaderParameter(insert,"$fbUrl",reader,21); AddReaderParameter(insert,"$fbViews",reader,22); AddReaderParameter(insert,"$fbReactions",reader,23); AddReaderParameter(insert,"$fbComments",reader,24); AddReaderParameter(insert,"$fbShares",reader,25); AddReaderParameter(insert,"$fbDate",reader,26); AddReaderParameter(insert,"$igPub",reader,27); AddReaderParameter(insert,"$igUrl",reader,28); AddReaderParameter(insert,"$igDate",reader,29); AddReaderParameter(insert,"$ytComment",reader,30); AddReaderParameter(insert,"$fbComment",reader,31); AddReaderParameter(insert,"$privacy",reader,32); AddReaderParameter(insert,"$ytSchedule",reader,33); AddReaderParameter(insert,"$fbSchedule",reader,34);
            await insert.ExecuteNonQueryAsync(cancellationToken); count++;
        }
        return count;
    }

    private static async Task<int> ImportNotesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "legacy_source", "fact_notes", cancellationToken)) return 0;
        await EnsureImportedNotesTableAsync(connection, transaction, cancellationToken);
        var count = 0;
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT id,title,category,notes,status,created,pinned,checked FROM legacy_source.fact_notes ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO imported_fact_notes(legacy_id,title,category,notes,status,created,pinned,checked) VALUES($id,$title,$category,$notes,$status,$created,$pinned,$checked);";
            AddReaderParameter(insert,"$id",reader,0); AddReaderParameter(insert,"$title",reader,1); AddReaderParameter(insert,"$category",reader,2); AddReaderParameter(insert,"$notes",reader,3); AddReaderParameter(insert,"$status",reader,4); AddReaderParameter(insert,"$created",reader,5); AddReaderParameter(insert,"$pinned",reader,6); AddReaderParameter(insert,"$checked",reader,7);
            await insert.ExecuteNonQueryAsync(cancellationToken); count++;
        }
        return count;
    }

    private static async Task EnsureMigrationTablesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS legacy_imports(id INTEGER PRIMARY KEY AUTOINCREMENT, source_database TEXT NOT NULL, imported_at_utc TEXT NOT NULL, imported_questions INTEGER NOT NULL, skipped_duplicates INTEGER NOT NULL, imported_categories INTEGER NOT NULL, imported_projects INTEGER NOT NULL, imported_history INTEGER NOT NULL, imported_notes INTEGER NOT NULL, preserved_tables INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS legacy_question_map(legacy_id INTEGER PRIMARY KEY, quiz_manager_id INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS legacy_project_metadata(legacy_id INTEGER PRIMARY KEY, quiz_manager_id INTEGER NOT NULL, status TEXT NOT NULL DEFAULT '', folder TEXT NOT NULL DEFAULT '', script TEXT NOT NULL DEFAULT '', on_screen_text TEXT NOT NULL DEFAULT '', visual_plan TEXT NOT NULL DEFAULT '', pinned_comment TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '', views INTEGER NOT NULL DEFAULT 0, likes INTEGER NOT NULL DEFAULT 0, upload_date TEXT NOT NULL DEFAULT '', youtube_url TEXT NOT NULL DEFAULT '', pinned INTEGER NOT NULL DEFAULT 0, updated TEXT NOT NULL DEFAULT '', scheduled_for TEXT NOT NULL DEFAULT '', search_terms TEXT NOT NULL DEFAULT '', broll_plan TEXT NOT NULL DEFAULT '', thumbnail_prompt TEXT NOT NULL DEFAULT '', tags TEXT NOT NULL DEFAULT '', sources TEXT NOT NULL DEFAULT '', subtitle_text TEXT NOT NULL DEFAULT '', narration_duration REAL NOT NULL DEFAULT 0, research_complete INTEGER NOT NULL DEFAULT 0, script_complete INTEGER NOT NULL DEFAULT 0, voice_complete INTEGER NOT NULL DEFAULT 0, subtitles_complete INTEGER NOT NULL DEFAULT 0, broll_complete INTEGER NOT NULL DEFAULT 0, graphics_complete INTEGER NOT NULL DEFAULT 0, capcut_complete INTEGER NOT NULL DEFAULT 0, export_complete INTEGER NOT NULL DEFAULT 0, upload_complete INTEGER NOT NULL DEFAULT 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureImportedHistoryTablesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS imported_quiz_history(id INTEGER PRIMARY KEY AUTOINCREMENT, legacy_id INTEGER UNIQUE NOT NULL, title TEXT NOT NULL, created TEXT NOT NULL, question_count INTEGER NOT NULL, categories TEXT NOT NULL, format TEXT NOT NULL, question_seconds INTEGER NOT NULL, shuffle_answers INTEGER NOT NULL, project_folder TEXT NOT NULL, series_name TEXT NOT NULL, episode_number INTEGER NOT NULL, youtube_title TEXT NOT NULL, youtube_description TEXT NOT NULL, youtube_hashtags TEXT NOT NULL, pinned_comment TEXT NOT NULL, published_on_youtube INTEGER NOT NULL, youtube_url TEXT NOT NULL, youtube_views INTEGER NOT NULL, youtube_likes INTEGER NOT NULL, youtube_upload_date TEXT NOT NULL, published_on_facebook INTEGER NOT NULL, facebook_url TEXT NOT NULL, facebook_views INTEGER NOT NULL, facebook_reactions INTEGER NOT NULL, facebook_comments INTEGER NOT NULL, facebook_shares INTEGER NOT NULL, facebook_upload_date TEXT NOT NULL, published_on_instagram INTEGER NOT NULL, instagram_url TEXT NOT NULL, instagram_upload_date TEXT NOT NULL, youtube_first_comment_id TEXT NOT NULL, facebook_first_comment_id TEXT NOT NULL, youtube_privacy TEXT NOT NULL, youtube_scheduled_for TEXT NOT NULL, facebook_scheduled_for TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureImportedNotesTableAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS imported_fact_notes(id INTEGER PRIMARY KEY AUTOINCREMENT, legacy_id INTEGER UNIQUE NOT NULL, title TEXT NOT NULL, category TEXT NOT NULL, notes TEXT NOT NULL, status TEXT NOT NULL, created TEXT NOT NULL, pinned INTEGER NOT NULL, checked INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string schema, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(schema)}.sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static void AddReaderParameter(SqliteCommand command, string name, SqliteDataReader reader, int ordinal)
    {
        command.Parameters.AddWithValue(name, reader.IsDBNull(ordinal) ? DBNull.Value : reader.GetValue(ordinal));
    }

    private static string NormalizeDate(string value) => DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime().ToString("O") : value;

    private static string CopyAssetIfAvailable(string imagePath, string sourceDb, string assetsDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return "";
        var candidates = new[] { imagePath, Path.Combine(Path.GetDirectoryName(sourceDb) ?? "", imagePath) };
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
        string.Join("\u001f", new[] { question.Trim(), category.Trim(), correct.ToString(), answers[0].Trim(), answers[1].Trim(), answers[2].Trim(), answers[3].Trim() }).ToUpperInvariant();

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
