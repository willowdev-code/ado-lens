using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdoLens;

/// <summary>"How does it work and why": one search over work items, wiki and code; a file; the history of a file.</summary>
public static partial class Knowledge
{
    const int MaxFileLines = 300;
    static readonly string[] Scopes = ["work", "wiki", "code"];

    [GeneratedRegex(@"</?highlighthit>")]
    private static partial Regex Highlight();

    /// <summary>One search over work items, wiki pages and code of a project, grouped; each part fails on its own.</summary>
    public static async Task<string> SearchAsync(Lens lens, string? connection, string? project, string text, string? scope, int top, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
        var session = lens.For(connection);
        var name = session.Project(project);
        var wanted = (scope ?? "work;wiki;code").Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
        if (wanted.Except(Scopes).FirstOrDefault() is { } bad) throw new ArgumentException($"scope '{bad}': use work, wiki, code (several with ';')");
        top = Math.Clamp(top, 1, 50);

        var parts = new List<(string Scope, Task<string> Task)>();
        if (wanted.Contains("work")) parts.Add(("work", Part(() => WorkItemsAsync(session, name, text, top, ct))));
        if (wanted.Contains("wiki")) parts.Add(("wiki", Part(() => WikiAsync(session.Client, name, text, top, ct))));
        if (wanted.Contains("code")) parts.Add(("code", Part(() => CodeAsync(session.Client, name, text, top, null, ct))));
        var sb = new StringBuilder($"search '{text}' in {name}\n");
        foreach (var (_, task) in parts) sb.AppendLine(await task);
        return sb.ToString().TrimEnd();
    }

    static async Task<string> Part(Func<Task<string>> f)
    {
        try { return await f(); }
        catch (AdoApiException ex) { return $"({ex.Message})"; }
    }

    static Dictionary<string, object> SearchBody(string text, int top, string project, Dictionary<string, string[]>? more = null, string projectFilter = "Project")
    {
        var filters = new Dictionary<string, string[]> { [projectFilter] = [project] };
        if (more is not null) foreach (var (k, v) in more) filters[k] = v;
        return new Dictionary<string, object> { ["searchText"] = text, ["$top"] = top, ["$skip"] = 0, ["includeFacets"] = false, ["filters"] = filters };
    }

    static string Count(int shown, int total) => total > shown ? $"{shown} of {total}" : shown.ToString(CultureInfo.InvariantCulture);

    static int Total(JsonElement root, int fallback) => root.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : fallback;

