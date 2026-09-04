using System.Collections.ObjectModel;
using QuizManager.Core.Models;
using QuizManager.Infrastructure.Data;

namespace QuizManager.Desktop;

public partial class QuizProjectsWindow : System.Windows.Window
{
    private readonly QuizProjectService _projects;
    private readonly QuestionLibraryService _library;
    private readonly ObservableCollection<QuizProject> _projectItems = [];
    private int _selectedId;

    public QuizProjectsWindow(QuizProjectService projects, QuestionLibraryService library)
    {
        _projects = projects;
        _library = library;
        InitializeComponent();
        ProjectsList.ItemsSource = _projectItems;
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

            // Promote projects preserved by the legacy database importer into the
            // native V2 list. The legacy rows remain available for future parity work.
            await new LegacyProjectMigrationService(((App)Application.Current).Database.DatabasePath)
                .MigrateAsync();

            var projects = await _projects.GetAsync();
            _projectItems.Clear();
            foreach (var project in projects)
                _projectItems.Add(project);
            StatusText.Text = $"{_projectItems.Count} saved projects";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load projects: {ex.Message}";
        }
    }

    private void Project_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProjectsList.SelectedItem is not QuizProject project)
            return;
        _selectedId = project.Id;
        NameText.Text = project.Name;
        DescriptionText.Text = project.Description;
        CountText.Text = project.QuestionCount.ToString();
        EnabledBox.IsChecked = project.IsEnabled;
        CategoryBox.SelectedItem = string.IsNullOrWhiteSpace(project.Category) ? "All categories" : project.Category;
        GeneratedStatusText.Text = project.LastGeneratedAtUtc is null
            ? "No quiz generated yet."
            : $"Last generated {project.LastGeneratedAtUtc.Value.ToLocalTime():g}";
        _ = LoadGeneratedAsync(project.Id);
    }

    private async Task LoadGeneratedAsync(int projectId)
    {
        try
        {
            var generated = await _projects.GetGeneratedAsync(projectId);
            GeneratedList.ItemsSource = generated.Select((q, i) => new GeneratedQuestion(i + 1, q)).ToList();
            if (generated.Count == 0)
                GeneratedStatusText.Text = "No quiz generated yet.";
        }
        catch (Exception ex)
        {
            GeneratedStatusText.Text = $"Could not load generated quiz: {ex.Message}";
        }
    }

    private async void Save_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(CountText.Text.Trim(), out var count) || count < 1 || count > 500)
                throw new ArgumentException("Question count must be a whole number between 1 and 500.");

            var category = CategoryBox.SelectedItem as string;
            if (category == "All categories")
                category = "";

            var existing = _projectItems.FirstOrDefault(p => p.Id == _selectedId);
            var project = new QuizProject(
                _selectedId,
                NameText.Text,
                category ?? "",
                count,
                DescriptionText.Text,
                existing?.CreatedAtUtc ?? DateTime.UtcNow,
                existing?.LastGeneratedAtUtc,
                EnabledBox.IsChecked == true);

            if (_selectedId == 0)
                _selectedId = await _projects.AddAsync(project);
            else
                await _projects.UpdateAsync(project);

            await LoadAsync();
            ProjectsList.SelectedItem = _projectItems.FirstOrDefault(p => p.Id == _selectedId);
            StatusText.Text = "Project saved.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not save project", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void Generate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedId == 0)
        {
            System.Windows.MessageBox.Show("Create or select a project first.", "Generate quiz", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        try
        {
            GeneratedStatusText.Text = "Generating quiz…";
            var generated = await _projects.GenerateAsync(_selectedId);
            GeneratedList.ItemsSource = generated.Select((q, i) => new GeneratedQuestion(i + 1, q)).ToList();
            GeneratedStatusText.Text = $"Generated {generated.Count} questions. These are the questions currently assigned to this project.";
            await LoadAsync();
            ProjectsList.SelectedItem = _projectItems.FirstOrDefault(p => p.Id == _selectedId);
        }
        catch (Exception ex)
        {
            GeneratedStatusText.Text = "Generation failed.";
            System.Windows.MessageBox.Show(ex.Message, "Could not generate quiz", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedId == 0)
            return;
        if (System.Windows.MessageBox.Show("Delete this project and its generated question list?", "Confirm delete", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        await _projects.DeleteAsync(_selectedId);
        New_Click(sender, e);
        await LoadAsync();
    }

    private void New_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _selectedId = 0;
        ProjectsList.SelectedItem = null;
        NameText.Clear();
        DescriptionText.Clear();
        CountText.Text = "10";
        EnabledBox.IsChecked = true;
        if (CategoryBox.Items.Count > 0)
            CategoryBox.SelectedIndex = 0;
        GeneratedList.ItemsSource = null;
        GeneratedStatusText.Text = "No quiz generated yet.";
    }

    private sealed class GeneratedQuestion
    {
        public GeneratedQuestion(int number, QuizQuestion question)
        {
            Number = number;
            Question = $"{number}. {question.Question}";
            AnswerA = $"A. {question.Answers.ElementAtOrDefault(0) ?? ""}";
            AnswerB = $"B. {question.Answers.ElementAtOrDefault(1) ?? ""}";
            AnswerC = $"C. {question.Answers.ElementAtOrDefault(2) ?? ""}";
            AnswerD = $"D. {question.Answers.ElementAtOrDefault(3) ?? ""}";
            CorrectText = $"Correct answer: {question.CorrectLetter} — {question.CorrectAnswer}";
        }

        public int Number { get; }
        public string Question { get; }
        public string AnswerA { get; }
        public string AnswerB { get; }
        public string AnswerC { get; }
        public string AnswerD { get; }
        public string CorrectText { get; }
    }
}
