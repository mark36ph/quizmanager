using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuizManager.Core.Models;

namespace QuizManager.Infrastructure.Data;

public sealed class QuizDatabase
{
    private readonly string _databasePath;

    public QuizDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS questions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                question TEXT NOT NULL,
                answers_json TEXT NOT NULL,
                correct_answer_index INTEGER NOT NULL,
                category TEXT NOT NULL,
                explanation TEXT NOT NULL DEFAULT '',
                difficulty TEXT NOT NULL DEFAULT 'medium',
                source TEXT NOT NULL DEFAULT '',
                times_used INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                image_path TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_questions_category_enabled
                ON questions(category, is_enabled);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuizQuestion>> GetQuestionsAsync(string? category = null, bool enabledOnly = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, question, answers_json, correct_answer_index, category,
                   explanation, difficulty, source, times_used, is_enabled, image_path
            FROM questions
            WHERE ($category IS NULL OR category = $category)
              AND ($enabledOnly = 0 OR is_enabled = 1)
            ORDER BY id DESC;
            """;
        command.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(category) ? DBNull.Value : category.Trim());
        command.Parameters.AddWithValue("$enabledOnly", enabledOnly ? 1 : 0);

        var results = new List<QuizQuestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadQuestion(reader));
        return results;
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT category FROM questions ORDER BY category;";
        var categories = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            categories.Add(reader.GetString(0));
        return categories;
    }

    public async Task<int> AddQuestionAsync(QuizQuestion question, CancellationToken cancellationToken = default)
    {
        Validate(question);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO questions
                (question, answers_json, correct_answer_index, category, explanation,
                 difficulty, source, times_used, is_enabled, image_path)
            VALUES
                ($question, $answers, $correct, $category, $explanation,
                 $difficulty, $source, $timesUsed, $enabled, $imagePath);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, question);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task UpdateQuestionAsync(QuizQuestion question, CancellationToken cancellationToken = default)
    {
        if (question.Id <= 0)
            throw new ArgumentOutOfRangeException(nameof(question), "A saved question must have a valid ID.");
        Validate(question);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE questions SET
                question = $question, answers_json = $answers, correct_answer_index = $correct,
                category = $category, explanation = $explanation, difficulty = $difficulty,
                source = $source, times_used = $timesUsed, is_enabled = $enabled, image_path = $imagePath
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", question.Id);
        AddParameters(command, question);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteQuestionAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM questions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task IncrementUsageAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var values = ids.Distinct().Where(id => id > 0).ToArray();
        if (values.Length == 0)
            return;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE questions SET times_used = times_used + 1 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static QuizQuestion ReadQuestion(SqliteDataReader reader)
    {
        var answers = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [];
        return new QuizQuestion(reader.GetInt32(0), reader.GetString(1), answers, reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetInt32(8), reader.GetInt32(9) != 0, reader.GetString(10));
    }

    private static void AddParameters(SqliteCommand command, QuizQuestion question)
    {
        command.Parameters.AddWithValue("$question", question.Question.Trim());
        command.Parameters.AddWithValue("$answers", JsonSerializer.Serialize(question.Answers));
        command.Parameters.AddWithValue("$correct", question.CorrectAnswerIndex);
        command.Parameters.AddWithValue("$category", question.Category.Trim());
        command.Parameters.AddWithValue("$explanation", question.Explanation.Trim());
        command.Parameters.AddWithValue("$difficulty", question.Difficulty.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$source", question.Source.Trim());
        command.Parameters.AddWithValue("$timesUsed", question.TimesUsed);
        command.Parameters.AddWithValue("$enabled", question.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$imagePath", question.ImagePath.Trim());
    }

    private static void Validate(QuizQuestion question)
    {
        if (string.IsNullOrWhiteSpace(question.Question))
            throw new ArgumentException("Question text is required.", nameof(question));
        if (question.Answers.Count != 4 || question.Answers.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Exactly four non-empty answers are required.", nameof(question));
        if (question.Answers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new ArgumentException("Answer choices must be distinct.", nameof(question));
        if (question.CorrectAnswerIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(question), "Correct answer must be A, B, C, or D.");
        if (string.IsNullOrWhiteSpace(question.Category))
            throw new ArgumentException("Category is required.", nameof(question));
    }
}
