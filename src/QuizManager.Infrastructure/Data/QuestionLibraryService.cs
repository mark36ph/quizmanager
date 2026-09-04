using QuizManager.Core.Models;
using QuizManager.Core.Services;

namespace QuizManager.Infrastructure.Data;

public sealed class QuestionLibraryService
{
    private readonly QuizDatabase _database;

    public QuestionLibraryService(QuizDatabase database)
    {
        _database = database;
    }

    public Task<IReadOnlyList<QuizQuestion>> GetAsync(string? category = null, bool enabledOnly = false, CancellationToken cancellationToken = default) =>
        _database.GetQuestionsAsync(category, enabledOnly, cancellationToken);

    public Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
        _database.GetCategoriesAsync(cancellationToken);

    public Task<int> AddAsync(QuizQuestion question, CancellationToken cancellationToken = default) =>
        _database.AddQuestionAsync(question, cancellationToken);

    public Task UpdateAsync(QuizQuestion question, CancellationToken cancellationToken = default) =>
        _database.UpdateQuestionAsync(question, cancellationToken);

    public Task DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        _database.DeleteQuestionAsync(id, cancellationToken);

    public async Task<IReadOnlyList<QuizQuestion>> SelectQuizAsync(
        string category,
        int count,
        IReadOnlySet<int>? recentlyUsed = null,
        CancellationToken cancellationToken = default)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "Question count must be at least 1.");

        var normalizedCategory = string.Equals(category.Trim(), "All categories", StringComparison.OrdinalIgnoreCase)
            ? null
            : category;
        var questions = await _database.GetQuestionsAsync(normalizedCategory, enabledOnly: true, cancellationToken);
        if (questions.Count < count)
            throw new InvalidOperationException($"Only {questions.Count} enabled questions are available for this project, but {count} are required.");

        var selected = QuizRotationSelector.Select(questions, count, preferLeastUsed: true, recentlyUsed);
        await _database.IncrementUsageAsync(selected.Select(q => q.Id), cancellationToken);
        return selected;
    }
}
