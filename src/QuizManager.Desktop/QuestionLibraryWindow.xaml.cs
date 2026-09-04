using System.Collections.ObjectModel;
using Microsoft.Win32;
using QuizManager.Core.Models;
using QuizManager.Infrastructure.Data;

namespace QuizManager.Desktop;

public partial class QuestionLibraryWindow : System.Windows.Window
{
    private readonly QuestionLibraryService _library;
    private readonly ObservableCollection<QuizQuestion> _questions = [];
    private readonly QuestionJsonTransferService _jsonTransfer = new();
    private int _selectedId;
    private string _selectedImagePath = "";

    public QuestionLibraryWindow(QuestionLibraryService library)
    {
        _library = library;
        InitializeComponent();
        QuestionsGrid.ItemsSource = _questions;
        CorrectBox.SelectedIndex = 0;
        DifficultyBox.SelectedIndex = 1;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var categories = await _library.GetCategoriesAsync();
            CategoryBox.Items.Clear();
            CategoryBox.Items.Add("All categories");
            foreach (var category in categories)
                CategoryBox.Items.Add(category);
            CategoryBox.SelectedIndex = 0;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load library: {ex.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        var category = CategoryBox.SelectedItem as string;
        if (category == "All categories") category = null;
        var all = await _library.GetAsync(category);
        _questions.Clear();
        var search = SearchBox.Text.Trim();
        foreach (var question in all.Where(q => search.Length == 0 || q.Question.Contains(search, StringComparison.OrdinalIgnoreCase)))
            _questions.Add(question);
        StatusText.Text = $"{_questions.Count} questions";
    }

    private void QuestionsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QuestionsGrid.SelectedItem is not QuizQuestion question) return;
        _selectedId = question.Id;
        _selectedImagePath = question.ImagePath ?? "";
        QuestionText.Text = question.Question;
        AnswerA.Text = question.Answers.ElementAtOrDefault(0) ?? "";
        AnswerB.Text = question.Answers.ElementAtOrDefault(1) ?? "";
        AnswerC.Text = question.Answers.ElementAtOrDefault(2) ?? "";
        AnswerD.Text = question.Answers.ElementAtOrDefault(3) ?? "";
        CorrectBox.SelectedIndex = question.CorrectAnswerIndex;
        CategoryText.Text = question.Category;
        DifficultyBox.SelectedIndex = Math.Max(0, new[] { "easy", "medium", "hard", "insane" }.ToList().IndexOf(question.Difficulty.ToLowerInvariant()));
        EnabledBox.IsChecked = question.IsEnabled;
        ExplanationText.Text = question.Explanation;
        SourceText.Text = question.Source;
        ImagePathText.Text = string.IsNullOrWhiteSpace(_selectedImagePath) ? "No image selected" : _selectedImagePath;
        UpdateImagePreview(_selectedImagePath);
    }

    private async void Save_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var question = new QuizQuestion(
                _selectedId,
                QuestionText.Text,
                [AnswerA.Text, AnswerB.Text, AnswerC.Text, AnswerD.Text],
                CorrectBox.SelectedIndex,
                string.IsNullOrWhiteSpace(CategoryText.Text) ? "General Knowledge" : CategoryText.Text,
                ExplanationText.Text,
                (DifficultyBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "medium",
                SourceText.Text,
                _questions.FirstOrDefault(q => q.Id == _selectedId)?.TimesUsed ?? 0,
                EnabledBox.IsChecked == true,
                _selectedImagePath);

            if (_selectedId == 0)
                _selectedId = await _library.AddAsync(question);
            else
                await _library.UpdateAsync(question);

            await LoadAsync();
            SelectById(_selectedId);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not save question", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedId == 0) return;
        if (System.Windows.MessageBox.Show("Delete this question?", "Confirm delete", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        await _library.DeleteAsync(_selectedId);
        New_Click(sender, e);
        await LoadAsync();
    }

    private void New_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _selectedId = 0;
        _selectedImagePath = "";
        QuestionsGrid.SelectedItem = null;
        QuestionText.Clear(); AnswerA.Clear(); AnswerB.Clear(); AnswerC.Clear(); AnswerD.Clear();
        CorrectBox.SelectedIndex = 0; CategoryText.Text = "General Knowledge"; DifficultyBox.SelectedIndex = 1;
        EnabledBox.IsChecked = true; ExplanationText.Clear(); SourceText.Clear();
        ImagePathText.Text = "No image selected";
        UpdateImagePreview("");
    }

    private void AddQuestion_Click(object sender, System.Windows.RoutedEventArgs e) => New_Click(sender, e);

    private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => await RefreshAsync();
    private async void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsInitialized) await RefreshAsync();
    }

    private async void ImportJson_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import questions",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var imported = await _jsonTransfer.ImportAsync(dialog.FileName);
            var added = 0;
            foreach (var question in imported)
            {
                await _library.AddAsync(question);
                added++;
            }

            await LoadAsync();
            StatusText.Text = $"Imported {added} questions";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not import questions", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void ExportJson_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export questions",
            FileName = "quiz-questions.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var category = CategoryBox.SelectedItem as string;
            if (category == "All categories") category = null;
            var questions = await _library.GetAsync(category);
            await _jsonTransfer.ExportAsync(dialog.FileName, questions);
            StatusText.Text = $"Exported {questions.Count} questions";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not export questions", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void SelectImage_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select question image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        _selectedImagePath = dialog.FileName;
        ImagePathText.Text = _selectedImagePath;
        UpdateImagePreview(_selectedImagePath);
    }

    private void ClearImage_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _selectedImagePath = "";
        ImagePathText.Text = "No image selected";
        UpdateImagePreview("");
    }

    private void UpdateImagePreview(string imagePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
            {
                ImagePreview.Source = null;
                return;
            }

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 300;
            bitmap.EndInit();
            bitmap.Freeze();
            ImagePreview.Source = bitmap;
        }
        catch
        {
            ImagePreview.Source = null;
        }
    }

    private void SelectById(int id)
    {
        QuestionsGrid.SelectedItem = _questions.FirstOrDefault(q => q.Id == id);
        if (QuestionsGrid.SelectedItem is not null)
            QuestionsGrid.ScrollIntoView(QuestionsGrid.SelectedItem);
    }
}
