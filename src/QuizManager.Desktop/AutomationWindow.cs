using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuizManager.Desktop;

public sealed class AutomationWindow : Window
{
    private sealed class AutomationPlan
    {
        public bool Enabled { get; set; }
        public string Frequency { get; set; } = "Daily";
        public int Hour { get; set; } = 9;
        public int Minute { get; set; } = 0;
        public string TitleTemplate { get; set; } = "Daily Fact Quiz";
        public DateTime? LastRunUtc { get; set; }
    }

    private readonly string _settingsPath;
    private readonly CheckBox _enabled = new() { Content = "Enable automation", Margin = new Thickness(0, 0, 0, 14) };
    private readonly ComboBox _frequency = new() { Width = 180, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox _hour = new() { Width = 70, Margin = new Thickness(8, 0, 0, 12) };
    private readonly TextBox _minute = new() { Width = 70, Margin = new Thickness(8, 0, 0, 12) };
    private readonly TextBox _title = new() { Width = 360, Margin = new Thickness(0, 0, 0, 18) };
    private readonly TextBlock _nextRun = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

    public AutomationWindow()
    {
        Title = "Factburst Quiz Manager — Automation";
        Width = 620;
        Height = 470;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 31, 43));
        _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactburstQuizManager", "data", "automation.json");
        _frequency.ItemsSource = new[] { "Daily", "Weekly" };
        _frequency.SelectedIndex = 0;
        _hour.Text = "9";
        _minute.Text = "0";
        _title.Text = "Daily Fact Quiz";

        var root = new StackPanel { Margin = new Thickness(28) };
        root.Children.Add(new TextBlock { Text = "Automation", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = "Configure a durable local production schedule. The plan is saved independently of the installed application files.", Margin = new Thickness(0, 7, 0, 22), Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_enabled);
        root.Children.Add(new TextBlock { Text = "Frequency", Foreground = Brushes.White });
        root.Children.Add(_frequency);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(new TextBlock { Text = "Time (24h)", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });
        timeRow.Children.Add(_hour);
        timeRow.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });
        timeRow.Children.Add(_minute);
        root.Children.Add(timeRow);
        root.Children.Add(new TextBlock { Text = "Title template", Foreground = Brushes.White });
        root.Children.Add(_title);
        var save = new Button { Content = "Save Automation", Width = 170, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += Save_Click;
        root.Children.Add(save);
        root.Children.Add(_nextRun);
        Content = root;
        Loaded += (_, _) => LoadPlan();
    }

    private void LoadPlan()
    {
        try
        {
            if (!File.Exists(_settingsPath)) { UpdateNextRun(null); return; }
            var plan = JsonSerializer.Deserialize<AutomationPlan>(File.ReadAllText(_settingsPath));
            if (plan is null) return;
            _enabled.IsChecked = plan.Enabled;
            _frequency.SelectedItem = plan.Frequency;
            _hour.Text = plan.Hour.ToString();
            _minute.Text = plan.Minute.ToString();
            _title.Text = plan.TitleTemplate;
            UpdateNextRun(plan);
        }
        catch (Exception ex)
        {
            _nextRun.Text = $"Saved automation could not be loaded: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_hour.Text, out var hour) || hour is < 0 or > 23 || !int.TryParse(_minute.Text, out var minute) || minute is < 0 or > 59)
        {
            MessageBox.Show(this, "Enter a valid 24-hour time between 00:00 and 23:59.", "Automation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_title.Text))
        {
            MessageBox.Show(this, "A title template is required.", "Automation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var plan = new AutomationPlan { Enabled = _enabled.IsChecked == true, Frequency = _frequency.SelectedItem?.ToString() ?? "Daily", Hour = hour, Minute = minute, TitleTemplate = _title.Text.Trim() };
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        UpdateNextRun(plan);
        MessageBox.Show(this, "Automation settings saved locally. The next production step will use this plan to drive quiz generation, rendering and publishing.", "Automation", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateNextRun(AutomationPlan? plan)
    {
        if (plan is null || !plan.Enabled) { _nextRun.Text = "Automation is disabled."; return; }
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, plan.Hour, plan.Minute, 0);
        if (next <= now) next = next.AddDays(1);
        if (string.Equals(plan.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase))
        {
            var days = ((int)DayOfWeek.Monday - (int)next.DayOfWeek + 7) % 7;
            next = next.AddDays(days == 0 && next <= now ? 7 : days);
        }
        _nextRun.Text = $"Next scheduled run: {next:g}\nLast completed run: {(plan.LastRunUtc.HasValue ? plan.LastRunUtc.Value.ToLocalTime().ToString("g") : "Never")}";
    }
}
