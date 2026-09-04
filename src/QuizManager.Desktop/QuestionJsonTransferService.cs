using System.Text.Json;
using System.Text.Json.Serialization;
using QuizManager.Core.Models;

namespace QuizManager.Desktop;

public sealed class QuestionJsonTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task ExportAsync(string filePath, IReadOnlyList<QuizQuestion> questions, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("An export file is required.", nameof(filePath));

        var payload = new QuestionExportDocument
        {
            Questions = questions.Select(q => new QuestionTransferItem
            {
                Question = q.Question,
                Answers = q.Answers.ToArray(),
                CorrectAnswerIndex = q.CorrectAnswerIndex,
                Category = q.Category,
                Explanation = q.Explanation,
                Difficulty = q.Difficulty,
                Source = q.Source,
                Enabled = q.IsEnabled,
                ImagePath = q.ImagePath
            }).ToList()
        };

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<QuizQuestion>> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("The selected JSON file was not found.", filePath);

        await using var stream = File.OpenRead(filePath);
        var document = await JsonSerializer.DeserializeAsync<QuestionExportDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The JSON file is empty or invalid.");

        if (document.Questions is null || document.Questions.Count == 0)
            throw new InvalidDataException("The JSON file does not contain any questions.");

        var imported = new List<QuizQuestion>(document.Questions.Count);
        foreach (var item in document.Questions)
        {
            var question = item.Question?.Trim() ?? "";
            var answers = item.Answers?.Select(a => a ?? "").ToArray() ?? Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(question) || answers.Length != 4 || answers.Any(string.IsNullOrWhiteSpace))
                continue;
            if (item.CorrectAnswerIndex is < 0 or > 3)
                continue;

            imported.Add(new QuizQuestion(
                0,
                question,
                answers,
                item.CorrectAnswerIndex,
                string.IsNullOrWhiteSpace(item.Category) ? "General Knowledge" : item.Category.Trim(),
                item.Explanation?.Trim() ?? "",
                string.IsNullOrWhiteSpace(item.Difficulty) ? "medium" : item.Difficulty.Trim(),
                item.Source?.Trim() ?? "",
                0,
                item.Enabled,
                item.ImagePath?.Trim() ?? ""));
        }

        return imported;
    }

    private sealed class QuestionExportDocument
    {
        public List<QuestionTransferItem> Questions { get; set; } = [];
    }

    private sealed class QuestionTransferItem
    {
        public string? Question { get; set; }
        public string[]? Answers { get; set; }
        public int CorrectAnswerIndex { get; set; }
        public string? Category { get; set; }
        public string? Explanation { get; set; }
        public string? Difficulty { get; set; }
        public string? Source { get; set; }
        public bool Enabled { get; set; } = true;
        public string? ImagePath { get; set; }
    }
}
