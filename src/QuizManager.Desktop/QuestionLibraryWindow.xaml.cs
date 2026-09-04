using System.Collections.ObjectModel;
using QuizManager.Core.Models;
using QuizManager.Infrastructure.Data;

namespace QuizManager.Desktop;

public partial class QuestionLibraryWindow : System.Windows.Window
{
    private readonly QuestionLibraryService _library;
    private readonly ObservableCollection<QuizQuestion> _questions = [];
    private int _selectedId;

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
                EnabledBox.IsChecked == true);

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
        QuestionsGrid.SelectedItem = null;
        QuestionText.Clear(); AnswerA.Clear(); AnswerB.Clear(); AnswerC.Clear(); AnswerD.Clear();
        CorrectBox.SelectedIndex = 0; CategoryText.Text = "General Knowledge"; DifficultyBox.SelectedIndex = 1;
        EnabledBox.IsChecked = true; ExplanationText.Clear(); SourceText.Clear();
    }

    private void AddQuestion_Click(object sender, System.Windows.RoutedEventArgs e) => New_Click(sender, e);

    private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => await RefreshAsync();
    private async void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsInitialized) await RefreshAsync();
    }

    private void SelectById(int id)
    {
        QuestionsGrid.SelectedItem = _questions.FirstOrDefault(q => q.Id == id);
        if (QuestionsGrid.SelectedItem is not null)
            QuestionsGrid.ScrollIntoView(QuestionsGrid.SelectedItem);
    }
}
