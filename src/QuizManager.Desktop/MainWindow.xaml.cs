using QuizManager.Infrastructure.Data;

namespace QuizManager.Desktop;

public partial class MainWindow : System.Windows.Window
{
    private readonly QuestionLibraryService _questionLibrary;

    public MainWindow(QuestionLibraryService questionLibrary)
    {
        _questionLibrary = questionLibrary;
        InitializeComponent();
    }

    private void QuestionLibrary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var window = new QuestionLibraryWindow(_questionLibrary)
        {
            Owner = this
        };
        window.ShowDialog();
    }
}
