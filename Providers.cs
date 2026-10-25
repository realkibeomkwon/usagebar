using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UsageBar;

/// <summary>A rate limit window: how much of it is used, and when it starts over.</summary>
public record UsageWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt);

public record Usage(string? Plan, List<UsageWindow> Windows);

/// <summary>A message meant for the user, shown as is.</summary>
class UsageException(string message) : Exception(message);

/// <param name="exe">The provider's CLI, whose id is its command name.</param>
/// <param name="signedIn">Whether to fetch at all; by default, whether the CLI is installed.</param>
public class Provider(string id, string name, string logo, string color, string dashboard, Func<string?> exe, string loginArgs,
    Func<Task<Usage>> fetch, Func<bool>? signedIn = null)
{
    public string Id => id;
    public string Name => name;
    /// <summary>WPF path data for the provider's mark.</summary>
    public string Logo => logo;
    public string Color => color;
    public string Dashboard => dashboard;

    /// <summary>Signed in, as far as the files and commands on this PC tell, so shown and fetched. Checked on each refresh.</summary>
    public bool Available { get; private set; } = signedIn?.Invoke() ?? exe() != null;
    public bool Installed => exe() != null;
    public string LoginCommand => $"{id} {loginArgs}";

    /// <summary>The CLI's own sign-in in a console window, which stays open only if it fails; null without the CLI.</summary>
    public ProcessStartInfo? Login() => exe() is { } path ? new("cmd.exe", $"/c \"\"{path}\" {loginArgs} || pause\"") : null;

    public Usage? Usage { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>The percentage left in the most used window, CodexBar's automatic menu bar number.</summary>
    // shortcut: CodexBar first ranks windows on track to run out before their reset; add that pace estimate if this misleads.
    public double? Remaining => Usage is { Windows.Count: > 0 } u ? Math.Clamp(100 - u.Windows.Max(w => w.UsedPercent), 0, 100) : null;

    public async Task Refresh()
    {
        Available = signedIn?.Invoke() ?? exe() != null;
        if (!Available)
        {
            (Usage, Error) = (null, null);
            return;
        }
        try
        {
            Usage = await fetch();
            (Error, UpdatedAt) = (null, DateTimeOffset.Now);
        }
        catch (Exception e) // the last good usage stays, shown with the error
        {
            Error = e switch
            {
                UsageException => e.Message,
                OperationCanceledException => "응답 시간이 초과됐습니다.",
                HttpRequestException => $"네트워크 오류: {e.Message}",
                _ => e.Message,
            };
        }
    }
}

static class Providers
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static Providers()
    {
        Debug.Assert(WindowLabel(300) == "세션" && WindowLabel(10080) == "주간" && WindowLabel(60 * 24 * 30) == "30일"
            && ClaudePlan("max", "default_claude_max_20x") == "Max 20x" && ClaudePlan("pro", null) == "Pro",
            "window and plan labels must read like CodexBar's");
        Debug.Assert(new[] { Logos.Claude, Logos.Codex, Logos.Grok }.All(d => !System.Windows.Media.Geometry.Parse(d).Bounds.IsEmpty),
            "every logo must parse as WPF path data, which Mindows draws too");
    }

    /// <summary>Every provider; the ones signed in on this PC are <see cref="Provider.Available"/>.</summary>
    public static List<Provider> All() => new() { Claude(), Codex(), Grok() };

    // The credential files belong to each CLI, which rotates their refresh tokens: read them only, never refresh here.

    static Provider Claude()
    {
        var file = Path.Combine(Home, ".claude", ".credentials.json");
        const string Expired = "로그인이 만료됐습니다. Claude Code를 실행하면 갱신됩니다.";
        return new("claude", "Claude", Logos.Claude, "#D97757", "https://claude.ai/settings/usage", () => Find("claude"), "auth login", async () =>
        {
            var oauth = JsonNode.Parse(await File.ReadAllTextAsync(file))?["claudeAiOauth"]
                ?? throw new UsageException("Claude Code에 로그인되어 있지 않습니다.");
            if (DateTimeOffset.FromUnixTimeMilliseconds((long)oauth["expiresAt"]!) < DateTimeOffset.UtcNow) throw new UsageException(Expired);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new("Bearer", (string)oauth["accessToken"]!);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.0 (external, cli)");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UsageException(Expired);
            response.EnsureSuccessStatusCode();
            var usage = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var windows = new List<UsageWindow>();
            foreach (var (key, label) in new[] { ("five_hour", "세션"), ("seven_day", "주간"), ("seven_day_opus", "주간 (Opus)"), ("seven_day_sonnet", "주간 (Sonnet)") })
                if (usage[key] is { } w && w["utilization"] is { } used) windows.Add(new(label, (double)used, Date(w["resets_at"])));
            return new(ClaudePlan((string?)oauth["subscriptionType"], (string?)oauth["rateLimitTier"]), windows);
        }, () => File.Exists(file));
    }

    static Provider Codex()
    {
        return new("codex", "Codex", Logos.Codex, "#10A37F", "https://chatgpt.com/codex/settings/usage", () => Find("codex"), "login", async () =>
        {
            var exe = Find("codex") ?? throw new UsageException("codex 명령을 찾을 수 없습니다.");
            JsonNode result;
            try
            {
                // The app server refreshes its own sign-in, which a direct call with auth.json can't.
                result = await Rpc(exe, "-s read-only -a never app-server",
                    Request(1, "initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "UsageBar", ["version"] = "1.0" } }),
                    new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JsonObject() },
                    Request(2, "account/rateLimits/read"));
            }
            catch (UsageException e) when (e.Message.Contains("401"))
            {
                throw new UsageException("로그인이 필요합니다. 터미널에서 codex login을 실행하세요.");
            }
            var limits = result["rateLimits"] ?? throw new UsageException("사용량 정보를 받지 못했습니다.");
            var windows = new[] { limits["primary"], limits["secondary"] }.OfType<JsonNode>().Select(w => new UsageWindow(
                WindowLabel((int?)w["windowDurationMins"]), (double)w["usedPercent"]!,
                w["resetsAt"] is { } at ? DateTimeOffset.FromUnixTimeSeconds((long)at) : null)).ToList();
            return new(Title((string?)limits["planType"]), windows);
        });
    }

    static Provider Grok()
    {
        static string? Exe() => Find("grok", Path.Combine(Home, ".grok", "bin"));
        return new("grok", "Grok", Logos.Grok, "#8E8E93", "https://grok.com/?_s=usage", Exe, "login", async () =>
        {
            var exe = Exe() ?? throw new UsageException("grok 명령을 찾을 수 없습니다.");
            JsonNode result;
            try
            {
                // The agent refreshes its own sign-in; the token in ~/.grok/auth.json expires after about a week.
                result = await Rpc(exe, "agent stdio",
                    Request(1, "initialize", new JsonObject
                    {
                        ["protocolVersion"] = 1,
                        ["clientCapabilities"] = new JsonObject
                        {
                            ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                            ["terminal"] = false,
                        },
                    }),
                    Request(2, "_x.ai/billing"));
            }
            catch (UsageException e) when (e.Message.Contains("grok login"))
            {
                throw new UsageException("로그인이 필요합니다. 터미널에서 grok login을 실행하세요.");
            }
            // shortcut: reads only the current credits payload, not the older monthlyLimit/usage one, and leaves the
            // percentage unknown when a new week's payload omits it; CodexBar asks grok.com then, add that if it shows up.
            var config = result["config"] ?? throw new UsageException("사용량 정보를 받지 못했습니다.");
            var period = config["currentPeriod"];
            var label = (string?)period?["type"] switch
            {
                "USAGE_PERIOD_TYPE_WEEKLY" => "주간",
                "USAGE_PERIOD_TYPE_MONTHLY" => "월간",
                _ => "크레딧",
            };
            var windows = new List<UsageWindow>();
            if (config["creditUsagePercent"] is { } used) windows.Add(new(label, (double)used, Date(period?["end"] ?? config["billingPeriodEnd"])));
            return new((string?)result["subscription_tier"], windows);
        });
    }

    static string WindowLabel(int? minutes) => minutes switch
    {
        null => "한도",
        <= 300 => "세션",
        10080 => "주간",
        _ when minutes % 1440 == 0 => $"{minutes / 1440}일",
        _ => $"{minutes / 60}시간",
    };

    /// <summary>"Max 20x" from subscription type max and tier default_claude_max_20x.</summary>
    static string? ClaudePlan(string? type, string? tier)
    {
        var multiplier = Regex.Match(tier ?? "", @"_(\d+x)$");
        return Title(type) is { } plan && multiplier.Success ? $"{plan} {multiplier.Groups[1].Value}" : Title(type);
    }

    static string? Title(string? s) => s == null ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);

    static DateTimeOffset? Date(JsonNode? node) =>
        DateTimeOffset.TryParse((string?)node, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>A command's full path on PATH, else in <paramref name="fallbackDir"/>: an exe, or an npm .cmd shim.</summary>
    static string? Find(string name, string? fallbackDir = null) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Append(fallbackDir).OfType<string>()
            .SelectMany(dir => new[] { ".exe", ".cmd" }.Select(ext => Path.Combine(dir.Trim('"'), name + ext)))
            .FirstOrDefault(File.Exists);

    static JsonObject Request(int id, string method, JsonObject? parameters = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };

    /// <summary>
    /// Sends JSON-RPC messages, one per line, to a CLI's stdio server and returns the last request's result.
    /// Notifications (no id) get no answer; the server's own notifications are skipped.
    /// </summary>
    static async Task<JsonNode> Rpc(string exe, string args, params JsonObject[] messages)
    {
        var shim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(shim ? "cmd.exe" : exe, shim ? $"/c \"\"{exe}\" {args}\"" : args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            JsonNode? result = null;
            foreach (var message in messages)
            {
                await process.StandardInput.WriteLineAsync(message.ToJsonString());
                await process.StandardInput.FlushAsync();
                if (message["id"] is not { } id) continue;
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                        ?? throw new UsageException($"{Path.GetFileNameWithoutExtension(exe)}이(가) 응답 없이 종료됐습니다.");
                    if (!line.StartsWith('{') || JsonNode.Parse(line) is not { } answer || answer["id"]?.ToJsonString() != id.ToJsonString()) continue;
                    if (answer["error"] is { } error) throw new UsageException((string?)error["message"] ?? error.ToJsonString());
                    result = answer["result"];
                    break;
                }
            }
            return result ?? throw new UsageException("사용량 정보를 받지 못했습니다.");
        }
        finally
        {
            // The whole tree: a shim's cmd would otherwise leave its node process running.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
        }
    }
}
