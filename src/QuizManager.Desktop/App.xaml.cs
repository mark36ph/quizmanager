using System.IO;
using QuizManager.Infrastructure.Data;

namespace QuizManager.Desktop;

public partial class App : System.Windows.Application
{
    public QuizDatabase Database { get; private set; } = null!;
    public QuestionLibraryService QuestionLibrary { get; private set; } = null!;
    public QuizProjectService QuizProjects { get; private set; } = null!;
    public AppUpdateService Updates { get; } = new();

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            var dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FactburstQuizManager");
            var databasePath = Path.Combine(dataRoot, "data", "quizmanager.db");

            Database = new QuizDatabase(databasePath);
            await Database.InitializeAsync();
            QuestionLibrary = new QuestionLibraryService(Database);
            QuizProjects = new QuizProjectService(Database, QuestionLibrary);
            await QuizProjects.InitializeAsync();

            MainWindow = new MainWindow(QuestionLibrary, QuizProjects, Updates);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Factburst Quiz Manager could not start.\n\n{ex.Message}",
                "Factburst Quiz Manager",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
