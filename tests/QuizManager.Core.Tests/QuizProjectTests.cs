using QuizManager.Core.Models;
using Xunit;

namespace QuizManager.Core.Tests;

public sealed class QuizProjectTests
{
    [Fact]
    public void UsesAllCategoriesWhenCategoryIsBlank()
    {
        var project = new QuizProject(1, "General Quiz", "", 20);

        Assert.Equal("All categories", project.CategoryDisplay);
    }

    [Fact]
    public void StoresProjectConfiguration()
    {
        var created = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var project = new QuizProject(7, "Science Sprint", "Science", 15, "Weekly science quiz", created);

        Assert.Equal(7, project.Id);
        Assert.Equal("Science", project.Category);
        Assert.Equal(15, project.QuestionCount);
        Assert.Equal(created, project.CreatedAtUtc);
        Assert.True(project.IsEnabled);
    }
}
