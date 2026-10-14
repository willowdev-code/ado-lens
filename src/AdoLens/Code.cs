using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AdoLens;

/// <summary>A changed file: its path, how it changed, and where to read the two versions.</summary>
public sealed record FileChange(string Path, string ChangeType, string? OldPath);

/// <summary>A change of code linked to work: a TFVC changeset, a Git commit or a pull request.</summary>
public sealed partial record CodeChange(string Kind, string Id, string? Repo, string? RepoId, string? Author, DateTime? Date, string? Message,
    string? Branch, string? State, string? TargetBranch, string? MergeCommit, List<FileChange> Files)
{
    public string Label => Kind switch
    {
        "changeset" => $"changeset {Id}",
        "pr" => $"PR {Id}",
        _ => $"commit {(Id.Length > 7 ? Id[..7] : Id)}",
    } + (Repo is null ? "" : $" ({Repo})");
}

/// <summary>Latest successful deployment of one stage of a release definition, with what it deployed.</summary>
public sealed record Deployment(string Definition, string Stage, DateTime? When, string? Branch, string? SourceVersion, string? BuildNumber, string? Repository);

/// <summary>Version control (TFVC and Git), builds and releases.</summary>
public sealed class Code(AdoClient client)
{
    const int MaxChanges = 2000;
    readonly ConcurrentDictionary<string, List<string>> _tfvcBranches = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (string Id, string Name, string? Project)> _repos = new(StringComparer.OrdinalIgnoreCase);

    static DateTime? Date(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    public async Task<CodeChange> ChangesetAsync(int id, CancellationToken ct)
    {
        var csTask = client.GetAsync($"_apis/tfvc/changesets/{id}?maxCommentLength=500", ct);
        var files = new List<FileChange>();
        while (files.Count < MaxChanges)
        {
            var page = await client.GetListAsync($"_apis/tfvc/changesets/{id}/changes?$skip={files.Count}&$top=1000", ct);
            if (page.Count == 0) break;
            files.AddRange(page.Select(c => new FileChange(Json.Str(Json.Obj(c, "item"), "path") ?? "", Json.Str(c, "changeType") ?? "",
                Json.Arr(c, "mergeSources").Select(m => Json.Str(m, "serverItem")).FirstOrDefault())));
        }
        var cs = await csTask;
        return new CodeChange("changeset", id.ToString(CultureInfo.InvariantCulture), null, null, Json.Str(Json.Obj(cs, "author"), "displayName"),
            Date(Json.Str(cs, "createdDate")), Json.Str(cs, "comment"), null, null, null, null, files)
        {
            // $/Project/Branch/... : the project is the first part of every server path.
            Project = files.Select(f => f.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)).FirstOrDefault(p => p is not null),
        };
    }

    /// <summary>TFVC branch roots of the project; a path belongs to the longest root it starts with.</summary>
    public async Task<string?> TfvcBranchAsync(string path, CancellationToken ct)
    {
        var project = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
        if (!_tfvcBranches.TryGetValue(project, out var roots))
        {
            try
            {
                var list = await client.GetListAsync($"{AdoClient.Scope(project)}_apis/tfvc/branches?includeChildren=true", ct);
                roots = [];
                void Walk(JsonElement b)
                {
                    if (Json.Str(b, "path") is { } p) roots.Add(p);
                    foreach (var c in Json.Arr(b, "children")) Walk(c);
                }
                foreach (var b in list) Walk(b);
            }
            catch (AdoApiException) { roots = []; }
            _tfvcBranches[project] = roots;
        }
        return roots.Where(r => path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) || path.Equals(r, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length).FirstOrDefault();
    }

    public async Task<(string Id, string Name, string? Project)> RepoAsync(string idOrName, string? project, CancellationToken ct)
    {
        if (_repos.TryGetValue(idOrName, out var hit)) return hit;
        var scope = project is null ? "" : AdoClient.Scope(project);
        var repo = await client.GetAsync($"{scope}_apis/git/repositories/{Uri.EscapeDataString(idOrName)}", ct);
        var result = (Json.Str(repo, "id") ?? idOrName, Json.Str(repo, "name") ?? idOrName, Json.Str(Json.Obj(repo, "project"), "name"));
        _repos[idOrName] = result;
        _repos[result.Item1] = result;
        return result;
    }

