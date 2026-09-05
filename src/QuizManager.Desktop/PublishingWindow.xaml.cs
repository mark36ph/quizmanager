using Microsoft.Win32;
using QuizManager.Infrastructure.Data;
using System.Diagnostics;
using System.IO;

namespace QuizManager.Desktop;

public partial class PublishingWindow : System.Windows.Window
{
    private readonly PublishingService _publishing;
    private IReadOnlyList<QuizManager.Core.Models.PublishJob> _jobs = Array.Empty<QuizManager.Core.Models.PublishJob>();

    public PublishingWindow(PublishingService publishing)
    {
        _publishing = publishing;
        InitializeComponent();
        Loaded += async (_, _) => await LoadJobsAsync();
    }

    private void BrowseVideo_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select rendered quiz video", Filter = "MP4 video (*.mp4)|*.mp4|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            VideoPathText.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(TitleText.Text)) TitleText.Text = Path.GetFileNameWithoutExtension(dialog.FileName).Replace('_', ' ');
        }
    }

    private void BrowseThumbnail_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select thumbnail", Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ThumbnailPathText.Text = dialog.FileName;
    }

    private async void Queue_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var id = await _publishing.QueueAsync(VideoPathText.Text, TitleText.Text, DescriptionText.Text, ThumbnailPathText.Text);
            await LoadJobsAsync();
            System.Windows.MessageBox.Show($"Publishing job #{id} queued locally. No online upload has been claimed.", "Queued", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"The publishing job could not be queued.\n\n{ex.Message}", "Publishing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async void Retry_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not QuizManager.Core.Models.PublishJob job || !string.Equals(job.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(this, "Select a failed publishing job first.", "Publishing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        try
        {
            await _publishing.MarkQueuedAsync(job.Id);
            await LoadJobsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"The job could not be re-queued.\n\n{ex.Message}", "Publishing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void Filter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ApplyFilter();

    private void OpenFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!File.Exists(VideoPathText.Text)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{VideoPathText.Text}\"") { UseShellExecute = true });
    }

    private async Task LoadJobsAsync()
    {
        _jobs = await _publishing.GetJobsAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = (StatusFilter?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "All";
        JobsGrid.ItemsSource = string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase)
            ? _jobs
            : _jobs.Where(job => string.Equals(job.Status, filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
