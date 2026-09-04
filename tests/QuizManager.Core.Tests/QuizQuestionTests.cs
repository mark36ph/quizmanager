using QuizManager.Core.Models;

namespace QuizManager.Core.Tests;

public sealed class QuizQuestionTests
{
    [Fact]
    public void StoresQuestionAndAnswerMetadata()
    {
        var question = new QuizQuestion(
            "Which planet is known as the Red Planet?",
            ["Earth", "Mars", "Venus", "Jupiter"],
            1,
            "Science");

        Assert.Equal("Mars", question.Answers[question.CorrectAnswerIndex]);
        Assert.Equal("Science", question.Category);
    }
}