    public async Task<CodeChange> CommitAsync(string repoId, string sha, CancellationToken ct)
    {
        var repo = await RepoAsync(repoId, null, ct);
        var c = await client.GetAsync($"_apis/git/repositories/{repo.Id}/commits/{sha}?changeCount=0", ct);
        var files = new List<FileChange>();
        while (files.Count < MaxChanges)
        {
            var page = await client.GetAsync($"_apis/git/repositories/{repo.Id}/commits/{sha}/changes?top=1000&skip={files.Count}", ct);
            var changes = Json.Arr(page, "changes").Where(x => !Json.Bool(Json.Obj(x, "item"), "isFolder")).ToList();
            if (!Json.Arr(page, "changes").Any()) break;
            files.AddRange(changes.Select(x => new FileChange(Json.Str(Json.Obj(x, "item"), "path") ?? "", Json.Str(x, "changeType") ?? "", Json.Str(x, "sourceServerItem"))));
            if (Json.Arr(page, "changes").Count() < 1000) break;
        }
        var author = Json.Obj(c, "author");
        return new CodeChange("commit", Json.Str(c, "commitId") ?? sha, repo.Name, repo.Id, Json.Str(author, "name"), Date(Json.Str(author, "date")),
            Json.Str(c, "comment"), null, null, null, null, files)
        { Parent = Json.Arr(c, "parents").Select(p => p.GetString()).FirstOrDefault(), Project = repo.Project };
    }

    public async Task<CodeChange> PullRequestAsync(string? repoId, int id, bool withFiles, CancellationToken ct)
    {
        var pr = await client.GetAsync(repoId is null ? $"_apis/git/pullrequests/{id}" : $"_apis/git/repositories/{repoId}/pullrequests/{id}", ct);
        var repo = Json.Obj(pr, "repository");
        var rid = Json.Str(repo, "id") ?? repoId ?? "";
        var files = new List<FileChange>();
        string? baseCommit = null;
        if (withFiles)
        {
            var iterations = await client.GetListAsync($"_apis/git/repositories/{rid}/pullRequests/{id}/iterations", ct);
            if (iterations.LastOrDefault() is { ValueKind: JsonValueKind.Object } it && it.TryGetProperty("id", out var itId))
            {
                baseCommit = Json.Str(Json.Obj(it, "commonRefCommit"), "commitId");
                var changes = await client.GetAsync($"_apis/git/repositories/{rid}/pullRequests/{id}/iterations/{itId.GetInt32()}/changes?$top=2000", ct);
                files.AddRange(Json.Arr(changes, "changeEntries").Where(x => !Json.Bool(Json.Obj(x, "item"), "isFolder"))
                    .Select(x => new FileChange(Json.Str(Json.Obj(x, "item"), "path") ?? "", Json.Str(x, "changeType") ?? "", Json.Str(x, "originalPath"))));
            }
        }
        var status = Json.Str(pr, "status");
        var merge = Json.Str(Json.Obj(pr, "lastMergeCommit"), "commitId");
        var head = Json.Str(Json.Obj(pr, "lastMergeSourceCommit"), "commitId");
        return new CodeChange("pr", id.ToString(CultureInfo.InvariantCulture), Json.Str(repo, "name"), rid,
            Json.Str(Json.Obj(pr, "createdBy"), "displayName"), Date(Json.Str(pr, "closedDate") ?? Json.Str(pr, "creationDate")),
            Json.Str(pr, "title"), Short(Json.Str(pr, "sourceRefName")), status == "completed" ? "completed" : status,
            Short(Json.Str(pr, "targetRefName")), status == "completed" ? merge : head, files)
        { BaseCommit = baseCommit, HeadCommit = head, Project = Json.Str(Json.Obj(repo, "project"), "name") };
    }

    public static string? Short(string? refName) => refName?.Replace("refs/heads/", "");

