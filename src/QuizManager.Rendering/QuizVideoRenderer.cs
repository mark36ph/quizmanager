using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuizManager.Core.Models;

namespace QuizManager.Rendering;

public sealed record QuizRenderResult(string VideoPath, string ThumbnailPath, int QuestionCount, TimeSpan Duration);

public sealed class QuizVideoRenderer
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int QuestionSeconds = 6;
    private const int RevealSeconds = 4;
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
        progress?.Invoke("Preparing quiz scenes…");
        for (var i = 0; i < questions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = i;
            var questionFrame = Path.Combine(frames, $"frame-{index:0000}-question.png");
            var revealFrame = Path.Combine(frames, $"frame-{index:0000}-reveal.png");
            await _sta.RunAsync(() => RenderFrame(questions[index], questionFrame, false));
            await _sta.RunAsync(() => RenderFrame(questions[index], revealFrame, true));
            progress?.Invoke($"Prepared question {i + 1} of {questions.Count}");
        }
        var thumbnail = Path.Combine(outputDirectory, "thumbnail.png");
        File.Copy(Path.Combine(frames, "frame-0000-question.png"), thumbnail, true);
        var video = Path.Combine(outputDirectory, "FactburstQuiz_Final.mp4");
        var concat = Path.Combine(outputDirectory, "frames.txt");
        await File.WriteAllLinesAsync(concat, BuildConcatList(frames, questions.Count), cancellationToken);
        progress?.Invoke("Encoding MP4 with FFmpeg…");
        await RunFfmpegAsync(ffmpegExecutable, concat, video, cancellationToken);
        progress?.Invoke("Render complete");
        return new QuizRenderResult(video, thumbnail, questions.Count, TimeSpan.FromSeconds(questions.Count * (QuestionSeconds + RevealSeconds)));
    }

    private static IEnumerable<string> BuildConcatList(string frames, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var question = Path.Combine(frames, $"frame-{i:0000}-question.png").Replace("'", "'\\''");
            var reveal = Path.Combine(frames, $"frame-{i:0000}-reveal.png").Replace("'", "'\\''");
            yield return $"file '{question}'";
            yield return $"duration {QuestionSeconds}";
            yield return $"file '{reveal}'";
            yield return $"duration {RevealSeconds}";
        }
        var last = Path.Combine(frames, $"frame-{count - 1:0000}-reveal.png").Replace("'", "'\\''");
        yield return $"file '{last}'";
    }

    private static async Task RunFfmpegAsync(string executable, string concat, string output, CancellationToken token)
    {
        var psi = new ProcessStartInfo { FileName = executable, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-y", "-f", "concat", "-safe", "0", "-i", concat, "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2", "-r", "30", "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output }) psi.ArgumentList.Add(arg);
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

    private static void RenderFrame(QuizQuestion question, string destination, bool reveal)
    {
        var root = new Grid { Width = Width, Height = Height, Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)) };
        root.Children.Add(new EllipseShape(560, Color.FromArgb(42, 99, 102, 241), HorizontalAlignment.Right, VerticalAlignment.Top));
        root.Children.Add(new EllipseShape(340, Color.FromArgb(32, 20, 184, 166), HorizontalAlignment.Left, VerticalAlignment.Bottom));
        var panel = new Border { Margin = new Thickness(72), Padding = new Thickness(48), CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)) };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(new TextBlock { Text = "FACTBURST QUIZ", FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        var meta = new TextBlock { Text = BuildMeta(question, reveal), FontSize = 18, Foreground = Brushes.Gainsboro, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(meta, Dock.Right);
        header.Children.Add(meta);
        stack.Children.Add(header);
        if (TryCreateImage(question.ImagePath, out var image))
        {
            image.Height = 150; image.MaxWidth = 300; image.Stretch = Stretch.Uniform; image.HorizontalAlignment = HorizontalAlignment.Left; image.Margin = new Thickness(0, 0, 0, 18); stack.Children.Add(image);
        }
        stack.Children.Add(new TextBlock { Text = question.Question, FontSize = 39, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 1050, Margin = new Thickness(0, 0, 0, 24) });
        for (var i = 0; i < question.Answers.Count; i++)
        {
            var correct = reveal && i == question.CorrectAnswerIndex;
            stack.Children.Add(new Border { Background = correct ? new SolidColorBrush(Color.FromRgb(22, 101, 52)) : new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 3, 0, 3), Child = new TextBlock { Text = $"{(char)('A' + i)}.  {question.Answers[i]}{(correct ? "   ✓ CORRECT" : "")}", FontSize = 23, FontWeight = correct ? FontWeights.Bold : FontWeights.Normal, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap } });
        }
        if (reveal && !string.IsNullOrWhiteSpace(question.Explanation))
            stack.Children.Add(new TextBlock { Text = "Why: " + question.Explanation, FontSize = 18, Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, MaxWidth = 1050, Margin = new Thickness(0, 16, 0, 0) });
        panel.Child = stack;
        root.Children.Add(panel);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(destination); encoder.Save(stream);
    }

    private static string BuildMeta(QuizQuestion question, bool reveal)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(question.Category)) parts.Add(question.Category);
        if (!string.IsNullOrWhiteSpace(question.Difficulty)) parts.Add(question.Difficulty);
        if (reveal) parts.Add("ANSWER REVEAL");
        return string.Join("  •  ", parts);
    }

    private static bool TryCreateImage(string? path, out Image image)
    {
        image = new Image();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.UriSource = new Uri(Path.GetFullPath(path)); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
            image.Source = bitmap; return true;
        }
        catch { return false; }
    }

    private sealed class EllipseShape : FrameworkElement
    {
        private readonly Brush _fill;
        public EllipseShape(double size, Brush fill, HorizontalAlignment horizontal, VerticalAlignment vertical)
        {
            Width = size; Height = size; _fill = fill; HorizontalAlignment = horizontal; VerticalAlignment = vertical;
        }
        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawEllipse(_fill, null, new Point(Width / 2, Height / 2), Width / 2, Height / 2);
        }
    }
}
