using QuizManager.Infrastructure.Data;
using Velopack;

namespace QuizManager.Desktop;

public partial class MainWindow : System.Windows.Window
{
    private readonly QuestionLibraryService _questionLibrary;
    private readonly AppUpdateService _updates;

    public MainWindow(QuestionLibraryService questionLibrary, AppUpdateService updates)
    {
        _questionLibrary = questionLibrary;
        _updates = updates;
        InitializeComponent();
        VersionText.Text = $"v{_updates.CurrentVersion}";
    }

    private void QuestionLibrary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var window = new QuestionLibraryWindow(_questionLibrary)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void Update_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        var originalText = UpdateButton.Content;
        try
        {
            if (!_updates.IsInstalled)
            {
                var result = System.Windows.MessageBox.Show(
                    "This is a development/portable build. Would you like to download the latest installable GitHub release?",
                    "Install Factburst Quiz Manager",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information);
                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    UpdateButton.Content = "Downloading…";
                    await _updates.BootstrapInstallAsync(percent =>
                    {
                        Dispatcher.Invoke(() => UpdateButton.Content = $"Downloading {percent}%");
                    });
                    System.Windows.MessageBox.Show(
                        "The installer has been launched. Finish the installation and then start Factburst Quiz Manager from the installed shortcut.",
                        "Installer Started",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                return;
            }

            UpdateButton.Content = "Checking…";
            var update = await _updates.CheckAsync();
            if (update is null)
            {
                System.Windows.MessageBox.Show(
                    $"Factburst Quiz Manager {_updates.CurrentVersion} is up to date.",
                    "No Update Available",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var resultUpdate = System.Windows.MessageBox.Show(
                $"Version {update.TargetFullRelease.Version} is available. Install it now?\n\nYour user data is kept outside the installed application.",
                "Update Available",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);
            if (resultUpdate != System.Windows.MessageBoxResult.Yes)
                return;

            UpdateButton.Content = "Downloading…";
            await _updates.InstallAsync(update, percent =>
            {
                Dispatcher.Invoke(() => UpdateButton.Content = $"Downloading {percent}%");
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"The update could not be completed.\n\n{ex.Message}",
                "Update Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            UpdateButton.Content = originalText;
            UpdateButton.IsEnabled = true;
        }
    }
}
