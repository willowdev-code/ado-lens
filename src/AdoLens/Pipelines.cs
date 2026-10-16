using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>Builds: recent runs, why a run failed (errors and the lines of the log around them), queuing a run.</summary>
public static partial class PipelineQueries
{
    static DateTime? Date(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    static string When(JsonElement b) =>
        (Date(Json.Str(b, "finishTime")) ?? Date(Json.Str(b, "startTime")) ?? Date(Json.Str(b, "queueTime")))?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";

    static string Duration(JsonElement b) =>
        Date(Json.Str(b, "startTime")) is { } s && Date(Json.Str(b, "finishTime")) is { } f ? $"{(int)(f - s).TotalMinutes} min" : "";

    static string Line(JsonElement b) =>
        $"build {b.GetProperty("id").GetInt32()} {Json.Str(Json.Obj(b, "definition"), "name")} {Json.Str(b, "buildNumber")}: " +
        $"{Json.Str(b, "result") ?? Json.Str(b, "status")}, {Code.Short(Json.Str(b, "sourceBranch"))}, " +
        $"{Json.Str(Json.Obj(b, "requestedFor"), "displayName")}, {When(b)}{(Duration(b) is { Length: > 0 } d ? $", {d}" : "")}";

    public static async Task<string> ListAsync(Lens lens, string? connection, string? project, string? definition, string? branch, int top, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var query = new List<string> { $"$top={Math.Clamp(top, 1, 100)}", "queryOrder=queueTimeDescending" };
        if (definition is not null) query.Add("definitions=" + (await DefinitionAsync(session, name, definition, ct)).Id);
        if (branch is not null) query.Add("branchName=" + Uri.EscapeDataString(branch.StartsWith("refs/", StringComparison.Ordinal) || branch.StartsWith("$/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch));
        var builds = await session.Client.GetListAsync($"{AdoClient.Scope(name)}_apis/build/builds?{string.Join('&', query)}", ct);
        if (builds.Count == 0) return "no builds found";
        return $"recent builds in {name}:\n" + string.Join('\n', builds.Select(Line)) + "\nbuild=<id> shows why a build failed.";
    }

    static async Task<(int Id, string Name)> DefinitionAsync(Session session, string project, string definition, CancellationToken ct)
    {
        if (int.TryParse(definition, out var id)) return (id, definition);
        var defs = await session.Client.GetListAsync($"{AdoClient.Scope(project)}_apis/build/definitions?name={Uri.EscapeDataString("*" + definition.Trim() + "*")}&$top=50", ct);
        var exact = defs.FirstOrDefault(d => (Json.Str(d, "name") ?? "").Equals(definition.Trim(), StringComparison.OrdinalIgnoreCase));
        var pick = exact.ValueKind == JsonValueKind.Object ? exact : defs.Count == 1 ? defs[0]
            : throw new InvalidOperationException(defs.Count == 0 ? $"no pipeline named like '{definition}'"
                : $"'{definition}' matches {defs.Count} pipelines: {string.Join("; ", defs.Select(d => Json.Str(d, "name")).Take(15))}");
        return (pick.GetProperty("id").GetInt32(), Json.Str(pick, "name")!);
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z ")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"(?i)(##\[error\]|\berror\b|\bfailed\b|\bexception\b)")]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"(?i)\b0 error|\berror\(s\)\s*:\s*0|warning")]
    private static partial Regex NotAnError();

    // MSBuild repeats where an error was reported from: "C:\...\x.targets(40,5): error : text [C:\...\x.proj]".
    [GeneratedRegex(@"^\s*\S.*?\.(targets|props|proj)\(\d+,\d+\):\s*")]
    private static partial Regex MsBuildPrefix();

    [GeneratedRegex(@"\s*\[[^\]]+\.(proj|csproj|rnrproj|sln)\]\s*$")]
    private static partial Regex MsBuildProject();

    static string Clean(string line) => MsBuildProject().Replace(MsBuildPrefix().Replace(line, ""), "");

    /// <summary>The lines of a log that say what went wrong, with their numbers; the end of the log when none do.</summary>
    internal static string Excerpt(string log, int max)
    {
        var lines = log.Replace("\r", "").Split('\n').Select(l => Clean(Timestamp().Replace(l, ""))).ToList();
        var hits = lines.Select((l, i) => (l, i)).Where(x => ErrorLine().IsMatch(x.l) && !NotAnError().IsMatch(x.l))
            .DistinctBy(x => x.l.Trim()).Take(max).ToList();
        if (hits.Count == 0)
            return string.Join('\n', lines.Select((l, i) => (l, i)).Where(x => x.l.Trim().Length > 0).TakeLast(Math.Min(max, 30)).Select(x => $"{x.i + 1}: {x.l.TrimEnd()}"));
        return string.Join('\n', hits.Select(x => $"{x.i + 1}: {Formatting.Collapse(x.l, 300)}"));
    }

    /// <summary>
    /// Recent deployments of classic releases, newest first: definition, stage, release name, when it finished, the
    /// build deployed - "when did XYZ reach UAT" (the date then goes into since= of other tools).
    /// </summary>
    public static async Task<string> DeploymentsAsync(Lens lens, string? connection, string? project, string? definition, string? stage, int top,
        CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var vsrm = session.Client.ServiceUrl("vsrm") + Uri.EscapeDataString(name) + "/";
        var filter = "";
        if (!string.IsNullOrWhiteSpace(definition))
        {
            var defs = await session.Client.GetListAsync($"{vsrm}_apis/release/definitions?searchText={Uri.EscapeDataString(definition.Trim())}", ct);
            var def = defs.FirstOrDefault(d => string.Equals(Json.Str(d, "name"), definition.Trim(), StringComparison.OrdinalIgnoreCase));
            if (def.ValueKind != JsonValueKind.Object) def = defs.FirstOrDefault();
            if (def.ValueKind != JsonValueKind.Object) return $"no release definition like '{definition}' in {name}";
            filter = $"&definitionId={def.GetProperty("id").GetInt32()}";
        }
        top = Math.Clamp(top, 1, 100);
        var list = await session.Client.GetListAsync($"{vsrm}_apis/release/deployments?queryOrder=descending&$top={(stage is null ? top : 200)}{filter}", ct);
        var rows = list.Where(d => stage is null || (Json.Str(Json.Obj(d, "releaseEnvironment"), "name") ?? "").Contains(stage.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(top).ToList();
        if (rows.Count == 0) return $"no deployments found in {name}{(definition is null ? "" : $" for {definition}")}{(stage is null ? "" : $", stage {stage}")}";
        var sb = new StringBuilder($"deployments in {name}, newest first:\n");
        foreach (var d in rows)
        {
            var when = DateTime.TryParse(Json.Str(d, "completedOn") ?? Json.Str(d, "startedOn") ?? Json.Str(d, "queuedOn"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var w) ? w.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?";
            var artifact = Json.Arr(Json.Obj(d, "release"), "artifacts").FirstOrDefault(a => Json.Bool(a, "isPrimary"));
            var version = Json.Str(Json.Obj(Json.Obj(artifact, "definitionReference"), "version"), "name");
            sb.AppendLine($"- {when} {Json.Str(Json.Obj(d, "releaseDefinition"), "name")} / {Json.Str(Json.Obj(d, "releaseEnvironment"), "name")}: " +
                          $"{Json.Str(Json.Obj(d, "release"), "name")} {Json.Str(d, "deploymentStatus")}{(version is null ? "" : $", build {version}")}");
        }
        return sb.Append("A time here works as since= (e.g. ado_pr merged=staging since=\"2026-10-15 14:30\").").ToString();
    }

    const long MaxArtifactRead = 20 * 1024 * 1024;

    /// <summary>
    /// The artifacts of a build (packages, logs, reports): the list; with <paramref name="artifact"/> the files in it,
    /// <paramref name="part"/> one file read as text, <paramref name="save"/> the whole artifact streamed to disk.
    /// </summary>
    public static async Task<string> ArtifactsAsync(Lens lens, string? connection, string? project, int build, string? artifact, string? part, bool save,
        CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var scope = AdoClient.Scope(session.Project(project));
        var list = await session.Client.GetListAsync($"{scope}_apis/build/builds/{build}/artifacts", ct);
        static long? SizeOf(JsonElement a) =>
            long.TryParse(Json.Str(Json.Obj(Json.Obj(a, "resource"), "properties"), "artifactsize"), out var n) ? n : null;
        if (list.Count == 0) return $"build {build} has no artifacts";
        if (string.IsNullOrWhiteSpace(artifact))
            return $"artifacts of build {build}:\n" + string.Join('\n', list.Select(a =>
                       $"- {Json.Str(a, "name")} ({Json.Str(Json.Obj(a, "resource"), "type")}{(SizeOf(a) is { } s ? ", " + Documents.Size(s) : "")})")) +
                   "\nartifact=<name> lists its files; part=<file> reads one; save=true saves the artifact (ZIP) to disk.";
        var pick = list.FirstOrDefault(a => string.Equals(Json.Str(a, "name"), artifact.Trim(), StringComparison.OrdinalIgnoreCase));
        if (pick.ValueKind != JsonValueKind.Object) return $"no artifact '{artifact}' in build {build}; artifacts: {string.Join(", ", list.Select(a => Json.Str(a, "name")))}";
        var name = Json.Str(pick, "name")!;
        var url = Json.Str(Json.Obj(pick, "resource"), "downloadUrl");
        if (url is null) return $"artifact {name} has no download link (type {Json.Str(Json.Obj(pick, "resource"), "type")}) - open the build in the browser";
        if (save)
            return await Downloads.SaveAsync(session.Client, url, Downloads.BaseDir(lens.Settings), $"build {build}", name + ".zip", ct);
        if (SizeOf(pick) > MaxArtifactRead)
            return $"{name} is {Documents.Size(SizeOf(pick)!.Value)} - too large to read here; save=true saves it to disk";
        byte[] bytes;
        try { bytes = await session.Client.GetBytesAsync(url, MaxArtifactRead, ct); }
        catch (AdoApiException ex) when (ex.Message.Contains("larger than", StringComparison.Ordinal)) { return $"{name}: {ex.Message}; save=true saves it to disk"; }
        var content = Documents.Read(name + ".zip", bytes, null, part);
        return (content.Note is null ? "" : content.Note + "\n") + (content.Text is null ? "nothing readable as text; save=true saves it to disk" : Documents.Fit(content.Text, 12000, false));
    }

    public static async Task<string> BuildAsync(Lens lens, string? connection, string? project, int id, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var scope = AdoClient.Scope(name);
        var b = await session.Client.GetAsync($"{scope}_apis/build/builds/{id}", ct);
        var sb = new StringBuilder(Line(b)).AppendLine();
        sb.AppendLine($"commit {Json.Str(b, "sourceVersion")?[..Math.Min(8, Json.Str(b, "sourceVersion")!.Length)]}, reason {Json.Str(b, "reason")}");
        if (Json.Str(Json.Obj(Json.Obj(b, "_links"), "web"), "href") is { } url) sb.AppendLine(url);

        var timeline = await session.Client.GetAsync($"{scope}_apis/build/builds/{id}/timeline", ct);
        var records = Json.Arr(timeline, "records").ToList();
        var failed = records.Where(r => Json.Str(r, "result") is "failed" or "canceled").ToList();
        var tasks = failed.Where(r => Json.Str(r, "type") == "Task").ToList();
        if (failed.Count == 0)
        {
            var warnings = records.Sum(r => r.TryGetProperty("warningCount", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0);
            sb.Append(Json.Str(b, "status") == "completed" ? $"nothing failed{(warnings > 0 ? $" ({warnings} warnings)" : "")}" : "still running");
            return sb.ToString();
        }
        sb.AppendLine("failed: " + string.Join(" > ", failed.Where(r => Json.Str(r, "type") is "Stage" or "Job" or "Phase").Select(r => Json.Str(r, "name")).Distinct().Append(string.Join(", ", tasks.Select(t => Json.Str(t, "name"))))));
        foreach (var t in tasks.Take(detail == "full" ? 10 : 2))
        {
            sb.AppendLine($"\n{Json.Str(t, "name")}:");
            var errors = Json.Arr(t, "issues").Where(i => Json.Str(i, "type") == "error").Select(i => Formatting.Collapse(Json.Str(i, "message") ?? "", 300)).Distinct().ToList();
            foreach (var e in errors.Take(10)) sb.AppendLine($"  error: {e}");
            if (Json.Obj(Json.Obj(t, "log"), "id") is { ValueKind: JsonValueKind.Number } logId)
            {
                var log = await session.Client.GetTextAsync($"{scope}_apis/build/builds/{id}/logs/{logId.GetInt32()}", ct);
                sb.AppendLine("  log (lines that report errors):");
                sb.AppendLine(string.Join('\n', Excerpt(log, detail == "full" ? 200 : 40).Split('\n').Select(l => "  " + l)));
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static async Task<string> RunAsync(Lens lens, string? connection, string? project, string definition, string? branch, string? parameters, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var def = await DefinitionAsync(session, name, definition, ct);
        var body = new Dictionary<string, object?> { ["definition"] = new { id = def.Id } };
        if (branch is not null) body["sourceBranch"] = branch.StartsWith("refs/", StringComparison.Ordinal) || branch.StartsWith("$/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;
        if (parameters is not null) body["parameters"] = parameters;
        var queued = await session.Client.PostAsync($"{AdoClient.Scope(name)}_apis/build/builds", body, ct);
        return $"queued {Line(queued)}" + (Json.Str(Json.Obj(Json.Obj(queued, "_links"), "web"), "href") is { } url ? "\n" + url : "");
    }
}
