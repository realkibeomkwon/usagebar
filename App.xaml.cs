using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace UsageBar;

public partial class App : Application
{
    public static readonly string Dir = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageBar")).FullName;
    /// <summary>The usage Mindows shows in its menu bar; see README for the format.</summary>
    static string StatusPath => Path.Combine(Dir, "status.json");
    /// <summary>The ids of providers left out of the menu bar and the tray, so a newly detected one shows.</summary>
    static string SettingsPath => Path.Combine(Dir, "settings.json");
    /// <summary>Posted by Mindows when its menu bar item is clicked: wParam is the provider's index, lParam the item's bottom left in screen pixels.</summary>
    static readonly uint ShowPopupMessage = RegisterWindowMessage("UsageBar.ShowPopup");
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    Mutex? instance;
    HwndSource? messages;
    readonly List<Provider> providers = UsageBar.Providers.All();
    readonly List<Forms.NotifyIcon> trayIcons = new();
    readonly HashSet<string> hidden = LoadHidden();
    /// <summary>The providers in the status file, in its order, which Mindows' item indexes refer to.</summary>
    List<Provider> published = new();
    PopupWindow? popup;
    (Provider? provider, long at) lastClosed;

    public static bool LightTheme { get; private set; }
    public bool Refreshing { get; private set; }
    /// <summary>Raised when a refresh starts and when it ends.</summary>
    public event Action? Refreshed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, "UsageBar.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, a) => Log(a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject);

