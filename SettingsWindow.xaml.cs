using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace UsageBar;

/// <summary>UsageBar's settings, as Mindows' and CodexBar's providers pane: a sidebar category per provider. Changes apply and save at once.</summary>
public partial class SettingsWindow : Window
{
    record Page(Provider Provider, string Name, Brush Color, Geometry Logo, bool Shown, bool CanHide, string ShownNote,
        string Status, string Plan, string Updated, bool Installed, string LoginLabel, string LoginNote);

    static SettingsWindow? open;

    readonly App app;

    public static void Open(App app)
    {
        open ??= new SettingsWindow(app);
        open.Show();
        if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
        open.Activate();
    }

    SettingsWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        Render();
        app.Refreshed += Render;
        Closed += (_, _) =>
        {
            app.Refreshed -= Render;
            open = null;
        };
        SourceInitialized += (_, _) =>
        {
            var dark = App.LightTheme ? 0 : 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
    }

    void Render()
    {
        var selected = Pages.SelectedIndex;
        Pages.ItemsSource = app.AllProviders.Select(p => new Page(p, p.Name,
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Color)), Geometry.Parse(p.Logo),
            app.IsShown(p), !app.IsLastShown(p),
            KeepWords(app.IsLastShown(p) ? "메뉴 바에 마지막으로 남은 항목은 숨길 수 없습니다." : !p.Available && app.IsShown(p) ? "로그인하면 표시됩니다." : ""),
            KeepWords(!p.Available ? (p.Installed ? "로그인되어 있지 않습니다." : $"{p.Id} 명령을 찾을 수 없습니다.")
                : app.Refreshing ? "확인 중..." : p.Error ?? (p.Usage != null ? "로그인됨" : "아직 확인하지 않음")),
            p.Usage?.Plan ?? "–",
            app.Refreshing ? "업데이트 중..." : p.UpdatedAt is { } at ? PopupWindow.Ago(at) : "–",
            p.Installed, p.Available ? "다시 로그인" : "로그인",
            p.Installed ? $"터미널에서 {p.LoginCommand}을 실행합니다." : $"{p.Id} 명령을 설치한 뒤 로그인할 수 있습니다.")).ToList();
        Pages.SelectedIndex = Math.Max(selected, 0);
    }

    /// <summary>Wraps only at spaces: WPF breaks Korean between any two syllables, so word joiners hold each word together.</summary>
    static string KeepWords(string text) => Regex.Replace(text, @"(?<=\S)(?=\S)", "⁠");

    void Shown_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        app.SetShown(((Page)box.DataContext).Provider, box.IsChecked == true);
        Render();
    }

    void Login_Click(object sender, RoutedEventArgs e)
    {
        if (((Page)((FrameworkElement)sender).DataContext).Provider.Login() is not { } start) return;
        var login = new Process { StartInfo = start, EnableRaisingEvents = true };
        // Signed in or not, check again: a new sign-in shows the provider in the menu bar.
        login.Exited += (_, _) =>
        {
            login.Dispose();
            app.Dispatcher.InvokeAsync(app.Refresh);
        };
        login.Start();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
