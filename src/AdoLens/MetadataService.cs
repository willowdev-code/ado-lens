using System.Collections.Concurrent;
using System.Text.Json;

namespace AdoLens;

/// <summary>
/// Keeps the local metadata of a connection current: fetched on first use of a project, refreshed in the background
/// when older than <see cref="MaxAge"/>, and on demand. Reads never wait for a background refresh.
/// </summary>
public sealed class MetadataService(AdoClient client, MetaStore store)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    const int Parallel = 6;

    readonly ConcurrentDictionary<string, Task<(Snapshot, List<ChangeRecord>)>> _running = new(StringComparer.OrdinalIgnoreCase);

    public MetaStore Store => store;

    /// <summary>The stored snapshot; fetched now when there is none, refreshed in the background when stale.</summary>
    public async Task<(Snapshot Snapshot, bool Refreshing)> GetAsync(string project, CancellationToken ct = default)
    {
        var snap = store.Load(project);
        if (snap is null)
            return ((await RefreshAsync(project, ct)).Snapshot, false);
        if (DateTime.UtcNow - snap.Project.RefreshedUtc > MaxAge)
        {
            _ = RefreshAsync(snap.Project.Name, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return (snap, true);
        }
        return (snap, false);
    }

    /// <summary>Fetches the structure of the project and stores it; concurrent calls for the same project share one fetch.</summary>
    public Task<(Snapshot Snapshot, List<ChangeRecord> Changes)> RefreshAsync(string project, CancellationToken ct = default)
    {
        var task = _running.GetOrAdd(project, p => Task.Run(async () =>
        {
            var snap = await FetchAsync(p, ct);
            return (snap, store.Save(snap));
        }, ct));
        // Removed only after it is in the dictionary, and only this task (not a newer one for the same project).
        task.ContinueWith(t => _running.TryRemove(KeyValuePair.Create(project, t)), TaskScheduler.Default);
        return task;
    }

    async Task<Snapshot> FetchAsync(string project, CancellationToken ct)
    {
        var proj = await client.GetAsync($"_apis/projects/{Uri.EscapeDataString(project)}?includeCapabilities=true", ct);
        var id = Json.Str(proj, "id")!;
        var name = Json.Str(proj, "name") ?? project;
        var scope = AdoClient.Scope(name);
        var vc = Json.Obj(Json.Obj(proj, "capabilities"), "versioncontrol");
        var team = Json.Str(Json.Obj(proj, "defaultTeam"), "name");

        var fieldsTask = client.GetListAsync("_apis/wit/fields", ct);
        var typesTask = client.GetListAsync($"{scope}_apis/wit/workitemtypes", ct);
        var processTask = ProcessNameAsync(id, ct);
        var backlogTask = team is null ? Task.FromResult<JsonElement?>(null)
            : TryAsync(() => client.GetAsync($"{scope}{Uri.EscapeDataString(team)}/_apis/work/backlogconfiguration", ct));
        var categoriesTask = TryAsync(() => client.GetAsync($"{scope}_apis/wit/workitemtypecategories", ct));

        var orgFields = (await fieldsTask)
            .Select(f => new FieldDef(Json.Str(f, "referenceName") ?? "", Json.Str(f, "name") ?? "", Json.Str(f, "type") ?? "string",
                Json.Bool(f, "isPicklist"), Json.Bool(f, "readOnly"), Json.Str(f, "description")))
            .Where(f => f.Ref.Length > 0)
            .ToDictionary(f => f.Ref, StringComparer.OrdinalIgnoreCase);

        var levels = LevelsByType(await backlogTask, await categoriesTask);
        var types = new List<TypeInfo>();
        foreach (var t in await typesTask)
        {
            var typeName = Json.Str(t, "name") ?? "";
            var states = Json.Arr(t, "states").Select(s => new StateInfo(Json.Str(s, "name") ?? "", Json.Str(s, "category") ?? "")).ToList();
            var transitions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (Json.Obj(t, "transitions") is { ValueKind: JsonValueKind.Object } tr)
                foreach (var from in tr.EnumerateObject())
                    transitions[from.Name] = from.Value.ValueKind == JsonValueKind.Array
                        ? [.. from.Value.EnumerateArray().Select(x => Json.Str(x, "to") ?? "").Where(x => x.Length > 0 && x != from.Name)]
                        : [];
            types.Add(new TypeInfo(typeName, Json.Str(t, "referenceName") ?? typeName,
                levels.TryGetValue(typeName, out var level) ? level : Levels.Other,
                Json.Str(t, "description"), Json.Bool(t, "isDisabled"), states, transitions));
        }

        // Per-type fields carry what the list does not: required flags, defaults and allowed values.
        var byType = new ConcurrentDictionary<string, List<TypeField>>(StringComparer.OrdinalIgnoreCase);
        await System.Threading.Tasks.Parallel.ForEachAsync(types.Where(t => !t.Disabled),
            new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct },
            async (t, token) =>
            {
                var list = await client.GetListAsync($"{scope}_apis/wit/workitemtypes/{Uri.EscapeDataString(t.Name)}/fields?$expand=allowedValues", token);
                byType[t.Name] = list.Select(f => new TypeField(
                    Json.Str(f, "referenceName") ?? "", Json.Str(f, "name") ?? "", Json.Bool(f, "alwaysRequired"),
                    f.TryGetProperty("defaultValue", out var d) ? Json.Scalar(d) : null,
                    [.. Json.Arr(f, "allowedValues").Select(Json.Scalar).Where(v => v is not null).Select(v => v!)],
                    Json.Str(f, "helpText"))).Where(f => f.Ref.Length > 0).ToList();
            });

        var info = new ProjectInfo(id, name, await processTask,
            IsTrue(Json.Str(vc, "gitEnabled")) || Json.Str(vc, "sourceControlType") == "Git",
            IsTrue(Json.Str(vc, "tfvcEnabled")) || Json.Str(vc, "sourceControlType") == "Tfvc",
            team, DateTime.UtcNow);
        return new Snapshot(info, types, new Dictionary<string, List<TypeField>>(byType, StringComparer.OrdinalIgnoreCase), orgFields);
    }

    /// <summary>
    /// Backlog level per type: from the backlog configuration of the default team, else from the type categories.
    /// Bug wins over requirement (bugs can sit on the requirement backlog but keep bug content).
    /// </summary>
    internal static Dictionary<string, string> LevelsByType(JsonElement? backlog, JsonElement? categories)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Set(IEnumerable<JsonElement> items, string level, bool overwrite = false)
        {
            foreach (var n in items.Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : Json.Str(i, "name")))
                if (!string.IsNullOrEmpty(n) && (overwrite || !map.ContainsKey(n))) map[n] = level;
        }

        if (backlog is { } b)
        {
            Set(Json.Arr(Json.Obj(b, "bugWorkItems"), "workItemTypes"), Levels.Bug);
            foreach (var pb in Json.Arr(b, "portfolioBacklogs")) Set(Json.Arr(pb, "workItemTypes"), Levels.Portfolio);
            Set(Json.Arr(Json.Obj(b, "requirementBacklog"), "workItemTypes"), Levels.Requirement);
            Set(Json.Arr(Json.Obj(b, "taskBacklog"), "workItemTypes"), Levels.Task);
        }
        if (categories is { } c)
            foreach (var cat in Json.Arr(c, "value"))
            {
                var level = Json.Str(cat, "referenceName") switch
                {
                    "Microsoft.BugCategory" => Levels.Bug,
                    "Microsoft.EpicCategory" or "Microsoft.FeatureCategory" => Levels.Portfolio,
                    "Microsoft.RequirementCategory" => Levels.Requirement,
                    "Microsoft.TaskCategory" => Levels.Task,
                    "Microsoft.HiddenCategory" => Levels.Hidden,
                    _ => null,
                };
                // Bugs shown on the requirement backlog (bugsBehavior asRequirements) still keep bug content.
                if (level is not null) Set(Json.Arr(cat, "workItemTypes"), level, overwrite: level == Levels.Bug);
            }
        return map;
    }

    /// <summary>
    /// The process name: "System.Process Template" holds the original template (e.g. Scrum), so an inherited process is
    /// read through System.ProcessTemplateType from work/processes; without that permission the template name is used.
    /// </summary>
    async Task<string?> ProcessNameAsync(string projectId, CancellationToken ct)
    {
        if (await TryAsync(() => client.GetAsync($"_apis/projects/{projectId}/properties?api-version=7.1-preview.1", ct)) is not { } props)
            return null;
        string? Prop(string key) => Json.Arr(props, "value").Where(v => Json.Str(v, "name") == key).Select(v => Json.Str(v, "value")).FirstOrDefault();
        var template = Prop("System.Process Template");
        if (Prop("System.ProcessTemplateType") is { } typeId
            && await TryAsync(() => client.GetAsync($"_apis/work/processes/{Uri.EscapeDataString(typeId)}", ct)) is { } process
            && Json.Str(process, "name") is { } processName)
            return template is null || processName.Equals(template, StringComparison.OrdinalIgnoreCase) ? processName : $"{processName} (based on {template})";
        return template;
    }

    static bool IsTrue(string? s) => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Optional parts of the metadata (process name, backlog levels): missing permission must not fail the refresh.</summary>
    static async Task<JsonElement?> TryAsync(Func<Task<JsonElement>> f)
    {
        try { return await f(); }
        catch (AdoApiException) { return null; }
    }
}
