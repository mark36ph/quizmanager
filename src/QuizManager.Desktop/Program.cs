using Velopack;

namespace QuizManager.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run before WPF initializes so install/update hooks
        // can complete without starting the full application UI.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
