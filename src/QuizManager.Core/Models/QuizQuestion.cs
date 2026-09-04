namespace QuizManager.Core.Models;

public sealed record QuizQuestion(
    string Question,
    IReadOnlyList<string> Answers,
    int CorrectAnswerIndex,
    string Category = "General");
