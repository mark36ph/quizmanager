using QuizManager.Core.Models;

namespace QuizManager.Core.Services;

public static class QuizRotationSelector
{
    public static IReadOnlyList<QuizQuestion> Select(
        IEnumerable<QuizQuestion> questions,
        int count,
        bool preferLeastUsed = true,
        IReadOnlySet<int>? recentlyUsedQuestionIds = null,
        Random? random = null)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Question count must be greater than zero.");

        var pool = questions
            .Where(q => q.IsEnabled)
            .GroupBy(q => q.Id)
            .Select(g => g.First())
            .ToList();

        if (count > pool.Count)
            throw new InvalidOperationException($"Only {pool.Count} enabled questions are available, but {count} were requested.");

        recentlyUsedQuestionIds ??= new HashSet<int>();
        random ??= Random.Shared;

        return pool
            .Select(q => new RankedQuestion(
                q,
                recentlyUsedQuestionIds.Contains(q.Id),
                preferLeastUsed ? q.TimesUsed : 0,
                random.NextDouble()))
            .OrderBy(x => x.Recent)
            .ThenBy(x => x.Usage)
            .ThenBy(x => x.Random)
            .Take(count)
            .Select(x => x.Question)
            .ToList();
    }

    private sealed record RankedQuestion(QuizQuestion Question, bool Recent, int Usage, double Random);
}