    /// <summary>Text of a file at a TFVC changeset; null when it does not exist there.</summary>
    public async Task<string?> TfvcFileAsync(string path, int changeset, CancellationToken ct)
    {
        if (changeset <= 0) return null;
        try
        {
            var item = await client.GetAsync($"_apis/tfvc/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={changeset}&versionDescriptor.versionType=changeset&includeContent=true", ct);
            return Json.Str(item, "content");
        }
        catch (AdoApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { return null; }
    }

    /// <summary>Text of a file at a Git commit; null when it does not exist there.</summary>
    public async Task<string?> GitFileAsync(string repoId, string path, string? commit, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(commit)) return null;
        try
        {
            var item = await client.GetAsync($"_apis/git/repositories/{repoId}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={commit}&versionDescriptor.versionType=commit&includeContent=true", ct);
            return Json.Str(item, "content");
        }
        catch (AdoApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { return null; }
    }

    /// <summary>Whether <paramref name="commit"/> is in the history of <paramref name="target"/> (a commit or branch).</summary>
    public async Task<bool?> ContainsAsync(string repoId, string commit, string target, bool targetIsBranch, CancellationToken ct)
    {
        try
        {
            var diff = await client.GetAsync($"_apis/git/repositories/{repoId}/diffs/commits?baseVersion={commit}&baseVersionType=commit" +
                $"&targetVersion={Uri.EscapeDataString(target)}&targetVersionType={(targetIsBranch ? "branch" : "commit")}&$top=1", ct);
            // The merge base equals the commit exactly when the commit is an ancestor of the target.
            return string.Equals(Json.Str(diff, "commonCommit"), commit, StringComparison.OrdinalIgnoreCase);
        }
        catch (AdoApiException) { return null; }
    }

    /// <summary>A build, or null when it is gone (builds are deleted by retention).</summary>
    public async Task<JsonElement?> BuildAsync(string project, int id, CancellationToken ct)
    {
        try { return await client.GetAsync($"{AdoClient.Scope(project)}_apis/build/builds/{id}", ct); }
        catch (AdoApiException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>The latest successful deployment of every stage of every classic release definition.</summary>
    public async Task<List<Deployment>> DeploymentsAsync(string project, CancellationToken ct)
    {
        List<JsonElement> list;
        try
        {
            list = await client.GetListAsync(client.ServiceUrl("vsrm") + $"{Uri.EscapeDataString(project)}/_apis/release/deployments?deploymentStatus=succeeded&queryOrder=descending&$top=200", ct);
        }
        catch (AdoApiException) { return []; }
        var latest = new Dictionary<(string, string), Deployment>();
        foreach (var d in list)
        {
            var def = Json.Str(Json.Obj(d, "releaseDefinition"), "name") ?? "";
            var stage = Json.Str(Json.Obj(d, "releaseEnvironment"), "name") ?? "";
            if (latest.ContainsKey((def, stage))) continue;
            var artifact = Json.Arr(Json.Obj(d, "release"), "artifacts").FirstOrDefault(a => Json.Bool(a, "isPrimary")) is { ValueKind: JsonValueKind.Object } p
                ? p : Json.Arr(Json.Obj(d, "release"), "artifacts").FirstOrDefault();
            var refs = Json.Obj(artifact, "definitionReference");
            latest[(def, stage)] = new Deployment(def, stage, Date(Json.Str(d, "completedOn")),
                Json.Str(Json.Obj(refs, "branch"), "id"), Json.Str(Json.Obj(refs, "sourceVersion"), "id"),
                Json.Str(Json.Obj(refs, "version"), "name"), Json.Str(Json.Obj(refs, "repository"), "id"));
        }
        return [.. latest.Values];
    }
}

public sealed partial record CodeChange
{
    /// <summary>For a pull request: the merge base and the last source commit (the PR's diff is between them).</summary>
    public string? BaseCommit { get; init; }
    public string? HeadCommit { get; init; }

    /// <summary>For a commit: its first parent (the version before it).</summary>
    public string? Parent { get; init; }

    /// <summary>The project the code lives in (may differ from the work item's project).</summary>
    public string? Project { get; init; }

    /// <summary>The commit that has to be in a branch for this change to be there: a commit itself, a completed PR's merge.</summary>
    public string? LandedCommit => Kind switch { "commit" => Id, "pr" when State == "completed" => MergeCommit, _ => null };
}
