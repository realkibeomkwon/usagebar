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
    /// <summary>WPF path data for a second shade of the mark, drawn under it; null for a one-tone mark.</summary>
    public string? LogoInner { get; init; }
    /// <summary>The settings icon's background when it differs from <see cref="Color"/>, which the popup's bars and the tray badge keep.</summary>
    public string? IconBackground { get; init; }
    public string IconForeground { get; init; } = "#FFFFFF";
    /// <summary>A picture file under Assets that the settings icon shows whole, in place of the tile and mark.</summary>
    public string? IconImage { get; init; }
    public string Dashboard => dashboard;

    /// <summary>Signed in, as far as the files and commands on this PC tell, so shown and fetched. Checked on each refresh.</summary>
    public bool Available { get; private set; } = signedIn?.Invoke() ?? exe() != null;
    public bool Installed => exe() != null;
    public string LoginCommand => $"{id} {loginArgs}".TrimEnd();

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
            && ClaudePlan("max", "default_claude_max_20x") == "Max 20x" && ClaudePlan("pro", null) == "Pro"
            && GrokPlan("SuperGrok Heavy") == "Ultra" && GrokPlan("SuperGrok") == "Super" && GrokPlan("Plus") == "Plus",
            "window and plan labels must read like CodexBar's");
        Debug.Assert(new[] { Logos.Claude, Logos.Codex, Logos.Grok, Logos.OpenCode, Logos.OpenCodeInner, Logos.Antigravity }.All(d => !System.Windows.Media.Geometry.Parse(d).Bounds.IsEmpty),
            "every logo must parse as WPF path data, which Mindows draws too");
        Debug.Assert(OpenCodeKey(JsonNode.Parse(
            """[{"integrationID":"opencode","active":false,"value":{"key":"old"}},{"integrationID":"opencode","active":true,"value":{}},{"integrationID":"opencode-go","active":true,"value":{"key":"go"}}]""")!.AsArray()) == "go",
            "opencode's key must be its active Go credential, not an older one");
        Debug.Assert(AntigravityWindows(JsonNode.Parse(
            """[{"name":"Gemini Models","buckets":[{"window":"weekly","remaining_fraction":0.25,"reset_time":"2026-12-01T11:01:48Z"},{"window":"5h","remaining_fraction":1}]},{"name":"Claude and GPT models","buckets":[{"window":"5h","remaining_fraction":0.5}]}]""")!.AsArray())
            .Select(w => $"{w.Label}={w.UsedPercent}").SequenceEqual(new[] { "주간 (Gemini)=75", "세션 (Gemini)=0", "세션 (Claude, GPT)=50" }),
            "agy's quota groups must become one window per bucket, labeled by group");
    }

    /// <summary>Every provider; the ones signed in on this PC are <see cref="Provider.Available"/>.</summary>
    public static List<Provider> All() => new() { Claude(), Codex(), Grok(), OpenCode(), Antigravity() };

    // The credential files belong to each CLI. Codex and Grok renew their own sign-in through their CLIs; Claude Code has
    // no command for that, so Claude's is renewed here the way Claude Code itself does it.

    const string ClaudeLogin = "로그인이 필요합니다. 터미널에서 claude auth login을 실행하세요.";
    const string ClaudeNotSignedIn = "Claude Code에 로그인되어 있지 않습니다.";

    static Provider Claude()
    {
        var file = Path.Combine(Home, ".claude", ".credentials.json");
        return new("claude", "Claude", Logos.Claude, "#D97757", "https://claude.ai/settings/usage", () => Find("claude"), "auth login", async () =>
        {
            var oauth = JsonNode.Parse(await File.ReadAllTextAsync(file))?["claudeAiOauth"] ?? throw new UsageException(ClaudeNotSignedIn);
            if (Expired(oauth["expiresAt"])) oauth = await RenewClaude(file);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new("Bearer", (string)oauth["accessToken"]!);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.0 (external, cli)");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UsageException(ClaudeLogin);
            response.EnsureSuccessStatusCode();
            var usage = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var windows = new List<UsageWindow>();
            foreach (var (key, label) in new[] { ("five_hour", "세션"), ("seven_day", "주간"), ("seven_day_opus", "주간 (Opus)"), ("seven_day_sonnet", "주간 (Sonnet)") })
                if (usage[key] is { } w && w["utilization"] is { } used) windows.Add(new(label, (double)used, Date(w["resets_at"])));
            return new(ClaudePlan((string?)oauth["subscriptionType"], (string?)oauth["rateLimitTier"]), windows);
        }, () => File.Exists(file));
    }

    static bool Expired(JsonNode? unixMs) => DateTimeOffset.FromUnixTimeMilliseconds((long)unixMs!) < DateTimeOffset.UtcNow;

    /// <summary>
    /// Renews Claude Code's expired access token as Claude Code does: under its refresh lock, after reading the file again,
    /// replacing only the claudeAiOauth tokens. Claude Code reads the file again under the same lock, so it takes the new tokens.
    /// </summary>
    static async Task<JsonNode> RenewClaude(string file)
    {
        // Claude Code's two locks, taken in its order: the current one, and the one older Claude Code builds take alone.
        var dir = Path.GetDirectoryName(file)!;
        string[] locks = { Path.Combine(dir, ".oauth_refresh.lock"), dir + ".lock" };
        if (!TryLock(locks[0])) throw new UsageException("Claude Code가 로그인을 갱신하는 중입니다.");
        if (!TryLock(locks[1]))
        {
            TryDelete(locks[0]);
            throw new UsageException("Claude Code가 로그인을 갱신하는 중입니다.");
        }
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            var oauth = root["claudeAiOauth"] ?? throw new UsageException(ClaudeNotSignedIn);
            if (!Expired(oauth["expiresAt"])) return oauth; // another Claude process renewed it meanwhile
            if (oauth["refreshTokenExpiresAt"] is { } end && Expired(end)) throw new UsageException(ClaudeLogin);
            var body = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = (string)oauth["refreshToken"]!,
                ["client_id"] = (string?)oauth["clientId"] ?? "9d1c250a-e61b-44d9-88ed-5944d1962f5e", // Claude Code's
                ["scope"] = string.Join(' ', oauth["scopes"]!.AsArray().Select(s => (string)s!)),
            };
            using var response = await Http.PostAsync("https://platform.claude.com/v1/oauth/token",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) throw new UsageException(ClaudeLogin);
            response.EnsureSuccessStatusCode();
            var tokens = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            oauth["accessToken"] = (string)tokens["access_token"]!;
            if (tokens["refresh_token"] is { } refresh) oauth["refreshToken"] = (string)refresh!;
            oauth["expiresAt"] = now + (long)tokens["expires_in"]! * 1000;
            if (tokens["refresh_token_expires_in"] is { } lifetime) oauth["refreshTokenExpiresAt"] = now + (long)lifetime * 1000;
            else oauth.AsObject().Remove("refreshTokenExpiresAt");
            if ((string?)tokens["scope"] is { Length: > 0 } scope)
                oauth["scopes"] = new JsonArray(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode)s).ToArray());
            // Written next to the file and swapped in, so a reader never sees half a file and the file keeps its permissions.
            var temp = file + ".usagebar";
            await File.WriteAllTextAsync(temp, root.ToJsonString());
            // The server may have retired the old refresh token already, so the new one must not stay behind in the temp file.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (attempt < 3) File.Replace(temp, file, null);
                    else File.Move(temp, file, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 3) { await Task.Delay(100 * (attempt + 1)); }
                catch (UnauthorizedAccessException) when (attempt < 3) { await Task.Delay(100 * (attempt + 1)); }
            }
            return oauth;
        }
        finally
        {
            TryDelete(locks[1]);
            TryDelete(locks[0]);
        }
    }

    /// <summary>A proper-lockfile lock: a directory its holder touches every 5 s, stale after 60 s.</summary>
    static bool TryLock(string dir) => CreateDirectory(dir, IntPtr.Zero)
        || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) >= TimeSpan.FromSeconds(60) && TryDelete(dir) && CreateDirectory(dir, IntPtr.Zero);

    static bool TryDelete(string dir)
    {
        try { Directory.Delete(dir); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>Unlike Directory.CreateDirectory, fails when the directory exists, which is what makes it a lock.</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    static Provider Codex()
    {
        return new("codex", "ChatGPT", Logos.Codex, "#10A37F", "https://chatgpt.com/codex/settings/usage", () => Find("codex"), "login", async () =>
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
        }) { IconBackground = "#FFFFFF", IconForeground = "#000000" };
    }

    static Provider Grok()
    {
        static string? Exe() => Find("grok", Path.Combine(Home, ".grok", "bin"));
        return new("grok", "Grok", Logos.Grok, "#000000", "https://grok.com/?_s=usage", Exe, "login", async () =>
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
            return new(GrokPlan((string?)result["subscription_tier"]), windows);
        });
    }

    const string OpenCodeLogin = "로그인이 필요합니다. 터미널에서 opencode auth login을 실행하세요.";

    static string? OpenCodeExe() => Find("opencode", Path.Combine(Home, ".opencode", "bin"));

    /// <summary>
    /// The API key opencode has for its Go plan, read through its CLI: 2.x keeps credentials in a database, so the
    /// auth.json that 1.x wrote can still hold a key that is no longer valid. That file is the fallback for older CLIs.
    /// </summary>
    static async Task<string?> OpenCodeKey()
    {
        if (OpenCodeExe() is { } exe)
        {
            try
            {
                var (exitCode, output) = await Output(exe, "auth export", TimeSpan.FromSeconds(10));
                if (exitCode == 0 && JsonNode.Parse(output) is JsonArray exported) return OpenCodeKey(exported);
            }
            catch (OperationCanceledException) { }
            catch (System.Text.Json.JsonException) { } // not the JSON this asks for; older CLI
        }
        try
        {
            var auth = JsonNode.Parse(File.ReadAllText(Path.Combine(Home, ".local", "share", "opencode", "auth.json")));
            return (string?)(auth?["opencode-go"] ?? auth?["opencode"])?["key"];
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The Go key among opencode's exported credentials: active ones only, and opencode-go before plain opencode.</summary>
    static string? OpenCodeKey(JsonArray exported) => exported
        .Where(c => (bool?)c?["active"] == true)
        .OrderBy(c => (string?)c?["integrationID"] == "opencode-go" ? 0 : 1)
        .Select(c => (string?)c?["value"]?["key"])
        .FirstOrDefault(key => key != null);

    static Provider OpenCode()
    {
        return new("opencode", "OpenCode", Logos.OpenCode, "#131010", "https://opencode.ai/auth", OpenCodeExe, "auth login", async () =>
        {
            var token = await OpenCodeKey() ?? throw new UsageException(OpenCodeLogin);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://opencode.ai/zen/go/v1/usage");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UsageException(OpenCodeLogin);
            response.EnsureSuccessStatusCode();
            var usage = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["usage"] ?? throw new UsageException("사용량 정보를 받지 못했습니다.");
            var windows = new List<UsageWindow>();
            foreach (var (key, label) in new[] { ("rolling", "세션"), ("weekly", "주간"), ("monthly", "월간") })
                if (usage[key] is { } w && w["percent"] is { } used) windows.Add(new(label, (double)used, Date(w["resetsAt"])));
            return new("Go", windows);
        }) { LogoInner = Logos.OpenCodeInner };
    }

    static Provider Antigravity()
    {
        static string? Exe() => Find("agy", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin"));
        // agy has no sign-in command; it asks for one when started without a prompt.
        return new("agy", "Antigravity", Logos.Antigravity, "#60BA7E", "https://antigravity.google/", Exe, "", async () =>
        {
            var exe = Exe() ?? throw new UsageException("agy 명령을 찾을 수 없습니다.");
            // /quota answers without a model turn. The log file is named so agy doesn't leave a new one per refresh.
            var (_, output) = await Output(exe, $"-p /quota --output-format json --print-timeout 60s --log-file \"{Path.Combine(App.Dir, "agy.log")}\"",
                TimeSpan.FromSeconds(75));
            JsonNode? result;
            try { result = JsonNode.Parse(output); }
            catch (System.Text.Json.JsonException) { throw new UsageException(output.Trim() is { Length: > 0 } text ? text : "사용량 정보를 받지 못했습니다."); }
            if (result?["command"]?["data"]?["groups"] is not JsonArray groups)
                throw new UsageException((string?)result?["response"] is { Length: > 0 } response ? response.Trim() : "사용량 정보를 받지 못했습니다.");
            return new(null, AntigravityWindows(groups));
        }) { IconImage = "antigravity.png" };
    }

    /// <summary>One window per quota bucket, such as "주간 (Gemini)"; each group of models has its own 5-hour and weekly limit.</summary>
    static List<UsageWindow> AntigravityWindows(JsonArray groups) => groups
        .SelectMany(g => g!["buckets"]!.AsArray().Select(b => new UsageWindow(
            $"{(string?)b!["window"] switch { "5h" => "세션", "weekly" => "주간", var w => w }} ({Regex.Replace((string)g["name"]!, @"\s+models$", "", RegexOptions.IgnoreCase).Replace(" and ", ", ")})",
            Math.Round((1 - (double)b["remaining_fraction"]!) * 100, 1), Date(b["reset_time"]))))
        .ToList();

    /// <summary>The CLI still answers with the old plan names; Grok's unified plans call them Super and Ultra.</summary>
    // shortcut: only these two are mapped, as Plus and Premium have no known CLI name yet; add them when one shows up.
    static string? GrokPlan(string? tier) => tier switch
    {
        "SuperGrok Heavy" => "Ultra",
        "SuperGrok" => "Super",
        _ => tier,
    };

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

    /// <summary>Runs a CLI to its end and returns its exit code and output; on timeout it is killed and the cancellation thrown.</summary>
    static async Task<(int ExitCode, string Output)> Output(string exe, string args, TimeSpan timeout)
    {
        var shim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        using var process = Process.Start(new ProcessStartInfo(shim ? "cmd.exe" : exe, shim ? $"/c \"\"{exe}\" {args}\"" : args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync(); // drained, so a chatty failure can't fill the pipe and stall
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
            throw;
        }
        return (process.ExitCode, await output);
    }

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
