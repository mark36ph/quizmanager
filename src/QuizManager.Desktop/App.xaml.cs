using System.IO;
using QuizManager.Infrastructure.Data;

namespace QuizManager.Desktop;

public partial class App : System.Windows.Application
{
    public QuizDatabase Database { get; private set; } = null!;
    public QuestionLibraryService QuestionLibrary { get; private set; } = null!;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FactburstQuizManager");
        var databasePath = Path.Combine(dataRoot, "data", "quizmanager.db");

        Database = new QuizDatabase(databasePath);
        await Database.InitializeAsync();
        QuestionLibrary = new QuestionLibraryService(Database);

        MainWindow = new MainWindow(QuestionLibrary);
        MainWindow.Show();
    }
}
