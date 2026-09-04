using QuizManager.Core.Models;

namespace QuizManager.Core.Services;

public interface IQuizGenerator
{
    Task<IReadOnlyList<QuizQuestion>> GenerateAsync(
        string category,
        int questionCount,
        CancellationToken cancellationToken = default);
}