    static async Task<string> WorkItemsAsync(Session session, string project, string text, int top, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            root = await session.Client.PostAsync(session.Client.ServiceUrl("almsearch") + $"{Uri.EscapeDataString(project)}/_apis/search/workitemsearchresults",
                SearchBody(text, top, project, projectFilter: "System.TeamProject"), ct);
        }
        catch (AdoApiException ex)
        {
            if (Environment.GetEnvironmentVariable("ADOLENS_VERBOSE") == "1") Console.Error.WriteLine($"work item search: {ex.Message}");
            // Without the search service: titles only, through WIQL.
            var safe = text.Replace("'", "''");
            var (ids, _) = await session.Work.WiqlAsync(project,
                $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Title] CONTAINS '{safe}' ORDER BY [System.ChangedDate] DESC", top, ct);
            var items = await session.Work.GetAsync(ids, relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title"], ct);
            return items.Count == 0 ? "work items: none (titles searched)"
                : $"work items (titles): {items.Count}\n" + string.Join('\n', items.Select(i => "- " + WorkItemFormat.Header(i)));
        }
        var results = Json.Arr(root, "results").ToList();
        if (results.Count == 0) return "work items: none";
        Snapshot? s = null;
        try { s = (await session.Meta.GetAsync(project, ct)).Snapshot; }
        catch (AdoApiException) { }
        string FieldName(string reference) =>
            s?.Fields.Values.FirstOrDefault(f => f.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase))?.Name ?? reference.Split('.')[^1];
        var sb = new StringBuilder($"work items: {Count(results.Count, Total(root, results.Count))}\n");
        foreach (var r in results)
        {
            var f = Json.Obj(r, "fields");
            sb.Append($"- #{Json.Str(f, "system.id")} {Json.Str(f, "system.workitemtype")} [{Json.Str(f, "system.state")}] {Json.Str(f, "system.title")}");
            // A hit outside the title says why it matched.
            var hit = Json.Arr(r, "hits").FirstOrDefault(h => Json.Str(h, "fieldReferenceName") is { } n && !n.Equals("system.title", StringComparison.OrdinalIgnoreCase));
            if (hit.ValueKind == JsonValueKind.Object && Json.Arr(hit, "highlights").Select(h => h.GetString()).FirstOrDefault() is { } snippet)
                sb.Append($" | {Json.Str(hit, "fieldName") ?? FieldName(Json.Str(hit, "fieldReferenceName")!)}: {Formatting.Collapse(Highlight().Replace(snippet, ""), 120)}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    static async Task<string> WikiAsync(AdoClient client, string project, string text, int top, CancellationToken ct)
    {
        var root = await client.PostAsync(client.ServiceUrl("almsearch") + $"{Uri.EscapeDataString(project)}/_apis/search/wikisearchresults", SearchBody(text, top, project), ct);
        var results = Json.Arr(root, "results").ToList();
        if (results.Count == 0) return "wiki: none";
        var sb = new StringBuilder($"wiki: {Count(results.Count, Total(root, results.Count))}\n");
        foreach (var r in results)
        {
            var path = WikiQueries.PagePath(Json.Str(r, "path") ?? Json.Str(r, "fileName") ?? "");
            var snippet = Json.Arr(r, "hits").SelectMany(h => Json.Arr(h, "highlights")).Select(h => h.GetString() ?? "").FirstOrDefault();
            sb.Append($"- {path}");
            if (snippet is not null) sb.Append(": ").Append(Formatting.Collapse(Highlight().Replace(snippet, ""), 140));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    internal sealed record CodeHit(string Repo, string Path, string? Branch, int Matches, bool Tfvc);

    internal static async Task<(List<CodeHit> Hits, int Total)> CodeHitsAsync(AdoClient client, string project, string text, int top, string? repo, CancellationToken ct)
    {
        var more = repo is null ? null : new Dictionary<string, string[]> { ["Repository"] = [repo] };
        var root = await client.PostAsync(client.ServiceUrl("almsearch") + $"{Uri.EscapeDataString(project)}/_apis/search/codesearchresults", SearchBody(text, top, project, more), ct);
        var hits = Json.Arr(root, "results").Select(r =>
        {
            var path = Json.Str(r, "path") ?? Json.Str(r, "fileName") ?? "";
            var matches = Json.Obj(r, "matches");
            var count = matches.ValueKind == JsonValueKind.Object ? matches.EnumerateObject().Sum(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.GetArrayLength() : 0) : 0;
            var branch = Json.Arr(r, "versions").Select(v => Json.Str(v, "branchName")).FirstOrDefault(b => b is not null);
            return new CodeHit(Json.Str(Json.Obj(r, "repository"), "name") ?? "", path, branch, count, path.StartsWith("$/", StringComparison.Ordinal));
        }).ToList();
        return (hits, Total(root, hits.Count));
    }

    static async Task<string> CodeAsync(AdoClient client, string project, string text, int top, string? repo, CancellationToken ct)
    {
        var (hits, total) = await CodeHitsAsync(client, project, text, top, repo, ct);
        if (hits.Count == 0) return "code: none";
        var sb = new StringBuilder($"code: {Count(hits.Count, total)}\n");
        foreach (var h in hits)
        {
            var label = AotDiff.IsAot(h.Path) ? $"{AotDiff.ObjectName(h.Path)} - " : "";
            sb.AppendLine($"- {label}{(h.Tfvc ? h.Path : $"{h.Repo}: {h.Path}")}{(h.Matches > 1 ? $" ({h.Matches} matches)" : "")}");
        }
        return sb.Append("ado_file reads a file or its history.").ToString();
    }

    // ---- files ---------------------------------------------------------------------------------------------------------

    sealed record FileRef(bool Tfvc, string Path, string? RepoId, string? RepoName, string Project);

    /// <summary>File lists already read in this process: TFVC folder or Git repository → its file paths.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> Listings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files whose path ends with <paramref name="path"/> (a name with or without ".xml"), from the file lists of the
    /// TFVC Metadata folders (found under $/project, a few levels deep) and of up to five Git repositories; and an
    /// example full path to suggest when nothing matches.
    /// </summary>
    static async Task<(List<CodeHit> Hits, string? Hint)> ListingAsync(Session session, string project, string path, string? repo, CancellationToken ct)
    {
        var client = session.Client;
        var tail = "/" + path.TrimStart('/');
        bool Match(string p) => p.EndsWith(tail, StringComparison.OrdinalIgnoreCase) || p.EndsWith(tail + ".xml", StringComparison.OrdinalIgnoreCase);
        var hits = new List<CodeHit>();
        string? hint = null;

        // TFVC: the Metadata folders of the branches ($/Project/Trunk/Main/Metadata, $/Project/Release/Metadata, ...).
        var metadata = new List<string>();
        // The project's own TFVC first; code kept in another project of the organization is found from $/.
        foreach (var start in new[] { $"$/{project}", "$/" })
        {
            if (metadata.Count > 0) break;
        try
        {
            var level = new List<string> { start };
            for (var depth = 0; depth < (start == "$/" ? 5 : 4) && level.Count > 0 && metadata.Count < 6; depth++)
            {
                var next = new List<string>();
                foreach (var folder in level.Take(15))
                {
                    var children = (await client.GetListAsync($"_apis/tfvc/items?scopePath={Uri.EscapeDataString(folder)}&recursionLevel=OneLevel", ct))
                        .Where(i => Json.Bool(i, "isFolder")).Select(i => Json.Str(i, "path") ?? "").Where(p => p.Length > 0 && !p.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var c in children)
                        if (c.EndsWith("/Metadata", StringComparison.OrdinalIgnoreCase)) metadata.Add(c);
                        else next.Add(c);
                }
                level = next;
            }
        }
        catch (AdoApiException) { }
        }
        foreach (var root in metadata.Take(6))
        {
            try
            {
                if (!Listings.TryGetValue(root, out var files))
                    Listings[root] = files = (await client.GetListAsync($"_apis/tfvc/items?scopePath={Uri.EscapeDataString(root)}&recursionLevel=Full", ct))
                        .Where(i => !Json.Bool(i, "isFolder")).Select(i => Json.Str(i, "path") ?? "").ToList();
                hits.AddRange(files.Where(Match).Select(p => new CodeHit("", p, null, 1, true)));
            }
            catch (AdoApiException) { }
        }
        if (metadata.Count > 0) hint = $"{metadata[0]}/<Model>/<Model>/AxClass/{path.Trim('/')}{(path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? "" : ".xml")}";

        // Git: the repositories of the project (or the one given).
        try
        {
            var repos = repo is not null ? [Json.Str(await client.GetAsync($"{AdoClient.Scope(project)}_apis/git/repositories/{Uri.EscapeDataString(repo)}", ct), "name") ?? repo]
                : (await client.GetListAsync($"{AdoClient.Scope(project)}_apis/git/repositories", ct)).Select(r => Json.Str(r, "name") ?? "").Where(n => n.Length > 0).Take(5).ToList();
            foreach (var r in repos)
            {
                if (!Listings.TryGetValue($"git:{project}/{r}", out var files))
                    Listings[$"git:{project}/{r}"] = files = (await client.GetListAsync($"{AdoClient.Scope(project)}_apis/git/repositories/{Uri.EscapeDataString(r)}/items?scopePath=/&recursionLevel=Full", ct))
                        .Where(i => !Json.Bool(i, "isFolder")).Select(i => Json.Str(i, "path") ?? "").ToList();
                hits.AddRange(files.Where(Match).Select(p => new CodeHit(r, p, null, 1, false)));
                hint ??= $"repo={r} path=/<path>/{path.Trim('/')}";
            }
        }
        catch (AdoApiException) { }
        return (hits.Take(20).ToList(), hint);
    }

    /// <summary>
    /// A TFVC path ($/…), a Git path with its repository, or only a file name: then Code Search finds it (several
    /// matches are listed for the caller to choose).
    /// </summary>
    static async Task<(FileRef? File, string? Choices)> ResolveAsync(Session session, string project, string path, string? repo, CancellationToken ct)
    {
        path = path.Trim().Replace('\\', '/');
        if (path.StartsWith("$/", StringComparison.Ordinal)) return (new FileRef(true, path, null, null, project), null);
        // The whole repository ("/") or a folder ("src/"): no file search, the repository is needed.
        if (path == "/" || path.EndsWith('/'))
        {
            if (repo is not null)
            {
                var r = await session.Code.RepoAsync(repo, project, ct);
                return (new FileRef(false, "/" + path.Trim('/'), r.Id, r.Name, r.Project ?? project), null);
            }
            var all = await session.Client.GetListAsync($"{AdoClient.Scope(project)}_apis/git/repositories", ct);
            if (all.Count == 1) return (new FileRef(false, "/" + path.Trim('/'), Json.Str(all[0], "id"), Json.Str(all[0], "name"), project), null);
            return (null, $"{path} is a folder: give repo=<name> ({string.Join(", ", all.Select(x => Json.Str(x, "name")).Take(30))})");
        }
        if (repo is not null && path.Contains('/'))
        {
            var r = await session.Code.RepoAsync(repo, project, ct);
            return (new FileRef(false, "/" + path.TrimStart('/'), r.Id, r.Name, r.Project ?? project), null);
        }
        if (repo is null && path.Contains('/'))
        {
            var repos = await session.Client.GetListAsync($"{AdoClient.Scope(project)}_apis/git/repositories", ct);
            if (repos.Count == 1)
                return (new FileRef(false, "/" + path.TrimStart('/'), Json.Str(repos[0], "id"), Json.Str(repos[0], "name"), project), null);
        }
        var name = path[(path.LastIndexOf('/') + 1)..];
        List<CodeHit> hits;
        try
        {
            // ADOLENS_NO_CODE_SEARCH=1 tries the way without Code Search (diagnostics).
            if (Environment.GetEnvironmentVariable("ADOLENS_NO_CODE_SEARCH") == "1") throw new AdoApiException("Code Search turned off by ADOLENS_NO_CODE_SEARCH");
            (hits, _) = await CodeHitsAsync(session.Client, project, $"file:{name}", 20, repo, ct);
        }
        catch (AdoApiException ex)
        {
            // Without Code Search (not installed, or failing): the file lists of the TFVC Metadata folders and of the
            // Git repositories, read once per session.
            var (listed, hint) = await ListingAsync(session, project, path, repo, ct);
            if (listed.Count == 0)
                throw new InvalidOperationException($"Code Search failed ({Formatting.Collapse(ex.Message, 120)}) and no file '{path}' is in the file lists either; " +
                                                    $"give the full path{(hint is null ? " ($/… for TFVC, or the path with repo= for Git)" : $", e.g. {hint}")}");
            hits = listed;
        }
        var wanted = path.StartsWith('/') ? path : "/" + path;
        // An X++ object may be named without ".xml".
        var matching = hits.Where(h => h.Path.EndsWith(wanted, StringComparison.OrdinalIgnoreCase) || h.Path.EndsWith(wanted + ".xml", StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 1)
        {
            var h = matching[0];
            if (h.Tfvc) return (new FileRef(true, h.Path, null, null, project), null);
            var r = await session.Code.RepoAsync(h.Repo, project, ct);
            return (new FileRef(false, h.Path, r.Id, r.Name, r.Project ?? project), null);
        }
        if (matching.Count == 0) return (null, $"no file '{path}' found in {project}");
        return (null, $"{matching.Count} files match '{path}' - pass the full path (and repo for Git):\n" +
                      string.Join('\n', matching.Select(h => h.Tfvc ? $"- {h.Path}" : $"- {h.Path} (repo {h.Repo})")));
    }

    /// <summary>A file at a version (default: latest): text, or for X++ objects their methods; part= one method.</summary>
    public static async Task<string> FileAsync(Lens lens, string? connection, string? project, string path, string? repo, string? version,
        string? part, bool history, int top, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var (file, choices) = await ResolveAsync(session, name, path, repo, ct);
        if (file is null) return choices!;
        // An environment name ("sbx03", "UAT") instead of a branch: the branch of the environment map.
        if (version is not null && session.Config.Environments.FirstOrDefault(e => !e.Key.Contains('/') &&
                (e.Value.Equals(version, StringComparison.OrdinalIgnoreCase) || e.Value.Contains(version, StringComparison.OrdinalIgnoreCase))) is { Key: { } branch })
            version = branch;
        if (history) return await HistoryAsync(session, file, version, top, ct);
        if (path.Trim() == "/" || path.TrimEnd().EndsWith('/')) return $"{path} is a folder: history=true lists its changes";

        string? text;
        string where;
        if (file.Tfvc)
        {
            var v = version is null ? "" : $"&versionDescriptor.version={Uri.EscapeDataString(version.TrimStart('C', 'c'))}&versionDescriptor.versionType=changeset";
            text = await Content(session.Client, $"_apis/tfvc/items?path={Uri.EscapeDataString(file.Path)}&includeContent=true{v}", ct);
            where = file.Path + (version is null ? "" : $" @ changeset {version}");
        }
        else
        {
            var v = version is null ? "" : version.Length >= 7 && version.All(Uri.IsHexDigit)
                ? $"&versionDescriptor.version={version}&versionDescriptor.versionType=commit"
                : $"&versionDescriptor.version={Uri.EscapeDataString(version)}&versionDescriptor.versionType=branch";
            text = await Content(session.Client, $"_apis/git/repositories/{file.RepoId}/items?path={Uri.EscapeDataString(file.Path)}&includeContent=true{v}", ct);
            where = $"{file.RepoName}: {file.Path} @ " + (version ?? $"{await DefaultBranchAsync(session.Client, file.RepoId!, ct)} (default branch; version=<branch> for another)");
        }
        if (text is null) return $"no file {where}";
        return Show(where, file.Path, text, part, detail == "full");
    }

    /// <summary>The default branch of a repository without "refs/heads/", or "the default branch" when unknown.</summary>
    static async Task<string> DefaultBranchAsync(AdoClient client, string repoId, CancellationToken ct)
    {
        try { return Json.Str(await client.GetAsync($"_apis/git/repositories/{repoId}", ct), "defaultBranch")?.Replace("refs/heads/", "") ?? "the default branch"; }
        catch (AdoApiException) { return "the default branch"; }
    }

    internal static async Task<string?> Content(AdoClient client, string url, CancellationToken ct)
    {
        try { return Json.Str(await client.GetAsync(url, ct), "content"); }
        catch (AdoApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { return null; }
    }

    internal static string Show(string where, string path, string text, string? part, bool full)
    {
        var sb = new StringBuilder(where).AppendLine();
        if (AotDiff.IsAot(path) && TryParse(text) is { } doc)
        {
            var methods = AotDiff.Methods(doc);
            if (part is not null)
            {
                var hit = methods.Where(m => m.Key.Equals(part, StringComparison.OrdinalIgnoreCase) || m.Key.EndsWith("." + part, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count == 0) return sb.Append($"no method '{part}' in {AotDiff.ObjectName(path)}; methods: {string.Join(", ", methods.Keys)}").ToString();
                foreach (var (n, src) in hit) sb.AppendLine($"--- {n}").AppendLine(src.Trim());
                return sb.ToString().TrimEnd();
            }
            if (!full && AotDiff.Skeleton(doc) is { Length: > 0 } skeleton) sb.AppendLine(skeleton);
            sb.AppendLine($"{AotDiff.ObjectName(path)}: {methods.Count} methods (part=<method> shows one)");
            foreach (var (n, src) in methods) sb.AppendLine($"- {n} ({Diff.Split(src.Trim()).Length} lines)");
            if (full) sb.AppendLine().AppendLine(AotDiff.Metadata(doc));
            return sb.ToString().TrimEnd();
        }
        var lines = Diff.Split(text);
        if (full || lines.Length <= MaxFileLines) return sb.Append(text.TrimEnd()).ToString();
        return sb.AppendJoin('\n', lines.Take(MaxFileLines)).Append($"\n… {lines.Length - MaxFileLines} more lines (detail=full: whole file)").ToString();
    }

    static XDocument? TryParse(string xml)
    {
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
    }

    [GeneratedRegex(@"Merged PR (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MergedPr();

    [GeneratedRegex(@"(?:AB)?#(\d{2,7})\b")]
    private static partial Regex ItemRef();

    sealed record Change(DateTime? Date, string? Author, string Label, string? Message, List<int> Items);

    /// <summary>Who changed a file, when, and for which work items: changesets (TFVC) or commits with their pull requests (Git).</summary>
    static async Task<string> HistoryAsync(Session session, FileRef file, string? branch, int top, CancellationToken ct)
    {
        top = Math.Clamp(top, 1, 50);
        var client = session.Client;
        var changes = new List<Change>();
        if (file.Tfvc)
        {
            var list = await client.GetListAsync($"_apis/tfvc/changesets?searchCriteria.itemPath={Uri.EscapeDataString(file.Path)}&$top={top}&maxCommentLength=200", ct);
            var items = await Task.WhenAll(list.Select(async c =>
            {
                var id = c.TryGetProperty("changesetId", out var v) ? v.GetInt32() : 0;
                try { return (await client.GetListAsync($"_apis/tfvc/changesets/{id}/workItems", ct)).Select(w => w.TryGetProperty("id", out var i) ? i.GetInt32() : 0).Where(i => i > 0).ToList(); }
                catch (AdoApiException) { return new List<int>(); }
            }));
            changes.AddRange(list.Select((c, i) => new Change(Date(Json.Str(c, "createdDate")), Json.Str(Json.Obj(c, "author"), "displayName"),
                $"changeset {(c.TryGetProperty("changesetId", out var v) ? v.GetInt32() : 0)}", Json.Str(c, "comment"), items[i])));
        }
        else
        {
            var b = branch is null ? "" : $"&searchCriteria.itemVersion.version={Uri.EscapeDataString(branch)}&searchCriteria.itemVersion.versionType=branch";
            var list = await client.GetListAsync($"_apis/git/repositories/{file.RepoId}/commits?searchCriteria.itemPath={Uri.EscapeDataString(file.Path)}&searchCriteria.$top={top}&searchCriteria.historyMode=fullHistorySimplifyMerges{b}", ct);
            // The pull request of each commit: "Merged PR n" in a merge commit, else the PR that brought the commit in.
            var prOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in list)
                if (MergedPr().Match(Json.Str(c, "comment") ?? "") is { Success: true } m) prOf[Json.Str(c, "commitId") ?? ""] = m.Groups[1].Value;
            var other = list.Select(c => Json.Str(c, "commitId")).Where(x => x is not null && !prOf.ContainsKey(x)).ToList();
            if (other.Count > 0)
            {
                try
                {
                    var found = await client.PostAsync($"_apis/git/repositories/{file.RepoId}/pullrequestquery",
                        new { queries = new[] { new { type = "commit", items = other } } }, ct);
                    foreach (var r in Json.Arr(found, "results"))
                        foreach (var p in r.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } pr
                                && pr.TryGetProperty("pullRequestId", out var id))
                                prOf[p.Name] = id.GetInt32().ToString(CultureInfo.InvariantCulture);
                }
                catch (AdoApiException) { }
            }
            var prItems = new Dictionary<string, List<int>>();
            foreach (var pr in prOf.Values.Distinct())
            {
                try
                {
                    var linked = await client.GetListAsync($"_apis/git/repositories/{file.RepoId}/pullRequests/{pr}/workitems", ct);
                    prItems[pr] = linked.Select(w => int.TryParse(Json.Str(w, "id"), out var i) ? i : 0).Where(i => i > 0).ToList();
                }
                catch (AdoApiException) { prItems[pr] = []; }
            }
            var items = list.Select(c =>
            {
                var ids = ItemRef().Matches(Json.Str(c, "comment") ?? "").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                if (prOf.TryGetValue(Json.Str(c, "commitId") ?? "", out var pr)) ids.AddRange(prItems.GetValueOrDefault(pr, []));
                return ids.Distinct().ToList();
            }).ToArray();
            changes.AddRange(list.Select((c, i) =>
            {
                var message = Json.Str(c, "comment");
                var shaKey = Json.Str(c, "commitId") ?? "";
                var pr = MergedPr().IsMatch(message ?? "") && prOf.TryGetValue(shaKey, out var n) ? $"PR {n}"
                    : prOf.TryGetValue(shaKey, out var via) ? $"commit {shaKey} (PR {via})" : null;
                var sha = Json.Str(c, "commitId") ?? "";
                return new Change(Date(Json.Str(Json.Obj(c, "author"), "date")), Json.Str(Json.Obj(c, "author"), "name"),
                    pr ?? $"commit {sha}", message, items[i]);
            }));
        }
        var where = file.Tfvc ? file.Path : $"{file.RepoName}: {file.Path} ({branch ?? await DefaultBranchAsync(client, file.RepoId!, ct)})";
        if (changes.Count == 0) return $"no history for {where}";

        var all = changes.SelectMany(c => c.Items).Distinct().ToList();
        var headers = all.Count == 0 ? [] : (await session.Work.GetAsync(all, relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title"], ct))
            .ToDictionary(WorkItems.Id, w => $"#{WorkItems.Id(w)} {Formatting.Collapse(WorkItems.Field(w, "System.Title") ?? "", 60)} ({WorkItems.Field(w, "System.State")})");
        var sb = new StringBuilder($"history of {where}: {changes.Count} changes, newest first\n");
        foreach (var c in changes)
        {
            sb.Append($"- {c.Date:yyyy-MM-dd} {c.Author} - {c.Label}");
            var msg = c.Message is null ? "" : MergedPr().Replace(c.Message, "").TrimStart(':', ' ');
            if (msg.Length > 0) sb.Append($": {Formatting.Collapse(msg, 80)}");
            var known = c.Items.Where(headers.ContainsKey).Select(i => headers[i]).ToList();
            if (known.Count > 0) sb.Append(" -> ").AppendJoin("; ", known);
            sb.AppendLine();
        }
        var none = changes.Count(c => !c.Items.Any(headers.ContainsKey));
        if (none > 0) sb.Append($"{none} of {changes.Count} changes have no work item");
        return sb.ToString().TrimEnd();
    }

    static DateTime? Date(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
