using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdoLens;

/// <summary>
/// Checks of a code change before it goes to testing: a work item is linked, every label it adds or uses exists in
/// every language, messages in X++ come from labels, no TODO is left behind.
/// </summary>
public static partial class Review
{
    [GeneratedRegex(@"@(?<file>[A-Za-z][A-Za-z0-9_]*):(?<id>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex LabelRef();

    // A message shown to the user with a literal text instead of a label.
    [GeneratedRegex(@"\b(?<call>info|warning|error|checkFailed|throw\s+error|Box::\w+)\s*\(\s*(?:strFmt\s*\(\s*)?""(?<text>[^""@][^""]{2,})""", RegexOptions.IgnoreCase)]
    private static partial Regex Hardcoded();

    [GeneratedRegex(@"\b(TODO|FIXME|HACK)\b")]
    private static partial Regex Todo();

    [GeneratedRegex(@"vstfs:///VersionControl/Changeset/(?<cs>\d+)|vstfs:///Git/PullRequestId/[^%]+%2[Ff][^%]+%2[Ff](?<pr>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ArtifactLink();

    /// <summary>A change (changeset, PR, commit) or every change linked to a work item and its tasks.</summary>
    public static async Task<string> ReviewAsync(Lens lens, string? connection, string? change, int? id, string? repo, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var refs = new List<string>();
        var sb = new StringBuilder();
        if (change is not null) refs.Add(change);
        else if (id is { } wid)
        {
            var items = await session.Work.GetAsync([wid], relations: true, ct: ct);
            if (items.Count == 0) return $"no work item #{wid} visible with this PAT";
            var children = Json.Arr(items[0], "relations").Where(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Forward")
                .Select(r => Audit.TargetOf(Json.Str(r, "url")) ?? 0).Where(i => i > 0).ToList();
            var all = children.Count == 0 ? items : items.Concat(await session.Work.GetAsync(children, relations: true, ct: ct)).ToList();
            foreach (var r in all.SelectMany(w => Json.Arr(w, "relations")).Where(r => Json.Str(r, "rel") == "ArtifactLink"))
                if (ArtifactLink().Match(Json.Str(r, "url") ?? "") is { Success: true } m)
                    refs.Add(m.Groups["cs"].Success ? m.Groups["cs"].Value : "PR " + m.Groups["pr"].Value);
            refs = refs.Distinct().ToList();
            if (refs.Count == 0) return $"{WorkItemFormat.Header(items[0])}: no changesets or pull requests linked to it or its tasks";
            sb.AppendLine($"{WorkItemFormat.Header(items[0])}: {refs.Count} change(s)");
        }
        else throw new ArgumentException("give a change (523, PR 482, commit sha with repo) or a work item id");

        foreach (var r in refs.Take(8))
        {
            sb.AppendLine(await ReviewOneAsync(session, r, repo, ct));
        }
        if (refs.Count > 8) sb.AppendLine($"… {refs.Count - 8} more changes not checked");
        return sb.ToString().TrimEnd();
    }

    static async Task<string> ReviewOneAsync(Session session, string change, string? repo, CancellationToken ct)
    {
        var (c, versions) = await TraceQueries.ResolveAsync(session, change, repo, ct);
        var client = session.Client;
        var sb = new StringBuilder($"{c.Label}: {c.Files.Count} file(s)\n");
        var issues = 0;

        // 1. Work item.
        List<int> linked = [];
        try
        {
            linked = c.Kind switch
            {
                "changeset" => (await client.GetListAsync($"_apis/tfvc/changesets/{c.Id}/workItems", ct)).Select(w => w.TryGetProperty("id", out var i) ? i.GetInt32() : 0).ToList(),
                "pr" => (await client.GetListAsync($"_apis/git/repositories/{c.RepoId}/pullRequests/{c.Id}/workitems", ct)).Select(w => int.TryParse(Json.Str(w, "id"), out var i) ? i : 0).ToList(),
                _ => WorkQueries.ParseIds(Regex.Match(c.Message ?? "", @"#\d+").Value),
            };
        }
        catch (AdoApiException) { }
        linked = linked.Where(i => i > 0).Distinct().ToList();
        if (linked.Count == 0) { sb.AppendLine("- CHECK no work item linked"); issues++; }

        // 2. Labels: added in label files or used in code, and present in every language of their label file.
        var labelFiles = c.Files.Where(f => f.Path.Contains("/AxLabelFile/LabelResources/", StringComparison.OrdinalIgnoreCase) && !f.ChangeType.Contains("delete")).ToList();
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); // label file id -> label ids
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);           // label file id -> LabelResources folder
        foreach (var f in labelFiles)
        {
            var (before, after) = await versions(f);
            var name = f.Path[(f.Path.LastIndexOf('/') + 1)..];
            var fileId = name.Split('.')[0];
            folders[fileId] = f.Path[..(f.Path.IndexOf("/LabelResources/", StringComparison.OrdinalIgnoreCase) + "/LabelResources".Length)];
            var old = before is null ? [] : Labels.Parse(before);
            foreach (var (lid, text) in after is null ? [] : Labels.Parse(after))
                if (!old.TryGetValue(lid, out var was) || was != text) Add(wanted, fileId, lid);
        }
        var hardcoded = new List<string>();
        var todos = new List<string>();
        foreach (var f in c.Files.Where(f => AotDiff.IsAot(f.Path) && !f.ChangeType.Contains("delete")).Take(40))
        {
            var (before, after) = await versions(f);
            if (after is null) continue;
            var addedLines = AddedLines(before, after);
            foreach (var (method, line) in addedLines)
            {
                foreach (Match m in LabelRef().Matches(line)) Add(wanted, m.Groups["file"].Value, m.Groups["id"].Value);
                if (Hardcoded().Match(line) is { Success: true } h) hardcoded.Add($"{AotDiff.ObjectName(f.Path)}.{method}: {h.Groups["call"].Value}(\"{Formatting.Collapse(h.Groups["text"].Value, 50)}\")");
                if (Todo().IsMatch(line)) todos.Add($"{AotDiff.ObjectName(f.Path)}.{method}: {Formatting.Collapse(line.Trim(), 70)}");
            }
            // Labels named in the metadata (Label, HelpText of fields and controls) of the new version.
            foreach (Match m in LabelRef().Matches(StripSource(after) ?? ""))
                if (before is null || !(StripSource(before) ?? "").Contains(m.Value, StringComparison.Ordinal)) Add(wanted, m.Groups["file"].Value, m.Groups["id"].Value);
        }

        var missing = new List<string>();
        foreach (var (fileId, ids) in wanted)
        {
            if (fileId.Equals("SYS", StringComparison.OrdinalIgnoreCase) || ids.Count == 0) continue;
            var folder = folders.GetValueOrDefault(fileId) ?? await FindFolderAsync(session, c, fileId, ct);
            if (folder is null) continue; // a label file of another model (Microsoft's): not checked
            var languages = await LanguageFilesAsync(session, c, folder, fileId, ct);
            foreach (var (lang, path) in languages)
            {
                var text = await ReadAtAsync(session, c, path, ct);
                var map = text is null ? [] : Labels.Parse(text);
                var absent = ids.Where(i => !map.ContainsKey(i)).ToList();
                if (absent.Count > 0) missing.Add($"{lang}: {string.Join(", ", absent.Take(8).Select(i => $"@{fileId}:{i}"))}{(absent.Count > 8 ? $" (+{absent.Count - 8})" : "")}");
            }
        }
        if (missing.Count > 0) { sb.AppendLine("- CHECK labels missing in a language:"); foreach (var m in missing) sb.AppendLine("  " + m); issues++; }
        if (hardcoded.Count > 0) { sb.AppendLine("- CHECK messages written in code instead of labels (not translated):"); foreach (var h in hardcoded.Take(10)) sb.AppendLine("  " + h); issues++; }
        if (todos.Count > 0) { sb.AppendLine("- CHECK TODO left:"); foreach (var t in todos.Take(10)) sb.AppendLine("  " + t); issues++; }
        if (issues == 0) sb.AppendLine($"- OK: work item {string.Join(", ", linked.Select(i => "#" + i))}, labels in all languages, messages from labels, no TODO");
        return sb.ToString().TrimEnd();
    }

    static void Add(Dictionary<string, HashSet<string>> map, string file, string id)
    {
        if (!map.TryGetValue(file, out var set)) map[file] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(id);
    }

    /// <summary>Lines of X++ that are new in <paramref name="after"/>, with the method they are in.</summary>
    internal static List<(string Method, string Line)> AddedLines(string? before, string after)
    {
        var a = Parse(before);
        var b = Parse(after);
        var oldMethods = AotDiff.Methods(a);
        var result = new List<(string, string)>();
        foreach (var (name, source) in AotDiff.Methods(b))
        {
            var was = oldMethods.GetValueOrDefault(name);
            if (was == source) continue;
            if (was is null) result.AddRange(Diff.Split(source).Select(l => (name, l)));
            else result.AddRange(Diff.Lines(Diff.Split(was), Diff.Split(source)).Where(x => x.Op == Diff.Op.Add).Select(x => (name, x.Line)));
        }
        return result;
    }

    static XDocument? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
    }

    static string? StripSource(string? xml) => Parse(xml) is { } d ? AotDiff.Metadata(d) : null;

    /// <summary>The LabelResources folder of a label file id on the change's branch (TFVC) or repository (Git).</summary>
    static async Task<string?> FindFolderAsync(Session session, CodeChange c, string fileId, CancellationToken ct)
    {
        // The model folder of a changed AOT file: …/Metadata/<Package>/<Model>/Ax… → …/Metadata/<Package>/<Model>/AxLabelFile/LabelResources
        foreach (var f in c.Files.Where(f => AotDiff.IsAot(f.Path)))
        {
            var p = f.Path;
            var ax = p.LastIndexOf("/Ax", StringComparison.Ordinal);
            if (ax <= 0) continue;
            var folder = p[..ax] + "/AxLabelFile/LabelResources";
            var files = await ListAsync(session, c, folder, ct);
            if (files.Any(x => x[(x.LastIndexOf('/') + 1)..].StartsWith(fileId + ".", StringComparison.OrdinalIgnoreCase))) return folder;
        }
        return null;
    }

    static async Task<List<(string Language, string Path)>> LanguageFilesAsync(Session session, CodeChange c, string folder, string fileId, CancellationToken ct) =>
        (await ListAsync(session, c, folder, ct))
            .Where(p => p.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase) && p[(p.LastIndexOf('/') + 1)..].StartsWith(fileId + ".", StringComparison.OrdinalIgnoreCase))
            .Select(p => (p[(p.LastIndexOf('/') + 1)..^".label.txt".Length][(fileId.Length + 1)..], p))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();

    static async Task<List<string>> ListAsync(Session session, CodeChange c, string folder, CancellationToken ct)
    {
        try
        {
            var version = c.HeadCommit ?? c.LandedCommit ?? (c.Kind == "commit" ? c.Id : null);
            var url = c.Kind == "changeset"
                ? $"_apis/tfvc/items?scopePath={Uri.EscapeDataString(folder)}&recursionLevel=Full&versionDescriptor.version={c.Id}&versionDescriptor.versionType=changeset"
                : $"_apis/git/repositories/{c.RepoId}/items?scopePath={Uri.EscapeDataString(folder)}&recursionLevel=Full" +
                  (version is null ? "" : $"&versionDescriptor.version={version}&versionDescriptor.versionType=commit");
            return (await session.Client.GetListAsync(url, ct)).Select(i => Json.Str(i, "path")).Where(p => p is not null).Select(p => p!).ToList();
        }
        catch (AdoApiException) { return []; }
    }

    static Task<string?> ReadAtAsync(Session session, CodeChange c, string path, CancellationToken ct) =>
        c.Kind == "changeset"
            ? session.Code.TfvcFileAsync(path, int.Parse(c.Id, CultureInfo.InvariantCulture), ct)
            : session.Code.GitFileAsync(c.RepoId!, path, c.HeadCommit ?? c.LandedCommit ?? c.Id, ct);
}
