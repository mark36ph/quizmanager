using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QuizManager.Core.Models;

namespace QuizManager.Rendering;

public sealed record QuizRenderResult(string VideoPath, string ThumbnailPath, int QuestionCount, TimeSpan Duration);

public sealed class QuizVideoRenderer
{
    private readonly StaRenderWorker _sta = new();

    public async Task<QuizRenderResult> RenderAsync(IReadOnlyList<QuizQuestion> questions, string outputDirectory, string ffmpegExecutable, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (questions.Count == 0) throw new ArgumentException("At least one question is required.", nameof(questions));
        if (string.IsNullOrWhiteSpace(ffmpegExecutable)) throw new ArgumentException("FFmpeg executable is required.", nameof(ffmpegExecutable));
        if (!File.Exists(ffmpegExecutable) && !IsOnPath(ffmpegExecutable)) throw new FileNotFoundException("FFmpeg could not be found. Select ffmpeg.exe or add FFmpeg to PATH.", ffmpegExecutable);

        Directory.CreateDirectory(outputDirectory);
        var frames = Path.Combine(outputDirectory, "frames");
        Directory.CreateDirectory(frames);
        foreach (var old in Directory.EnumerateFiles(frames, "frame-*.png")) File.Delete(old);

        progress?.Invoke("Preparing video scenes…");
        for (var i = 0; i < questions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = i;
            await _sta.RunAsync(() => RenderFrame(questions[index], Path.Combine(frames, $"frame-{index:0000}.png")));
            progress?.Invoke($"Prepared scene {i + 1} of {questions.Count}");
        }

        var thumbnail = Path.Combine(outputDirectory, "thumbnail.png");
        File.Copy(Path.Combine(frames, "frame-0000.png"), thumbnail, true);
        var video = Path.Combine(outputDirectory, "FactburstQuiz_Final.mp4");
        var concat = Path.Combine(outputDirectory, "frames.txt");
        await File.WriteAllLinesAsync(concat, BuildConcatList(frames, questions.Count), cancellationToken);
        progress?.Invoke("Encoding MP4 with FFmpeg…");
        await RunFfmpegAsync(ffmpegExecutable, concat, video, cancellationToken);
        progress?.Invoke("Render complete");
        return new QuizRenderResult(video, thumbnail, questions.Count, TimeSpan.FromSeconds(questions.Count * 8));
    }

    private static IEnumerable<string> BuildConcatList(string frames, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"file '{Path.Combine(frames, $"frame-{i:0000}.png").Replace("'", "'\\''")}'";
            yield return "duration 8";
        }
        yield return $"file '{Path.Combine(frames, $"frame-{count - 1:0000}.png").Replace("'", "'\\''")}'";
    }

    private static async Task RunFfmpegAsync(string executable, string concat, string output, CancellationToken token)
    {
        var psi = new ProcessStartInfo { FileName = executable, Arguments = $"-y -f concat -safe 0 -i \"{concat}\" -vf \"scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2\" -r 30 -c:v libx264 -pix_fmt yuv420p -movflags +faststart \"{output}\"", RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg failed with exit code {process.ExitCode}. {string.Join(Environment.NewLine, error.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(8))}");
    }

    private static bool IsOnPath(string executable)
    {
        if (Path.IsPathRooted(executable)) return File.Exists(executable);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(folder => File.Exists(Path.Combine(folder, executable)) || File.Exists(Path.Combine(folder, executable + ".exe")));
    }

    private static void RenderFrame(QuizQuestion question, string destination)
    {
        const int width = 1280, height = 720;
        var root = new Grid { Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)) };
        root.Children.Add(new Ellipse { Width = 520, Height = 520, Fill = new SolidColorBrush(Color.FromArgb(45, 99, 102, 241)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
        var panel = new Border { Margin = new Thickness(90), Padding = new Thickness(55), CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)) };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "FACTBURST QUIZ", FontSize = 25, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 24) });
        stack.Children.Add(new TextBlock { Text = question.Question, FontSize = 42, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 1020, Margin = new Thickness(0, 0, 0, 30) });
        for (var i = 0; i < question.Answers.Count; i++) stack.Children.Add(new TextBlock { Text = $"{(char)('A' + i)}.  {question.Answers[i]}", FontSize = 27, Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 5, 0, 5), TextWrapping = TextWrapping.Wrap });
        panel.Child = stack; root.Children.Add(panel); root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(destination); encoder.Save(stream);
    }
}
