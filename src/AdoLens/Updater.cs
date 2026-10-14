using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AdoLens;

/// <summary>
/// A release version: "0.2.0", or a test release "0.3.0-beta.2". A test release comes before its final version
/// (0.3.0-beta.2 &lt; 0.3.0); its parts after the dash compare as numbers where they are numbers (beta.10 &gt; beta.9).
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Pre = null) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Pre != null;

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre == null || other.Pre == null) return Pre == null ? (other.Pre == null ? 0 : 1) : -1;
        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = int.TryParse(a[i], out var ai), bn = int.TryParse(b[i], out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(ReleaseVersion? a, ReleaseVersion? b) => a is not null && a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion? a, ReleaseVersion? b) => b is not null && b.CompareTo(a) > 0;
    public static bool operator >=(ReleaseVersion? a, ReleaseVersion? b) => !(a < b);
    public static bool operator <=(ReleaseVersion? a, ReleaseVersion? b) => !(a > b);

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Pre != null ? "-" + Pre : "")}";
}

/// <summary>
/// Updates from GitHub releases: the package's SHA-256 is the digest GitHub publishes for the asset, and the installer
/// of the new package runs in its own window (it replaces this program's files). The MCP server checks at most once a
/// day in the background and tells Claude once per session; nothing is installed without being asked.
/// </summary>
public static class Updater
{
    public const string Repository = "willowdev-code/ado-lens";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string PageUrl, string? AssetName, string? AssetUrl, string? Sha256,
        string Notes, bool Prerelease = false);

    /// <summary>The running version, test-release suffix included; "+commit" build metadata dropped.</summary>
    public static ReleaseVersion Current { get; } =
        ParseVersion(typeof(Lens).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion)
        ?? new ReleaseVersion(0, 0, 0);

    /// <summary>"v0.2.10" / "0.2.10" → 0.2.10; "v0.3.0-beta.2" → 0.3.0-beta.2.</summary>
    public static ReleaseVersion? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        if (t.IndexOf('+') is var plus and >= 0) t = t[..plus];
        string? pre = null;
        if (t.IndexOf('-') is var dash and > 0)
        {
            pre = t[(dash + 1)..];
            t = t[..dash];
            if (pre.Length == 0) pre = null;
        }
        return Version.TryParse(t, out var v) ? new ReleaseVersion(v.Major, v.Minor, Math.Max(0, v.Build), pre) : null;
    }

    public static ReleaseInfo? ParseRelease(string json) => ParseRelease(JsonNode.Parse(json));

    /// <summary>The newest release in the JSON of GET /repos/{repo}/releases, test releases included (drafts skipped).</summary>
    public static ReleaseInfo? NewestRelease(string json) =>
        (JsonNode.Parse(json) as JsonArray)?.OfType<JsonObject>()
            .Where(r => r["draft"]?.GetValue<bool>() != true)
            .Select(r => ParseRelease(r)).OfType<ReleaseInfo>()
            .MaxBy(r => r.Version);

    static ReleaseInfo? ParseRelease(JsonNode? n)
    {
        var tag = n?["tag_name"]?.GetValue<string>();
        if (ParseVersion(tag) is not { } version) return null;
        var asset = (n!["assets"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["name"]?.GetValue<string>() is { } name
                                 && name.StartsWith("ado-lens-", StringComparison.OrdinalIgnoreCase)
                                 && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var digest = asset?["digest"]?.GetValue<string>();
        return new ReleaseInfo(version, tag!, n["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repository}/releases",
            asset?["name"]?.GetValue<string>(), asset?["browser_download_url"]?.GetValue<string>(),
            digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
            n["body"]?.GetValue<string>() ?? "", n["prerelease"]?.GetValue<bool>() ?? version.IsPrerelease);
    }

    static string StateFile => Path.Combine(Settings.DataDir, "update.json");

    static (ReleaseVersion? Latest, DateTime? Checked) ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return (null, null);
            var n = JsonNode.Parse(File.ReadAllText(StateFile));
            var when = DateTime.TryParse(n?["checkedUtc"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null;
            return (ParseVersion(n?["latest"]?.GetValue<string>()), when);
        }
        catch (Exception) { return (null, null); }
    }

    static void SaveState(ReleaseInfo r)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(StateFile, new JsonObject { ["latest"] = r.Version.ToString(), ["checkedUtc"] = DateTime.UtcNow.ToString("o") }.ToJsonString());
        }
        catch (Exception) { }
    }

    /// <summary>"ADO Lens 0.3.0 is available (you have 0.2.0)…" from the last check, without touching the network.</summary>
    public static string? PendingNotice()
    {
        var (latest, _) = ReadState();
        return latest != null && latest > Current
            ? $"ADO Lens {latest} is available (you have {Current}): ado_upgrade installs it (say 'update ADO Lens'); settings and PATs are kept"
            : null;
    }

    /// <summary>Checks GitHub at most once a day, in the background; failures are silent (offline, proxy).</summary>
    public static void CheckInBackground()
    {
        var (_, checkedUtc) = ReadState();
        if (checkedUtc is { } c && DateTime.UtcNow - c < CheckEvery) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = Client();
                http.Timeout = TimeSpan.FromSeconds(20);
                if (ParseRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest")) is { } r) SaveState(r);
            }
            catch (Exception) { }
        });
    }

    static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ado-lens", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>The installer renames a running adolens.exe to *.old; the next start removes those no longer in use.</summary>
    public static void RemoveLeftovers()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old"))
                try { File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still used by an older Claude session
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The installation folder of the running program: the parent of its 'bin' folder.</summary>
    public static string InstallDir()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && bin.Parent != null ? bin.Parent.FullName : bin.FullName;
    }

    /// <summary>Looks up the latest release; with <paramref name="install"/> downloads, verifies and starts its installer.</summary>
    /// <returns>Text for the user, and whether it failed.</returns>
    public static async Task<(string Text, bool Failed)> RunAsync(bool install, bool pre = false, CancellationToken ct = default)
    {
        using var http = Client();
        ReleaseInfo? latest;
        try
        {
            // /releases/latest never returns a test release; the list holds both kinds.
            latest = pre
                ? NewestRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", ct))
                : ParseRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ($"cannot reach GitHub: {ex.Message}", true);
        }
        if (latest == null) return ("the latest release has no readable version tag", true);
        if (!latest.Prerelease) SaveState(latest);

        var head = $"installed: {Current}\nlatest:    {latest.Version}{(latest.Prerelease ? " (test release)" : "")}  {latest.PageUrl}";
        if (latest.Version <= Current) return ($"{head}\nyou have the latest version.", false);
        if (!install)
        {
            var notes = latest.Notes.Replace("\r", "").Split('\n').Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).Take(12).ToList();
            return ($"{head}{(notes.Count > 0 ? "\nchanges:\n" + string.Join('\n', notes.Select(l => "  " + l.Trim())) : "")}" +
                    $"\ninstall it with: adolens upgrade --install{(pre ? " --pre" : "")} (settings and PATs are kept)", false);
        }
        if (latest.AssetUrl == null || latest.AssetName == null) return ($"release {latest.Tag} has no ado-lens-*.zip package - download it from {latest.PageUrl}", true);
        if (latest.Sha256 == null) return ($"GitHub publishes no SHA-256 digest for {latest.AssetName} - not installing an unverified package", true);

        var work = Path.Combine(Path.GetTempPath(), $"ado-lens-update-{latest.Version}");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, latest.AssetName);
        await using (var src = await http.GetStreamAsync(latest.AssetUrl, ct))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst, ct);

        var actual = Sha256Of(zip);
        if (!actual.Equals(latest.Sha256, StringComparison.OrdinalIgnoreCase))
            return ($"SHA-256 mismatch for {latest.AssetName}: expected {latest.Sha256}, got {actual} - not installing", true);

        var extracted = Path.Combine(work, "package");
        ZipFile.ExtractToDirectory(zip, extracted);
        var script = Directory.EnumerateFiles(extracted, "install.ps1", SearchOption.AllDirectories).FirstOrDefault();
        if (script == null) return ("the package has no install.ps1", true);

        // The installer replaces this program's own files, so it runs in its own window after this process ends.
        var target = InstallDir();
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{script}\" -InstallDir \"{target}\"",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        });
        return ($"{head}\nSHA-256 verified ({actual[..12]}…). The installer continues in its own window (target {target}); " +
                "Claude may stay open - new sessions use the new version, Claude Desktop needs a restart for running ones.", false);
    }
}
