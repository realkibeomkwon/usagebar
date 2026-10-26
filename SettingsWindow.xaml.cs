using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace UsageBar;

/// <summary>UsageBar's settings, as Mindows' and CodexBar's providers pane: 일반, then a sidebar category per provider. Changes apply and save at once.</summary>
public partial class SettingsWindow : Window
{
    record Page(Provider Provider, string Name, Brush Color, Geometry Logo, bool Shown, bool CanHide, string ShownNote,
        string Status, string Plan, string Updated, bool Installed, string LoginLabel, string LoginNote);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string TaskName = "UsageBar";

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
        foreach (var _ in app.AllProviders)
            Pages.Items.Add(new TabItem { HeaderTemplate = (DataTemplate)Resources["ProviderHeader"], ContentTemplate = (DataTemplate)Resources["ProviderPage"] });
        // The task itself is the setting, as in Mindows, so nothing keeps a copy that could drift from it.
        LaunchAtLogin.IsChecked = LogonTaskExists();
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
        var pages = app.AllProviders.Select(p => new Page(p, p.Name,
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Color)), Geometry.Parse(p.Logo),
            app.IsShown(p), !app.IsLastShown(p),
            KeepWords(app.IsLastShown(p) ? "메뉴 바에 마지막으로 남은 항목은 숨길 수 없습니다." : !p.Available && app.IsShown(p) ? "로그인하면 표시됩니다." : ""),
            KeepWords(!p.Available ? (p.Installed ? "로그인되어 있지 않습니다." : $"{p.Id} 명령을 찾을 수 없습니다.")
                : app.Refreshing ? "확인 중..." : p.Error ?? (p.Usage != null ? "로그인됨" : "아직 확인하지 않음")),
            p.Usage?.Plan ?? "–",
            app.Refreshing ? "업데이트 중..." : p.UpdatedAt is { } at ? PopupWindow.Ago(at) : "–",
            p.Installed, p.Available ? "다시 로그인" : "로그인",
            p.Installed ? $"터미널에서 {p.LoginCommand}을 실행합니다." : $"{p.Id} 명령을 설치한 뒤 로그인할 수 있습니다.")).ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            var tab = (TabItem)Pages.Items[i + 1]; // after 일반
            tab.DataContext = tab.Header = tab.Content = pages[i];
        }
    }

    /// <summary>Wraps only at spaces: WPF breaks Korean between any two syllables, so word joiners hold each word together.</summary>
    static string KeepWords(string text) => Regex.Replace(text, @"(?<=\S)(?=\S)", "\u2060");

    void LaunchAtLogin_Click(object sender, RoutedEventArgs e)
    {
        // Earlier builds used the Run key, which Explorer starts a minute or more after sign-in.
        Registry.CurrentUser.OpenSubKey(RunKey, writable: true)?.DeleteValue("UsageBar", throwOnMissingValue: false);
        if (LaunchAtLogin.IsChecked == true) TaskFolder().RegisterTask(TaskName, LogonTaskXml(), 6 /* CREATE_OR_UPDATE */, null, null, 3 /* INTERACTIVE_TOKEN */);
        else if (LogonTaskExists()) TaskFolder().DeleteTask(TaskName, 0);
    }

    static dynamic TaskFolder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    static bool LogonTaskExists()
    {
        try { TaskFolder().GetTask(TaskName); return true; }
        catch (FileNotFoundException) { return false; }
    }

    /// <summary>Starts UsageBar as soon as the user signs in. The task's defaults would stop it after 3 days or on battery.</summary>
    static string LogonTaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions><Exec><Command>{SecurityElement.Escape(Environment.ProcessPath)}</Command></Exec></Actions>
            </Task>
            """;
    }

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

/// <summary>
/// A settings switch that raises Click when a screen reader toggles it, as a mouse or key press does; WPF's own
/// peer only flips IsChecked, so the change would show without being applied.
/// </summary>
public class Switch : CheckBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    class Peer(Switch owner) : CheckBoxAutomationPeer(owner), IToggleProvider
    {
        ToggleState IToggleProvider.ToggleState =>
            ((Switch)Owner).IsChecked switch { true => ToggleState.On, false => ToggleState.Off, null => ToggleState.Indeterminate };

        void IToggleProvider.Toggle()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            ((Switch)Owner).OnClick();
        }
    }
}