        ApplyTheme();
        messages = new HwndSource(new HwndSourceParameters("UsageBar") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        messages.AddHook(WndProc);
        // Mindows leaves these out of its menu bar and draws its own items from the status file.
        foreach (var p in providers)
        {
            var icon = new Forms.NotifyIcon { Icon = Badge(p), Text = p.Name, Visible = TrayVisible(p) };
            icon.MouseUp += (_, _) => ShowPopup(p, Forms.Cursor.Position.X, Forms.Cursor.Position.Y, fromTray: true);
            trayIcons.Add(icon);
        }
        var timer = new DispatcherTimer { Interval = Interval };
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        // Started at sign-in, UsageBar can beat the network; don't leave a failed first refresh up until the next tick.
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, a) =>
        {
            if (a.IsAvailable) Dispatcher.InvokeAsync(Refresh);
        };
        _ = Refresh();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (messages != null) // not a second instance, which must leave the first one's file alone
        {
            foreach (var icon in trayIcons) icon.Dispose(); // else the icons linger until the pointer passes over them
            try { File.Delete(StatusPath); } // Mindows drops its items at once
            catch (IOException) { }
        }
        base.OnExit(e);
    }

    public async Task Refresh()
    {
        if (Refreshing) return;
        Refreshing = true;
        Refreshed?.Invoke();
        try { await Task.WhenAll(providers.Select(p => p.Refresh())); }
        finally { Refreshing = false; }
        for (var i = 0; i < providers.Count; i++)
        {
            var (p, icon) = (providers[i], trayIcons[i]);
            var old = icon.Icon;
            icon.Icon = Badge(p);
            old?.Dispose();
            icon.Visible = TrayVisible(p); // a provider signed in or out since
            var tip = p.Remaining is { } left ? $"{p.Name}: {left:0}% 남음" : $"{p.Name}: {p.Error ?? "사용량 없음"}";
            icon.Text = tip.Length > 127 ? tip[..127] : tip; // the most a tray tooltip holds
        }
        Publish();
        Refreshed?.Invoke();
    }

    public IReadOnlyList<Provider> AllProviders => providers;
    public bool IsShown(Provider p) => !hidden.Contains(p.Id);
    /// <summary>Whether hiding it would leave nothing in the menu bar to click, and so no way back to the popup.</summary>
    public bool IsLastShown(Provider p) => p.Available && IsShown(p) && providers.Count(q => q.Available && IsShown(q)) == 1;
    /// <summary>In the menu bar and the tray: signed in and shown, or every signed in one when all of those are hidden.</summary>
    bool Visible(Provider p) => p.Available && (IsShown(p) || !providers.Any(q => q.Available && IsShown(q)));

    /// <summary>The tray as <see cref="Visible"/> has it, plus the first provider when none is signed in, so the popup and its settings stay reachable.</summary>
    bool TrayVisible(Provider p) => Visible(p) || (p == providers[0] && !providers.Any(Visible));

    public void SetShown(Provider p, bool shown)
    {
        if (shown) hidden.Remove(p.Id);
        else hidden.Add(p.Id);
        for (var i = 0; i < providers.Count; i++) trayIcons[i].Visible = TrayVisible(providers[i]);
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { hidden })); }
        catch (IOException e) { Log(e); }
        Publish();
    }

    static HashSet<string> LoadHidden()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsPath))?["hidden"]?.Deserialize<HashSet<string>>() ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return new(); } // none yet, or not ours
    }

    void Publish()
    {
        published = providers.Where(Visible).ToList();
        var status = new
        {
            pid = Environment.ProcessId,
            hwnd = (long)messages!.Handle,
            providers = published.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                logo = p.Logo,
                remainingPercent = p.Remaining,
                error = p.Error,
                plan = p.Usage?.Plan,
                updatedAt = p.UpdatedAt,
                windows = p.Usage?.Windows.Select(w => new { label = w.Label, usedPercent = w.UsedPercent, resetsAt = w.ResetsAt }),
            }),
        };
        try
        {
            // Replaced whole, so Mindows never reads half a file.
            var temp = StatusPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status));
            File.Move(temp, StatusPath, overwrite: true);
        }
        catch (IOException e) { Log(e); } // Mindows was reading it; the next refresh writes it again
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == ShowPopupMessage && (int)wParam is var i && i >= 0 && i < published.Count)
        {
            ShowPopup(published[i], (short)(long)lParam, (short)((long)lParam >> 16), fromTray: false);
            handled = true;
        }
        return IntPtr.Zero;
    }

    void ShowPopup(Provider p, int x, int y, bool fromTray)
    {
        // Clicking the item again closes the popup, as on macOS. Mindows' bar never takes activation, so the popup is
        // still open then; a tray click has already closed it by deactivating it.
        if (popup?.Provider == p)
        {
            popup.Close();
            return;
        }
        if (lastClosed.provider == p && Environment.TickCount64 - lastClosed.at < 300) return;
        popup?.Close();
        var shown = popup = new PopupWindow(this, p);
        shown.Closed += (_, _) =>
        {
            lastClosed = (p, Environment.TickCount64);
            if (popup == shown) popup = null;
        };
        shown.ShowAt(x, y, above: fromTray);
    }

    /// <summary>A tray icon: the percentage left on the provider's color, or ! for an error with nothing to show.</summary>
    static Drawing.Icon Badge(Provider p)
    {
        var size = Forms.SystemInformation.SmallIconSize.Width;
        var text = p.Remaining is { } left ? $"{left:0}" : p.Error != null ? "!" : "…";
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(p.Color));
            g.FillRoundedRectangle(fill, new Drawing.Rectangle(0, 0, size, size), new Drawing.Size(size / 3, size / 3));
            using var font = new Drawing.Font("Segoe UI", size * (text.Length > 2 ? 0.45f : 0.62f), Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var center = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString(text, font, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, size, size), center);
        }
        var handle = bitmap.GetHicon();
        try { return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    /// <summary>Mindows' menu colors, so the popup matches its menus.</summary>
    void ApplyTheme()
    {
        LightTheme = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is 1;
        // WindowBg is opaque, for the settings window: normal (non-layered) windows draw translucent backgrounds over black.
        var colors = LightTheme
            ? new[] { "#F2F6F6F6", "#FF1C1C1C", "#22000000", "#1F000000", "#1A000000", "#FFF4F4F4" }
            : new[] { "#F22B2B2B", "#FFF0F0F0", "#33FFFFFF", "#33FFFFFF", "#26FFFFFF", "#FF242424" };
        string[] keys = { "MenuBg", "MenuFg", "MenuBorder", "Hover", "Track", "WindowBg" };
        for (var i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze();
            Resources[keys[i]] = brush;
        }
    }

    static void Log(object error)
    {
        try { File.AppendAllText(Path.Combine(Dir, "crash.log"), $"{DateTime.Now:O} {error}\n\n"); }
        catch (IOException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}
