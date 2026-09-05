using Microsoft.Win32;
using System.Windows.Threading;
using QuizManager.Infrastructure.Data;
using Velopack;

namespace QuizManager.Desktop;

public partial class MainWindow : System.Windows.Window
{
    private readonly QuestionLibraryService _questionLibrary;
    private readonly QuizProjectService _quizProjects;
    private readonly PublishingService _publishing;
    private readonly AppUpdateService _updates;
    private readonly DispatcherTimer _updateTimer;
    private bool _updateCheckInProgress;
    private string? _lastAlertedVersion;

    public MainWindow(QuestionLibraryService questionLibrary, QuizProjectService quizProjects, PublishingService publishing, AppUpdateService updates)
    {
        _questionLibrary = questionLibrary; _quizProjects = quizProjects; _publishing = publishing; _updates = updates; InitializeComponent();
        VersionText.Text = $"v{_updates.CurrentVersion}";
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) }; _updateTimer.Tick += UpdateTimer_Tick; _updateTimer.Start(); Loaded += MainWindow_Loaded; Closed += (_, _) => _updateTimer.Stop();
    }
    private async void MainWindow_Loaded(object sender, System.Windows.RoutedEventArgs e) { Loaded -= MainWindow_Loaded; await CheckForUpdateSilentlyAsync(); }
    private async void UpdateTimer_Tick(object? sender, EventArgs e) => await CheckForUpdateSilentlyAsync();
    private async Task CheckForUpdateSilentlyAsync()
    {
        if (!_updates.IsInstalled || _updateCheckInProgress) return; _updateCheckInProgress = true;
        try { var update = await _updates.CheckAsync(); if (update is null) return; var availableVersion = update.TargetFullRelease.Version.ToString(); if (string.Equals(_lastAlertedVersion, availableVersion, StringComparison.OrdinalIgnoreCase)) return; _lastAlertedVersion = availableVersion; if (System.Windows.MessageBox.Show($"Factburst Quiz Manager {availableVersion} is available.\n\nWould you like to install the update now?\n\nYour user data is kept outside the installed application.", "Update Available", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.Yes) await InstallUpdateAsync(update); } catch { } finally { _updateCheckInProgress = false; }
    }
    private void QuestionLibrary_Click(object sender, System.Windows.RoutedEventArgs e) => new QuestionLibraryWindow(_questionLibrary) { Owner = this }.ShowDialog();
    private void QuizProjects_Click(object sender, System.Windows.RoutedEventArgs e) => new QuizProjectsWindow(_quizProjects, _questionLibrary) { Owner = this }.ShowDialog();
    private void Rendering_Click(object sender, System.Windows.RoutedEventArgs e) => new RenderingWindow(_quizProjects) { Owner = this }.ShowDialog();
    private void Publishing_Click(object sender, System.Windows.RoutedEventArgs e) => new PublishingWindow(_publishing) { Owner = this }.ShowDialog();

    private async void ImportLegacy_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import FactVault Manager database", Filter = "FactVault databases (*.db)|*.db|SQLite databases (*.db3;*.sqlite;*.sqlite3)|*.db3;*.sqlite;*.sqlite3|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        if (System.Windows.MessageBox.Show("Quiz Manager will create a backup before importing. Existing V2 data will not be deleted. Continue?", "Import existing database", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        try { System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait; var result = await new LegacyDatabaseImporter(((App)System.Windows.Application.Current).Database.DatabasePath).ImportAsync(dialog.FileName); System.Windows.MessageBox.Show($"Import complete.\n\nQuestions imported: {result.ImportedQuestions}\nDuplicates skipped: {result.SkippedDuplicateQuestions}\nLegacy tables preserved: {result.LegacyTablesPreserved}", "Database imported", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information); } catch (Exception ex) { System.Windows.MessageBox.Show($"The database could not be imported.\n\n{ex.Message}", "Import failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); } finally { System.Windows.Input.Mouse.OverrideCursor = null; }
    }

    private async void Update_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false; var originalText = UpdateButton.Content;
        try { if (!_updates.IsInstalled) { if (System.Windows.MessageBox.Show("This is a development/portable build. Download the latest installable GitHub release?", "Install Factburst Quiz Manager", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.Yes) { UpdateButton.Content = "Downloading…"; await _updates.BootstrapInstallAsync(percent => Dispatcher.Invoke(() => UpdateButton.Content = $"Downloading {percent}%")); System.Windows.MessageBox.Show("The installer has been launched. Finish installation and then start Factburst Quiz Manager from the installed shortcut.", "Installer Started", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information); } return; } UpdateButton.Content = "Checking…"; var update = await _updates.CheckAsync(); if (update is null) { System.Windows.MessageBox.Show($"Factburst Quiz Manager {_updates.CurrentVersion} is up to date.", "No Update Available", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information); return; } if (System.Windows.MessageBox.Show($"Version {update.TargetFullRelease.Version} is available. Install it now?\n\nYour user data is kept outside the installed application.", "Update Available", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.Yes) await InstallUpdateAsync(update); } catch (Exception ex) { System.Windows.MessageBox.Show($"The update could not be completed.\n\n{ex.Message}", "Update Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); } finally { UpdateButton.Content = originalText; UpdateButton.IsEnabled = true; }
    }
    private async Task InstallUpdateAsync(UpdateInfo update) { UpdateButton.IsEnabled = false; UpdateButton.Content = "Downloading…"; await _updates.InstallAsync(update, percent => Dispatcher.Invoke(() => UpdateButton.Content = $"Downloading {percent}%")); }
}
