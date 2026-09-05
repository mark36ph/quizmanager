using System.IO;
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

        var exportDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var assetDirectoryName = Path.GetFileNameWithoutExtension(filePath) + ".assets";
        var assetDirectory = Path.Combine(exportDirectory, assetDirectoryName);
        Directory.CreateDirectory(assetDirectory);

        var items = new List<QuestionTransferItem>(questions.Count);
        for (var index = 0; index < questions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imagePath = await CopyExportAssetAsync(questions[index].ImagePath, assetDirectory, assetDirectoryName, index + 1, cancellationToken);
            items.Add(new QuestionTransferItem
            {
                Question = questions[index].Question,
                Answers = questions[index].Answers.ToArray(),
                CorrectAnswerIndex = questions[index].CorrectAnswerIndex,
                Category = questions[index].Category,
                Explanation = questions[index].Explanation,
                Difficulty = questions[index].Difficulty,
                Source = questions[index].Source,
                Enabled = questions[index].IsEnabled,
                ImagePath = imagePath
            });
        }

        var payload = new QuestionExportDocument { Questions = items };
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

        var importAssetDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FactburstQuizManager", "data", "assets", "questions");
        Directory.CreateDirectory(importAssetDirectory);
        var jsonDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;

        var imported = new List<QuizQuestion>(document.Questions.Count);
        foreach (var item in document.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var question = item.Question?.Trim() ?? "";
            var answers = item.Answers?.Select(a => a ?? "").ToArray() ?? Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(question) || answers.Length != 4 || answers.Any(string.IsNullOrWhiteSpace))
                continue;
            if (item.CorrectAnswerIndex is < 0 or > 3)
                continue;

            var imagePath = await ImportAssetAsync(item.ImagePath, jsonDirectory, importAssetDirectory, cancellationToken);
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
                imagePath));
        }

        return imported;
    }

    private static async Task<string> CopyExportAssetAsync(string? sourcePath, string assetDirectory, string assetDirectoryName, int index, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return "";
        var extension = Path.GetExtension(sourcePath);
        if (extension.Length == 0) return "";
        var safeName = string.Concat(Path.GetFileNameWithoutExtension(sourcePath).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_');
        if (safeName.Length == 0) safeName = "image";
        var fileName = $"{index:D4}_{safeName}{extension.ToLowerInvariant()}";
        var destination = Path.Combine(assetDirectory, fileName);
        await using var input = File.OpenRead(sourcePath);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
        return $"{assetDirectoryName}/{fileName}";
    }

    private static async Task<string> ImportAssetAsync(string? imagePath, string jsonDirectory, string importAssetDirectory, CancellationToken cancellationToken)
    {
        var value = imagePath?.Trim() ?? "";
        if (value.Length == 0) return "";
        var source = Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(jsonDirectory, value));
        if (!File.Exists(source)) return "";
        var extension = Path.GetExtension(source);
        if (extension.Length == 0) return "";
        var safeName = string.Concat(Path.GetFileNameWithoutExtension(source).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_');
        if (safeName.Length == 0) safeName = "image";
        var fileName = $"{Guid.NewGuid():N}_{safeName}{extension.ToLowerInvariant()}";
        var destination = Path.Combine(importAssetDirectory, fileName);
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
        return destination;
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
