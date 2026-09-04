using QuizManager.Core.Models;
using QuizManager.Core.Services;
using Xunit;

namespace QuizManager.Core.Tests;

public sealed class QuizQuestionTests
{
    [Fact]
    public void StoresQuestionAndAnswerMetadata()
    {
        var question = new QuizQuestion(
            1,
            "Which planet is known as the Red Planet?",
            ["Earth", "Mars", "Venus", "Jupiter"],
            1,
            "Science");

        Assert.Equal("Mars", question.CorrectAnswer);
        Assert.Equal("B", question.CorrectLetter);
        Assert.Equal("Science", question.Category);
    }

    [Fact]
    public void RotationPrefersQuestionsNotRecentlyUsedAndLeastUsed()
    {
        var questions = new[]
        {
            new QuizQuestion(1, "One", ["A", "B", "C", "D"], 0, TimesUsed: 10),
            new QuizQuestion(2, "Two", ["A", "B", "C", "D"], 0, TimesUsed: 1),
            new QuizQuestion(3, "Three", ["A", "B", "C", "D"], 0, TimesUsed: 2)
        };

        var selected = QuizRotationSelector.Select(questions, 2, true, new HashSet<int> { 3 }, new Random(42));

        Assert.Equal([2, 1], selected.Select(q => q.Id));
    }
}
