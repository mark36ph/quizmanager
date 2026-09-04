using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace QuizManager.Desktop;

public sealed class AppUpdateService
{
    private const string RepositoryUrl = "https://github.com/mark36ph/quizmanager";
    public const string StableSetupDownloadUrl = RepositoryUrl + "/releases/latest/download/QuizManager-win-x64-stable-Setup.exe";

    private readonly UpdateManager _manager = new(
        new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "development";

    public Task<UpdateInfo?> CheckAsync()
    {
        if (!IsInstalled)
            return Task.FromResult<UpdateInfo?>(null);
        return _manager.CheckForUpdatesAsync();
    }

    public async Task InstallAsync(UpdateInfo update, Action<int>? progress = null)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Updates can only be installed from an installed Quiz Manager build.");

        await _manager.DownloadUpdatesAsync(update, progress);
        _manager.ApplyUpdatesAndRestart(update);
    }

    public async Task<string> BootstrapInstallAsync(Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
            throw new InvalidOperationException("Quiz Manager is already installed. Use Check for Updates instead.");

        var installerPath = Path.Combine(Path.GetTempPath(), $"QuizManager-Setup-{Guid.NewGuid():N}.exe");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuizManager-Updater/1.0");

        using var response = await client.GetAsync(StableSetupDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (total is > 0)
                progress?.Invoke((int)Math.Clamp(downloaded * 100 / total.Value, 0, 100));
        }
        await output.FlushAsync(cancellationToken);

        if (new FileInfo(installerPath).Length < 1024 * 1024)
        {
            File.Delete(installerPath);
            throw new InvalidOperationException("The downloaded installer was incomplete. Try again in a moment.");
        }

        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        return installerPath;
    }
}
