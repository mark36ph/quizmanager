namespace QuizManager.Core.Models;

public sealed record QuizQuestion(
    int Id,
    string Question,
    IReadOnlyList<string> Answers,
    int CorrectAnswerIndex,
    string Category = "General Knowledge",
    string Explanation = "",
    string Difficulty = "medium",
    string Source = "",
    int TimesUsed = 0,
    bool IsEnabled = true,
    string ImagePath = "")
{
    public string CorrectAnswer =>
        CorrectAnswerIndex >= 0 && CorrectAnswerIndex < Answers.Count
            ? Answers[CorrectAnswerIndex]
            : "";

    public string CorrectLetter =>
        CorrectAnswerIndex is >= 0 and <= 3
            ? ((char)('A' + CorrectAnswerIndex)).ToString()
            : "";

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);
}
