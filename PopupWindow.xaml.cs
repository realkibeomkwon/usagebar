using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace UsageBar;

/// <summary>One provider's usage, as CodexBar's menu: each window with what is left and when it resets.</summary>
public partial class PopupWindow : Window
{
    record Row(string Title, string Reset, GridLength Left, GridLength Used, Brush Fill);

    readonly App app;
    readonly Provider provider;
    public Provider Provider => provider;

    static PopupWindow()
    {
        var now = DateTimeOffset.Now;
        Debug.Assert(Until(now.AddMinutes(218.5), now) == "3시간 38분 후 초기화" && Until(now.AddDays(2.5), now) == "2일 12시간 후 초기화"
            && Until(now.AddSeconds(20), now) == "1분 후 초기화" && Until(now.AddMinutes(-1), now) == "곧 초기화",
            "reset times must read like the menu bar's");
    }

    public PopupWindow(App app, Provider provider)
    {
        (this.app, this.provider) = (app, provider);
        InitializeComponent();
        app.Refreshed += Render;
        Closed += (_, _) => app.Refreshed -= Render;
        Render();
    }

    /// <summary>Shows the popup with its top left at a point in screen pixels or, from the tray, with its bottom right there.</summary>
    public void ShowAt(int x, int y, bool above)
    {
        Show(); // off screen, to measure it
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = SystemParameters.WorkArea;
        double left = x / scale, top = y / scale;
        if (above) (left, top) = (left - ActualWidth, top - ActualHeight);
        else left -= 8; // the shadow margin, so the card lines up with the menu bar item
        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        Activate();
    }

    void Render()
    {
        ProviderName.Text = provider.Name;
        Plan.Text = provider.Usage?.Plan ?? "";
        Updated.Text = app.Refreshing ? "업데이트 중..." : provider.UpdatedAt is { } at ? Ago(at) : "아직 업데이트되지 않음";
        var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(provider.Color));
        Windows.ItemsSource = provider.Usage?.Windows.Select(w =>
        {
            var left = Math.Clamp(100 - w.UsedPercent, 0, 100);
            return new Row($"{w.Label} {left:0}% 남음", w.ResetsAt is { } reset ? Until(reset, DateTimeOffset.Now) : "",
                new GridLength(left, GridUnitType.Star), new GridLength(100 - left, GridUnitType.Star), fill);
        }).ToList();
        Error.Text = provider.Error;
        Error.Visibility = provider.Error == null ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = !app.Refreshing;
    }

    static string Ago(DateTimeOffset at)
    {
        var minutes = (int)(DateTimeOffset.Now - at).TotalMinutes;
        return minutes < 1 ? "방금 업데이트됨" : minutes < 60 ? $"{minutes}분 전 업데이트" : $"{minutes / 60}시간 전 업데이트";
    }

    static string Until(DateTimeOffset at, DateTimeOffset now)
    {
        var t = at - now;
        if (t <= TimeSpan.Zero) return "곧 초기화";
        var span = t.TotalDays >= 1 ? $"{(int)t.TotalDays}일 {t.Hours}시간"
            : t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분"
            : $"{Math.Max(1, t.Minutes)}분";
        return span + " 후 초기화";
    }

    // Closing deactivates the window too, and closing it again then throws.
    void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!closing) Close();
    }

    bool closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        closing = true;
        base.OnClosing(e);
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await app.Refresh();

    void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(provider.Dashboard) { UseShellExecute = true });
        Close();
    }

    void Quit_Click(object sender, RoutedEventArgs e) => app.Shutdown();
}
