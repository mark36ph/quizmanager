using Microsoft.Data.Sqlite;
using QuizManager.Core.Models;
using QuizManager.Core.Services;

namespace QuizManager.Infrastructure.Data;

public sealed class QuizProjectService
{
    private readonly QuizDatabase _database;
    private readonly QuestionLibraryService _library;
    private readonly string _databasePath;

    public QuizProjectService(QuizDatabase database, QuestionLibraryService library)
    {
        _database = database;
        _library = library;
        _databasePath = database.DatabasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS quiz_projects (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                category TEXT NOT NULL DEFAULT '',
                question_count INTEGER NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                created_at_utc TEXT NOT NULL,
                last_generated_at_utc TEXT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS quiz_project_questions (
                project_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                question_id INTEGER NOT NULL,
                PRIMARY KEY(project_id, position),
                FOREIGN KEY(project_id) REFERENCES quiz_projects(id) ON DELETE CASCADE,
                FOREIGN KEY(question_id) REFERENCES questions(id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS ix_project_questions_project
                ON quiz_project_questions(project_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuizProject>> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, category, question_count, description,
                   created_at_utc, last_generated_at_utc, is_enabled
            FROM quiz_projects
            ORDER BY id DESC;
            """;
        var projects = new List<QuizProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            projects.Add(ReadProject(reader));
        return projects;
    }

    public async Task<int> AddAsync(QuizProject project, CancellationToken cancellationToken = default)
    {
        Validate(project);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quiz_projects
                (name, category, question_count, description, created_at_utc, last_generated_at_utc, is_enabled)
            VALUES
                ($name, $category, $count, $description, $created, $lastGenerated, $enabled);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, project);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task UpdateAsync(QuizProject project, CancellationToken cancellationToken = default)
    {
        if (project.Id <= 0)
            throw new ArgumentOutOfRangeException(nameof(project), "A saved project must have a valid ID.");
        Validate(project);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quiz_projects SET
                name = $name, category = $category, question_count = $count,
                description = $description, is_enabled = $enabled
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name.Trim());
        command.Parameters.AddWithValue("$category", project.Category.Trim());
        command.Parameters.AddWithValue("$count", project.QuestionCount);
        command.Parameters.AddWithValue("$description", project.Description.Trim());
        command.Parameters.AddWithValue("$enabled", project.IsEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM quiz_project_questions WHERE project_id = $id; DELETE FROM quiz_projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuizQuestion>> GenerateAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("The selected quiz project no longer exists.");
        if (!project.IsEnabled)
            throw new InvalidOperationException("The selected quiz project is disabled.");

        var category = string.IsNullOrWhiteSpace(project.Category) ? null : project.Category.Trim();
        var selected = await _library.SelectQuizAsync(category ?? "", project.QuestionCount, cancellationToken: cancellationToken);
        if (selected.Count < project.QuestionCount)
        {
            var available = selected.Count;
            throw new InvalidOperationException($"This project needs {project.QuestionCount} questions, but only {available} enabled questions are available in the selected category.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM quiz_project_questions WHERE project_id = $id;";
            clear.Parameters.AddWithValue("$id", projectId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var i = 0; i < selected.Count; i++)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO quiz_project_questions(project_id, position, question_id) VALUES($project, $position, $question);";
            insert.Parameters.AddWithValue("$project", projectId);
            insert.Parameters.AddWithValue("$position", i + 1);
            insert.Parameters.AddWithValue("$question", selected[i].Id);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            stamp.CommandText = "UPDATE quiz_projects SET last_generated_at_utc = $generated WHERE id = $id;";
            stamp.Parameters.AddWithValue("$generated", DateTime.UtcNow.ToString("O"));
            stamp.Parameters.AddWithValue("$id", projectId);
            await stamp.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return selected;
    }

    public async Task<IReadOnlyList<QuizQuestion>> GetGeneratedAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT q.id, q.question, q.answers_json, q.correct_answer_index, q.category,
                   q.explanation, q.difficulty, q.source, q.times_used, q.is_enabled, q.image_path
            FROM quiz_project_questions pq
            INNER JOIN questions q ON q.id = pq.question_id
            WHERE pq.project_id = $project
            ORDER BY pq.position;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        var results = new List<QuizQuestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var answers = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [];
            results.Add(new QuizQuestion(reader.GetInt32(0), reader.GetString(1), answers, reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), reader.GetInt32(9) != 0, reader.GetString(10)));
        }
        return results;
    }

    private async Task<QuizProject?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, category, question_count, description,
                   created_at_utc, last_generated_at_utc, is_enabled
            FROM quiz_projects WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProject(reader) : null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static QuizProject ReadProject(SqliteDataReader reader)
    {
        var created = DateTime.TryParse(reader.GetString(5), out var createdAt) ? createdAt : DateTime.UtcNow;
        DateTime? lastGenerated = null;
        if (!reader.IsDBNull(6) && DateTime.TryParse(reader.GetString(6), out var parsed))
            lastGenerated = parsed;
        return new QuizProject(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), created, lastGenerated, reader.GetInt32(7) != 0);
    }

    private static void AddParameters(SqliteCommand command, QuizProject project)
    {
        command.Parameters.AddWithValue("$name", project.Name.Trim());
        command.Parameters.AddWithValue("$category", project.Category.Trim());
        command.Parameters.AddWithValue("$count", project.QuestionCount);
        command.Parameters.AddWithValue("$description", project.Description.Trim());
        command.Parameters.AddWithValue("$created", project.CreatedAtUtc == default ? DateTime.UtcNow.ToString("O") : project.CreatedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$lastGenerated", project.LastGeneratedAtUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$enabled", project.IsEnabled ? 1 : 0);
    }

    private static void Validate(QuizProject project)
    {
        if (string.IsNullOrWhiteSpace(project.Name))
            throw new ArgumentException("Project name is required.", nameof(project));
        if (project.QuestionCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(project), "Question count must be between 1 and 500.");
    }
}
