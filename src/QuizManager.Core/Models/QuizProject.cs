namespace QuizManager.Core.Models;

public sealed record QuizProject(
    int Id,
    string Name,
    string Category,
    int QuestionCount,
    string Description = "",
    DateTime CreatedAtUtc = default,
    DateTime? LastGeneratedAtUtc = null,
    bool IsEnabled = true)
{
    public string CategoryDisplay => string.IsNullOrWhiteSpace(Category) ? "All categories" : Category;
}
