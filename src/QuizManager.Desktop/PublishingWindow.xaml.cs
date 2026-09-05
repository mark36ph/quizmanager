using Microsoft.Win32;
using QuizManager.Core.Models;
using QuizManager.Infrastructure.Data;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;

namespace QuizManager.Desktop;

public partial class PublishingWindow : System.Windows.Window
{
    private readonly PublishingService _publishing;
    private readonly YouTubePublisher _youtube = new();
    private IReadOnlyList<PublishJob> _jobs = Array.Empty<PublishJob>();

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
        if (JobsGrid.SelectedItem is not PublishJob job || !string.Equals(job.Status, "Failed", StringComparison.OrdinalIgnoreCase))
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

    private async void UploadSelected_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not PublishJob job || !string.Equals(job.Status, "Queued", StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(this, "Select a queued publishing job first.", "YouTube", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var tokenBox = new PasswordBox { Width = 360, Margin = new System.Windows.Thickness(0, 8, 0, 12) };
        var panel = new StackPanel { Margin = new System.Windows.Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "YouTube access token", FontWeight = System.Windows.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Paste a valid OAuth access token for this upload. It is used only in memory and is not saved by Quiz Manager.", Margin = new System.Windows.Thickness(0, 6, 0, 4), TextWrapping = System.Windows.TextWrapping.Wrap });
        panel.Children.Add(tokenBox);
        var dialog = new System.Windows.Window { Title = "YouTube authorization", Content = panel, Width = 430, Height = 210, Owner = this, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 31, 43)) };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 34, Margin = new System.Windows.Thickness(0, 0, 8, 0) };
        var upload = new Button { Content = "Upload", Width = 90, Height = 34 };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        upload.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(upload); panel.Children.Add(buttons);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(tokenBox.Password)) return;

        try
        {
            UploadSelectedButton.IsEnabled = false;
            UploadSelectedButton.Content = "Uploading…";
            var result = await _youtube.UploadAsync(tokenBox.Password, job, "private");
            if (!string.IsNullOrWhiteSpace(job.ThumbnailPath)) await _youtube.SetThumbnailAsync(tokenBox.Password, result.VideoId, job.ThumbnailPath);
            await _publishing.MarkPublishedAsync(job.Id, result.VideoId, result.Url);
            await LoadJobsAsync();
            System.Windows.MessageBox.Show(this, $"YouTube upload completed.\n\n{result.Url}", "Published", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            try { await _publishing.MarkFailedAsync(job.Id, ex.Message); await LoadJobsAsync(); } catch { }
            System.Windows.MessageBox.Show(this, $"The YouTube upload failed.\n\n{ex.Message}", "YouTube", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            UploadSelectedButton.IsEnabled = true;
            UploadSelectedButton.Content = "Upload Selected";
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

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
        var filter = (StatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        JobsGrid.ItemsSource = string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase)
            ? _jobs
            : _jobs.Where(job => string.Equals(job.Status, filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
