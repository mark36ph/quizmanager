using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using QuizManager.Core.Models;
using QuizManager.Infrastructure.Data;
using QuizManager.Rendering;

namespace QuizManager.Desktop;

public partial class RenderingWindow : System.Windows.Window
{
    private readonly QuizProjectService _projects;
    private readonly List<QuizProject> _items = [];
    private CancellationTokenSource? _renderCancellation;

    public RenderingWindow(QuizProjectService projects)
    {
        _projects = projects;
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _items.Clear();
            _items.AddRange(await _projects.GetAsync());
            ProjectBox.ItemsSource = _items;
            if (_items.Count > 0) ProjectBox.SelectedIndex = 0;
            else ProjectInfoText.Text = "No quiz projects exist yet. Create a project and generate its question set first.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not load projects: {ex.Message}"; }
    }

    private async void Project_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not QuizProject project) return;
        try
        {
            var questions = await _projects.GetGeneratedAsync(project.Id);
            ProjectInfoText.Text = questions.Count == 0
                ? $"{project.Name}: no generated quiz yet. Open Quiz Projects and click Generate Quiz first."
                : $"{project.Name}: {questions.Count} questions ready. The first renderer uses an 8-second scene per question and creates a thumbnail from the opening scene.";
        }
        catch (Exception ex) { ProjectInfoText.Text = ex.Message; }
    }

    private void Browse_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select FFmpeg executable", Filter = "FFmpeg executable (ffmpeg.exe)|ffmpeg.exe|Executable (*.exe)|*.exe|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) FfmpegBox.Text = dialog.FileName;
    }

    private async void Render_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not QuizProject project)
        {
            System.Windows.MessageBox.Show("Select a project first.", "Render", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        RenderButton.IsEnabled = false;
        _renderCancellation = new CancellationTokenSource();
        try
        {
            var questions = await _projects.GetGeneratedAsync(project.Id);
            if (questions.Count == 0) throw new InvalidOperationException("This project has no generated questions. Open Quiz Projects and generate the quiz first.");
            var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactburstQuizManager", "renders", MakeSafeName(project.Name));
            var renderer = new QuizVideoRenderer();
            Progress.Value = 0;
            var result = await renderer.RenderAsync(questions, output, FfmpegBox.Text.Trim(), message => Dispatcher.Invoke(() => StatusText.Text = message), _renderCancellation.Token);
            Progress.Value = 100;
            StatusText.Text = $"Finished: {result.VideoPath}";
            if (System.Windows.MessageBox.Show($"Render complete.\n\nVideo: {result.VideoPath}\nThumbnail: {result.ThumbnailPath}\n\nOpen the render folder?", "Render complete", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{output}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException) { StatusText.Text = "Render cancelled."; }
        catch (Exception ex) { StatusText.Text = "Render failed."; System.Windows.MessageBox.Show(ex.Message, "Render failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); }
        finally { _renderCancellation.Dispose(); _renderCancellation = null; RenderButton.IsEnabled = true; }
    }

    private static string MakeSafeName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "project" : value.Trim();
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}
