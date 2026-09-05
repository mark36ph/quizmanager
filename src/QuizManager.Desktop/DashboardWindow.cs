using QuizManager.Infrastructure.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuizManager.Desktop;

public sealed class DashboardWindow : Window
{
    private readonly QuestionLibraryService _questions;
    private readonly QuizProjectService _projects;
    private readonly PublishingService _publishing;
    private readonly StackPanel _content = new();

    public DashboardWindow(QuestionLibraryService questions, QuizProjectService projects, PublishingService publishing)
    {
        _questions = questions;
        _projects = projects;
        _publishing = publishing;
        Title = "Factburst Quiz Manager — Dashboard";
        Width = 900;
        Height = 600;
        MinWidth = 760;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 31, 43));
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Dashboard", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        Grid.SetRow(_content, 1);
        _content.Margin = new Thickness(0, 24, 0, 24);
        root.Children.Add(_content);
        var refresh = new Button { Content = "Refresh", Width = 110, Height = 38, HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => await LoadAsync();
        Grid.SetRow(refresh, 2);
        root.Children.Add(refresh);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var questions = await _questions.GetQuestionsAsync();
            var projects = await _projects.GetAsync();
            var jobs = await _publishing.GetJobsAsync();
            _content.Children.Clear();
            AddCard("Questions", questions.Count.ToString(), $"{questions.Count(q => q.IsEnabled)} enabled");
            AddCard("Quiz projects", projects.Count.ToString(), $"{projects.Count(p => p.IsEnabled)} enabled");
            AddCard("Publishing queue", jobs.Count(j => string.Equals(j.Status, "Queued", StringComparison.OrdinalIgnoreCase)).ToString(), "ready for publishing");
            AddCard("Published", jobs.Count(j => string.Equals(j.Status, "Published", StringComparison.OrdinalIgnoreCase)).ToString(), "completed jobs");
            AddCard("Needs attention", jobs.Count(j => string.Equals(j.Status, "Failed", StringComparison.OrdinalIgnoreCase)).ToString(), "failed jobs");
            var latest = jobs.FirstOrDefault();
            _content.Children.Add(new TextBlock { Text = latest is null ? "No publishing activity yet." : $"Latest publishing activity: {latest.Status} — {latest.Title}", Margin = new Thickness(0, 22, 0, 0), Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        }
        catch (Exception ex)
        {
            _content.Children.Clear();
            _content.Children.Add(new TextBlock { Text = $"Dashboard could not load.\n\n{ex.Message}", Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap });
        }
    }

    private void AddCard(string title, string value, string detail)
    {
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(38, 52, 73)), CornerRadius = new CornerRadius(10), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 10) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = title, Width = 230, FontSize = 17, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = value, Width = 100, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = detail, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        border.Child = row;
        _content.Children.Add(border);
    }
}
