using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AdoLens;

public sealed record Iteration(string Id, string Name, string Path, DateTime? Start, DateTime? Finish, string TimeFrame);

/// <summary>Teams, sprints, capacity, boards and backlogs of a project.</summary>
public sealed class Boards(Session session)
{
    readonly AdoClient client = session.Client;
    WorkItems Work => session.Work;

    /// <summary>"Project/Team/" for team-scoped routes.</summary>
    static string Scope(string project, string team) => AdoClient.Scope(project) + Uri.EscapeDataString(team) + "/";

    /// <summary>The team given (checked against the project's teams), else the connection's team, else the project's default team.</summary>
    public async Task<string> TeamAsync(string project, string? team, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(team)) return await Work.TeamAsync(project, ct);
        var teams = (await client.GetListAsync($"_apis/projects/{Uri.EscapeDataString(project)}/teams?$top=500", ct))
            .Select(t => Json.Str(t, "name") ?? "").Where(n => n.Length > 0).ToList();
        return teams.FirstOrDefault(t => t.Equals(team.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? teams.SingleOrDefault(t => t.Contains(team.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"no team '{team}' in {project}; teams: {string.Join(", ", teams.Order())}");
    }

    public async Task<List<Iteration>> IterationsAsync(string project, string team, CancellationToken ct)
    {
        var list = await client.GetListAsync($"{Scope(project, team)}_apis/work/teamsettings/iterations", ct);
        return list.Select(i =>
        {
            var a = Json.Obj(i, "attributes");
            return new Iteration(Json.Str(i, "id") ?? "", Json.Str(i, "name") ?? "", Json.Str(i, "path") ?? "",
                Date(Json.Str(a, "startDate")), Date(Json.Str(a, "finishDate")), Json.Str(a, "timeFrame") ?? "");
        }).ToList();
    }

    static DateTime? Date(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.Date : null;

    /// <summary>current / next / previous, or a sprint by name or path.</summary>
    public static Iteration Pick(List<Iteration> all, string? which, DateTime today)
    {
        if (all.Count == 0) throw new InvalidOperationException("the team has no sprints (iterations) selected in its settings");
        var dated = all.Where(i => i.Start is not null && i.Finish is not null).OrderBy(i => i.Start).ToList();
        var w = (which ?? "current").Trim();
        Iteration? current = dated.FirstOrDefault(i => i.Start <= today && today <= i.Finish)
                             ?? all.FirstOrDefault(i => i.TimeFrame == "current");
        switch (w.ToLowerInvariant())
        {
            case "current":
                return current ?? dated.LastOrDefault(i => i.Finish < today) ?? dated.FirstOrDefault()
                    ?? throw new InvalidOperationException("the team's sprints have no dates");
            case "next":
                return dated.FirstOrDefault(i => i.Start > today) ?? throw new InvalidOperationException("no future sprint is planned for the team");
            case "previous" or "last":
                var before = current?.Start ?? today;
                return dated.LastOrDefault(i => i.Finish < before) ?? throw new InvalidOperationException("no earlier sprint");
            default:
                return all.FirstOrDefault(i => i.Name.Equals(w, StringComparison.OrdinalIgnoreCase) || i.Path.Equals(w, StringComparison.OrdinalIgnoreCase))
                    ?? all.SingleOrDefault(i => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"no sprint '{which}'; sprints: {string.Join(", ", all.TakeLast(12).Select(i => i.Name))}");
        }
    }

    public async Task<HashSet<DayOfWeek>> WorkingDaysAsync(string project, string team, CancellationToken ct)
    {
        var settings = await client.GetAsync($"{Scope(project, team)}_apis/work/teamsettings", ct);
        var days = Json.Arr(settings, "workingDays").Select(d => Enum.TryParse<DayOfWeek>(d.GetString(), true, out var v) ? (DayOfWeek?)v : null)
            .Where(d => d is not null).Select(d => d!.Value).ToHashSet();
        return days.Count > 0 ? days : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    }

    public static int WorkingDays(DateTime from, DateTime to, HashSet<DayOfWeek> working, IReadOnlyList<(DateTime Start, DateTime End)> off)
    {
        var n = 0;
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            if (working.Contains(d.DayOfWeek) && !off.Any(o => o.Start.Date <= d && d <= o.End.Date)) n++;
        return n;
    }

    static List<(DateTime, DateTime)> DaysOff(JsonElement e) =>
        Json.Arr(e, "daysOff").Select(d => (Date(Json.Str(d, "start")), Date(Json.Str(d, "end"))))
            .Where(d => d.Item1 is not null && d.Item2 is not null).Select(d => (d.Item1!.Value, d.Item2!.Value)).ToList();

    /// <summary>Capacity left in hours from <paramref name="from"/> to the end of the sprint, or null when nobody has capacity set.</summary>
    public async Task<(double Hours, int People)?> CapacityLeftAsync(string project, string team, Iteration it, DateTime from,
        HashSet<DayOfWeek> working, CancellationToken ct)
    {
        if (it.Finish is not { } finish || from > finish) return null;
        var scope = $"{Scope(project, team)}_apis/work/teamsettings/iterations/{it.Id}/";
        var cap = await client.GetAsync(scope + "capacities", ct);
        var members = cap.ValueKind == JsonValueKind.Object && cap.TryGetProperty("teamMembers", out var tm) ? tm.EnumerateArray().ToList()
            : Json.Arr(cap, "value").ToList();
        var teamOff = DaysOff(await client.GetAsync(scope + "teamdaysoff", ct));
        double hours = 0;
        var people = 0;
        foreach (var m in members)
        {
            var perDay = Json.Arr(m, "activities").Sum(a => a.TryGetProperty("capacityPerDay", out var c) && c.TryGetDouble(out var v) ? v : 0);
            if (perDay <= 0) continue;
            people++;
            hours += perDay * WorkingDays(from, finish, working, [.. teamOff, .. DaysOff(m)]);
        }
        return people == 0 ? null : (hours, people);
    }

    /// <summary>Ids in the sprint of the team and the parent of each child (tasks under their requirement).</summary>
    public async Task<(List<int> Ids, Dictionary<int, int> Parent)> SprintItemsAsync(string project, string team, Iteration it, CancellationToken ct)
    {
        JsonElement root;
        try { root = await client.GetAsync($"{Scope(project, team)}_apis/work/teamsettings/iterations/{it.Id}/workitems", ct); }
        catch (AdoApiException ex) when (ex.Message.Contains("VS402337", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{it.Name} holds more than 20 000 work items (the Azure DevOps limit for one query) - it is probably a " +
                                                "collecting iteration, not a sprint; pick another with sprint=<name>, or ask with ado_query");
        }
        var ids = new List<int>();
        var parent = new Dictionary<int, int>();
        foreach (var r in Json.Arr(root, "workItemRelations"))
        {
            var target = WorkItems.Id(Json.Obj(r, "target"));
            if (target == 0) continue;
            if (!ids.Contains(target)) ids.Add(target);
            var source = WorkItems.Id(Json.Obj(r, "source"));
            if (source > 0) parent[target] = source;
        }
        return (ids, parent);
    }

    public async Task<List<JsonElement>> BoardsAsync(string project, string team, CancellationToken ct) =>
        await client.GetListAsync($"{Scope(project, team)}_apis/work/boards", ct);

    public Task<JsonElement> BoardAsync(string project, string team, string id, CancellationToken ct) =>
        client.GetAsync($"{Scope(project, team)}_apis/work/boards/{Uri.EscapeDataString(id)}", ct);

    /// <summary>WIQL condition for the team's area paths (its "team field").</summary>
    public async Task<string> TeamAreaConditionAsync(string project, string team, CancellationToken ct)
    {
        var tfv = await client.GetAsync($"{Scope(project, team)}_apis/work/teamsettings/teamfieldvalues", ct);
        var field = Json.Str(Json.Obj(tfv, "field"), "referenceName") ?? "System.AreaPath";
        var parts = Json.Arr(tfv, "values").Select(v =>
        {
            var value = (Json.Str(v, "value") ?? "").Replace("'", "''");
            return $"[{field}] {(Json.Bool(v, "includeChildren") ? "UNDER" : "=")} '{value}'";
        }).ToList();
        return parts.Count == 0 ? "" : "(" + string.Join(" OR ", parts) + ")";
    }

    public async Task<List<JsonElement>> BacklogsAsync(string project, string team, CancellationToken ct) =>
        await client.GetListAsync($"{Scope(project, team)}_apis/work/backlogs?api-version=7.1-preview.1", ct);

    /// <summary>Top-level items of a backlog in their order.</summary>
    public async Task<List<int>> BacklogOrderAsync(string project, string team, string backlogId, CancellationToken ct)
    {
        var root = await client.GetAsync($"{Scope(project, team)}_apis/work/backlogs/{Uri.EscapeDataString(backlogId)}/workItems?api-version=7.1-preview.1", ct);
        return Json.Arr(root, "workItems").Where(w => WorkItems.Id(Json.Obj(w, "source")) == 0)
            .Select(w => WorkItems.Id(Json.Obj(w, "target"))).Where(i => i > 0).Distinct().ToList();
    }

    public Task<JsonElement> ReorderAsync(string project, string team, int id, int previousId, int nextId, CancellationToken ct) =>
        client.SendAsync(HttpMethod.Patch, $"{Scope(project, team)}_apis/work/workitemsorder?api-version=7.1-preview.1",
            new { ids = new[] { id }, previousId, nextId, parentId = 0 }, "application/json", ct);

    public async Task<(string Id, string Name)> MeAsync(CancellationToken ct)
    {
        var data = await client.GetAsync("_apis/connectionData?api-version=7.1-preview.1", ct);
        var user = Json.Obj(data, "authenticatedUser");
        return (Json.Str(user, "id") ?? "", Json.Str(user, "providerDisplayName") ?? "");
    }

    public async Task<List<JsonElement>> PullRequestsToReviewAsync(string project, string me, CancellationToken ct)
    {
        var list = await client.GetListAsync($"{AdoClient.Scope(project)}_apis/git/pullrequests?searchCriteria.reviewerId={me}&searchCriteria.status=active&$top=50", ct);
        // Waiting for me: my vote is still 0 (no vote) and the PR is not a draft.
        return list.Where(pr => !Json.Bool(pr, "isDraft") && Json.Arr(pr, "reviewers").Any(r =>
            Json.Str(r, "id") == me && (!r.TryGetProperty("vote", out var v) || v.GetInt32() == 0))).ToList();
    }
}

/// <summary>The answers behind the boards tools.</summary>
public static partial class BoardQueries
{
    /// <summary>Changed less recently than this (in working days) while in progress: worth a look.</summary>
    const int StaleDays = 3;

    static readonly string[] Estimates = ["Microsoft.VSTS.Scheduling.StoryPoints", "Microsoft.VSTS.Scheduling.Effort", "Microsoft.VSTS.Scheduling.Size"];
    const string RemainingRef = "Microsoft.VSTS.Scheduling.RemainingWork";
    const string BlockedRef = "Microsoft.VSTS.CMMI.Blocked";

    static string Category(Snapshot s, JsonElement wi)
    {
        var type = s.FindType(WorkItems.Field(wi, "System.WorkItemType") ?? "");
        var state = WorkItems.Field(wi, "System.State");
        var category = type?.States.FirstOrDefault(x => x.Name.Equals(state, StringComparison.OrdinalIgnoreCase))?.Category;
        // Some processes put "Done" in the Resolved category (waiting for a check); for the people it is done.
        if (category == "Resolved" && GuessCategory(state) == "Completed") return "Completed";
        return category
               // A type or state this project does not know (an item of another project in the sprint): any type with that state.
               ?? s.Types.SelectMany(t => t.States).FirstOrDefault(x => x.Name.Equals(state, StringComparison.OrdinalIgnoreCase))?.Category
               ?? GuessCategory(state);
    }

    /// <summary>The category of a state no process here knows, by its usual names.</summary>
    internal static string GuessCategory(string? state) => (state ?? "").Trim().ToLowerInvariant() switch
    {
        "done" or "closed" or "completed" or "finished" or "zamknięty" or "zakończony" or "gotowe" => "Completed",
        "removed" or "cut" or "usunięty" => "Removed",
        "new" or "proposed" or "to do" or "nowy" => "Proposed",
        "resolved" => "Resolved",
        _ => "InProgress",
    };

    static bool HasField(Snapshot s, string type, string reference) =>
        s.FieldsOf(type) is { Count: > 0 } f ? f.Any(x => x.Ref == reference) : s.Fields.ContainsKey(reference);

    /// <summary>
    /// Not started, or in the first in-progress state of its type. Later (in test, waiting for production) a missing
    /// estimate or criteria is no longer worth a flag.
    /// </summary>
    static bool Early(Snapshot s, JsonElement wi)
    {
        var category = Category(s, wi);
        if (category == "Proposed") return true;
        if (category != "InProgress") return false;
        var type = s.FindType(WorkItems.Field(wi, "System.WorkItemType") ?? "");
        var first = type?.States.FirstOrDefault(x => x.Category == "InProgress")?.Name;
        return first is null || first.Equals(WorkItems.Field(wi, "System.State"), StringComparison.OrdinalIgnoreCase);
    }

    static string Group(string category) => category switch
    {
        "Proposed" => "not started",
        "Completed" => "done",
        "Removed" => "removed",
        _ => "in progress",
    };

    static string Level(Snapshot s, JsonElement wi) => s.FindType(WorkItems.Field(wi, "System.WorkItemType") ?? "")?.Level ?? Levels.Other;

    static double? Number(JsonElement wi, string reference) =>
        double.TryParse(WorkItems.Field(wi, reference), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    static string Num(double v) => v.ToString(v % 1 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);

    static string Who(JsonElement wi) => WorkItems.Field(wi, "System.AssignedTo") is { } w ? $" ({w})" : "";

    static DateTime? Changed(JsonElement wi) =>
        DateTime.TryParse(WorkItems.Field(wi, "System.ChangedDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    public static async Task<string> SprintAsync(Lens lens, string? connection, string? project, string? team, string? sprint, string? detail,
        DateTime? today = null, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var boards = new Boards(session);
        var t = await boards.TeamAsync(name, team, ct);
        var day = (today ?? DateTime.Now).Date;
        var iterations = await boards.IterationsAsync(name, t, ct);
        var it = Boards.Pick(iterations, sprint, day);
        var working = await boards.WorkingDaysAsync(name, t, ct);
        var s = (await session.Meta.GetAsync(name, ct)).Snapshot;

        var sb = new StringBuilder();
        var asked = (sprint ?? "current").Trim();
        if (asked.Equals("current", StringComparison.OrdinalIgnoreCase) && it.Start is { } s0 && it.Finish is { } f0 && (day < s0 || day > f0))
            sb.AppendLine("NO CURRENT SPRINT for the team today - this is the " + (day > f0 ? "last one that ended:" : "next one:"));
        sb.Append($"{it.Name} ({t})");
        if (it.Start is { } start && it.Finish is { } finish)
        {
            sb.Append($": {start:yyyy-MM-dd} - {finish:yyyy-MM-dd}");
            var total = Boards.WorkingDays(start, finish, working, []);
            if (day > finish)
                sb.Append($", ENDED {(day - finish).Days} days ago" +
                          (iterations.All(i => i.Finish is null || i.Finish < day) ? " - no sprint covers today (the team may not plan in sprints now; sprint=next|<name>)" : ""));
            else if (day < start) sb.Append($", starts in {(start - day).Days} days, {total} working days");
            else sb.Append($", working day {Boards.WorkingDays(start, day, working, [])} of {total}, {Boards.WorkingDays(day.AddDays(1), finish, working, [])} left after today");
        }
        sb.AppendLine();

        var (ids, parent) = await boards.SprintItemsAsync(name, t, it, ct);
        if (ids.Count == 0) return sb.Append("no work items in this sprint").ToString();

        var fields = new List<string> { "System.Id", "System.WorkItemType", "System.State", "System.Title", "System.AssignedTo", "System.TeamProject", "System.ChangedDate", "System.Tags" };
        var estimate = Estimates.FirstOrDefault(e => s.Fields.ContainsKey(e));
        foreach (var f in new[] { estimate, RemainingRef, BlockedRef, WorkItems.AcceptanceRef })
            if (f is not null && s.Fields.ContainsKey(f)) fields.Add(f);
        var items = await session.Work.GetAsync(ids, relations: false, fields, ct);
        var byId = items.ToDictionary(WorkItems.Id);

        // Requirements and bugs on top; tasks under them (or on their own when their parent is not in the sprint).
        var top = items.Where(w => !parent.TryGetValue(WorkItems.Id(w), out var p) || !byId.ContainsKey(p)).ToList();
        var children = items.Where(w => parent.TryGetValue(WorkItems.Id(w), out var p) && byId.ContainsKey(p))
            .GroupBy(w => parent[WorkItems.Id(w)]).ToDictionary(g => g.Key, g => g.ToList());
        var live = top.Where(w => Category(s, w) != "Removed").ToList();

        var groups = live.GroupBy(w => Group(Category(s, w))).ToDictionary(g => g.Key, g => g.ToList());
        int Count(string g) => groups.TryGetValue(g, out var l) ? l.Count : 0;
        sb.Append($"items: {live.Count} (done {Count("done")}, in progress {Count("in progress")}, not started {Count("not started")})");
        if (estimate is not null)
        {
            var sum = live.Sum(w => Number(w, estimate) ?? 0);
            var done = live.Where(w => Group(Category(s, w)) == "done").Sum(w => Number(w, estimate) ?? 0);
            if (sum > 0) sb.Append($"; {s.Fields[estimate].Name.ToLowerInvariant()} {Num(done)} of {Num(sum)} done");
        }
        sb.AppendLine();

        var open = items.Where(w => Group(Category(s, w)) is not ("done" or "removed")).ToList();
        var remaining = open.Sum(w => Number(w, RemainingRef) ?? 0);
        // Remaining Work is kept on tasks; when most open tasks have none, a "fits" would only be a guess.
        var openTasks = open.Where(w => Level(s, w) == Levels.Task).ToList();
        var kept = openTasks.Count(w => Number(w, RemainingRef) is not null);
        if (it.Finish is { } end && day <= end && s.Fields.ContainsKey(RemainingRef))
        {
            if (openTasks.Count > 0 && kept * 2 < openTasks.Count)
                sb.AppendLine($"remaining work is filled on only {kept} of {openTasks.Count} open tasks ({Num(remaining)} h) - too little to compare with capacity");
            else
            {
                var cap = await TryAsync(() => boards.CapacityLeftAsync(name, t, it, it.Start is { } st && st > day ? st : day, working, ct));
                sb.AppendLine(cap is { } c
                    ? $"remaining work {Num(remaining)} h, capacity left {Num(c.Hours)} h ({c.People} people) - " +
                      (remaining <= c.Hours ? $"fits, {Num(c.Hours - remaining)} h spare" : $"OVER by {Num(remaining - c.Hours)} h")
                    : $"remaining work {Num(remaining)} h; no capacity set for the team");
            }
        }

        var attention = new List<string>();
        var attentionTop = 0;
        var topIds = live.Select(WorkItems.Id).ToHashSet();
        foreach (var w in live.Concat(children.Values.SelectMany(x => x)).Where(w => Group(Category(s, w)) is not ("done" or "removed")))
        {
            var why = new List<string>();
            var level = Level(s, w);
            if (Group(Category(s, w)) == "in progress" && Changed(w) is { } ch && Boards.WorkingDays(ch.ToLocalTime().Date.AddDays(1), day, working, []) >= StaleDays)
                why.Add($"no change for {(day - ch.ToLocalTime().Date).Days} days");
            if ((WorkItems.Field(w, "System.Tags") ?? "").Split(';').Any(x => x.Trim().Equals("blocked", StringComparison.OrdinalIgnoreCase))
                || WorkItems.Field(w, BlockedRef) is "Yes") why.Add("blocked");
            var typeName = WorkItems.Field(w, "System.WorkItemType") ?? "";
            if (level == Levels.Requirement && Early(s, w))
            {
                if (estimate is not null && HasField(s, typeName, estimate) && Number(w, estimate) is null) why.Add("no estimate");
                if (fields.Contains(WorkItems.AcceptanceRef) && HasField(s, typeName, WorkItems.AcceptanceRef)
                    && Html.ToMarkdown(WorkItems.Field(w, WorkItems.AcceptanceRef)).Length == 0)
                    why.Add("no acceptance criteria");
            }
            if (WorkItems.Field(w, "System.AssignedTo") is null) why.Add("unassigned");
            if (why.Count == 0) continue;
            attention.Add($"- {WorkItemFormat.Header(w)}{Who(w)}: {string.Join("; ", why)}");
            if (topIds.Contains(WorkItems.Id(w))) attentionTop++;
        }
        if (attention.Count > 0)
        {
            var taskCount = attention.Count - attentionTop;
            sb.AppendLine(taskCount > 0 ? $"attention ({attention.Count}: {attentionTop} items, {taskCount} of their tasks):" : $"attention ({attention.Count}):");
            foreach (var a in attention.Take(detail == "full" ? 200 : 15)) sb.AppendLine(a);
            if (attention.Count > 15 && detail != "full") sb.AppendLine($"- … {attention.Count - 15} more (detail=full)");
        }
        if (detail == "summary") return sb.ToString().TrimEnd();

        if (detail == "full")
        {
            // Per person: their open items and tasks and the hours left on them.
            var people = open.GroupBy(w => WorkItems.Field(w, "System.AssignedTo") ?? "(unassigned)")
                .Select(g => (Who: g.Key, Items: g.Count(w => Level(s, w) != Levels.Task), Tasks: g.Count(w => Level(s, w) == Levels.Task),
                    Left: g.Sum(w => Number(w, RemainingRef) ?? 0)))
                .OrderByDescending(p => p.Items + p.Tasks).ToList();
            if (people.Count > 0)
            {
                sb.AppendLine("by person (open):");
                foreach (var p in people)
                    sb.AppendLine($"  {p.Who}: {p.Items} item(s), {p.Tasks} task(s){(p.Left > 0 ? $", {Num(p.Left)} h left" : "")}");
            }
        }

        foreach (var g in new[] { "in progress", "not started", "done" })
        {
            if (!groups.TryGetValue(g, out var list)) continue;
            var shown = g == "done" && detail != "full" ? list.Take(10).ToList() : list;
            sb.AppendLine($"{g} ({list.Count}):");
            foreach (var w in shown)
            {
                var line = $"  {WorkItemFormat.Header(w)}{Who(w)}";
                if (estimate is not null && Number(w, estimate) is { } e) line += $" | {Num(e)}";
                if (children.TryGetValue(WorkItems.Id(w), out var kids))
                {
                    var doneKids = kids.Count(k => Group(Category(s, k)) == "done");
                    var left = kids.Sum(k => Group(Category(s, k)) == "done" ? 0 : Number(k, RemainingRef) ?? 0);
                    line += $" | tasks {doneKids}/{kids.Count} done{(left > 0 ? $", {Num(left)} h left" : "")}";
                }
                sb.AppendLine(line);
            }
            if (shown.Count < list.Count) sb.AppendLine($"  … {list.Count - shown.Count} more (detail=full)");
        }
        return sb.ToString().TrimEnd();
    }

    static async Task<T?> TryAsync<T>(Func<Task<T?>> f) where T : struct
    {
        try { return await f(); }
        catch (AdoApiException) { return null; }
    }

    /// <summary>The board of the requirement backlog unless another is named.</summary>
    static async Task<(JsonElement Board, string Name)> PickBoardAsync(Boards boards, string project, string team, string? board, CancellationToken ct)
    {
        var list = await boards.BoardsAsync(project, team, ct);
        if (list.Count == 0) throw new InvalidOperationException($"team {team} has no boards");
        JsonElement? pick = null;
        if (!string.IsNullOrWhiteSpace(board))
            pick = list.FirstOrDefault(b => (Json.Str(b, "name") ?? "").Equals(board.Trim(), StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } x ? x
                : throw new InvalidOperationException($"no board '{board}'; boards: {string.Join(", ", list.Select(b => Json.Str(b, "name")))}");
        else
        {
            var backlogs = await boards.BacklogsAsync(project, team, ct);
            var requirement = backlogs.Where(b => Json.Str(b, "type") == "requirement").Select(b => Json.Str(b, "name")).FirstOrDefault();
            pick = list.FirstOrDefault(b => Json.Str(b, "name") == requirement) is { ValueKind: JsonValueKind.Object } r ? r : list[0];
        }
        var id = Json.Str(pick!.Value, "id") ?? Json.Str(pick.Value, "name")!;
        return (await boards.BoardAsync(project, team, id, ct), Json.Str(pick.Value, "name") ?? id);
    }

    sealed record Column(string Name, string Type, int Limit, bool Split, Dictionary<string, string> States);

    static List<Column> Columns(JsonElement board) =>
        Json.Arr(board, "columns").Select(c => new Column(Json.Str(c, "name") ?? "", Json.Str(c, "columnType") ?? "",
            c.TryGetProperty("itemLimit", out var l) && l.TryGetInt32(out var n) ? n : 0, Json.Bool(c, "isSplit"),
            Json.Obj(c, "stateMappings") is { ValueKind: JsonValueKind.Object } m
                ? m.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))).ToList();

    public static async Task<string> BoardAsync(Lens lens, string? connection, string? project, string? team, string? board,
        int? move, string? column, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var boards = new Boards(session);
        var t = await boards.TeamAsync(name, team, ct);
        var (b, boardName) = await PickBoardAsync(boards, name, t, board, ct);
        var columns = Columns(b);
        var fields = Json.Obj(b, "fields");
        var columnField = Json.Str(Json.Obj(fields, "columnField"), "referenceName") ?? "System.BoardColumn";
        var doneField = Json.Str(Json.Obj(fields, "doneField"), "referenceName");
        var types = columns.SelectMany(c => c.States.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var s = (await session.Meta.GetAsync(name, ct)).Snapshot;

        if (move is { } id)
        {
            if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("give the column to move to");
            var col = columns.FirstOrDefault(c => c.Name.Equals(column.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"no column '{column}' on {boardName}; columns: {string.Join(", ", columns.Select(c => c.Name))}");
            var items = await session.Work.GetAsync([id], relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.TeamProject", columnField], ct);
            if (items.Count == 0) return $"no work item #{id} visible with this PAT";
            var wi = items[0];
            var typeName = WorkItems.Field(wi, "System.WorkItemType") ?? "";
            if (!col.States.TryGetValue(typeName, out var state))
                throw new InvalidOperationException($"{typeName} items are not on the {boardName} board (it shows {string.Join(", ", types)})");
            var ops = new List<object> { new { op = "add", path = "/fields/" + columnField, value = col.Name } };
            if (doneField is not null && col.Split) ops.Add(new { op = "add", path = "/fields/" + doneField, value = false });
            if (!state.Equals(WorkItems.Field(wi, "System.State"), StringComparison.OrdinalIgnoreCase))
                ops.Add(new { op = "add", path = "/fields/System.State", value = state });
            var updated = await session.Work.PatchAsync(id, ops, ct);
            Audit.Write(lens, session.Config.Connection, Audit.FromOps(Audit.NewBatch(), "ado_board", wi, updated, ops));
            var note = "";
            if (col.Limit > 0)
            {
                var (count, _) = await CardsAsync(session, boards, name, t, s, types, columnField, ct);
                var inColumn = count.GetValueOrDefault(col.Name)?.Count ?? 0;
                if (inColumn > col.Limit) note = $"\nnote: {col.Name} now has {inColumn} items, WIP limit {col.Limit}";
            }
            return $"moved {WorkItemFormat.Header(updated)} to {col.Name} on {boardName}{note}";
        }

        var (cards, recentDays) = await CardsAsync(session, boards, name, t, s, types, columnField, ct);
        var sb = new StringBuilder($"{boardName} board ({t}): ");
        sb.AppendLine(string.Join(" | ", columns.Select(c =>
        {
            var n = cards.GetValueOrDefault(c.Name)?.Count ?? 0;
            var limit = c.Limit > 0 ? $"/{c.Limit}{(n > c.Limit ? " OVER" : "")}" : "";
            return $"{c.Name} {n}{limit}{(c.Type == "outgoing" ? $" (last {recentDays} days)" : "")}";
        })));
        if (detail == "summary") return sb.ToString().TrimEnd();
        foreach (var c in columns)
        {
            if (!cards.TryGetValue(c.Name, out var list) || list.Count == 0) continue;
            var max = detail == "full" ? 500 : c.Type == "outgoing" ? 5 : 15;
            sb.AppendLine($"{c.Name}:");
            foreach (var w in list.Take(max)) sb.AppendLine($"  {WorkItemFormat.Header(w)}{Who(w)}");
            if (list.Count > max) sb.AppendLine($"  … {list.Count - max} more (detail=full)");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Cards per column: the board's types in the team's areas, open ones plus those finished in the last days.</summary>
    static async Task<(Dictionary<string, List<JsonElement>> Cards, int RecentDays)> CardsAsync(Session session, Boards boards, string project, string team,
        Snapshot s, List<string> types, string columnField, CancellationToken ct)
    {
        const int RecentDays = 14;
        var area = await boards.TeamAreaConditionAsync(project, team, ct);
        var closed = types.Select(s.FindType).Where(x => x is not null)
            .SelectMany(x => x!.States.Where(st => st.Category is "Completed" or "Removed").Select(st => st.Name)).Distinct().ToList();
        var removed = types.Select(s.FindType).Where(x => x is not null)
            .SelectMany(x => x!.States.Where(st => st.Category == "Removed").Select(st => st.Name)).Distinct().ToList();
        string List(IEnumerable<string> v) => string.Join(", ", v.Select(x => $"'{x.Replace("'", "''")}'"));
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN ({List(types)})" +
                   (area.Length > 0 ? $" AND {area}" : "") +
                   (removed.Count > 0 ? $" AND [System.State] NOT IN ({List(removed)})" : "") +
                   (closed.Count > 0 ? $" AND ([System.State] NOT IN ({List(closed)}) OR [System.ChangedDate] >= @Today - {RecentDays})" : "") +
                   " ORDER BY [Microsoft.VSTS.Common.BacklogPriority], [Microsoft.VSTS.Common.StackRank], [System.Id]";
        List<int> ids;
        try { ids = (await session.Work.WiqlAsync(project, wiql, 1000, ct)).Ids; }
        catch (AdoApiException) // processes without the ordering fields
        { ids = (await session.Work.WiqlAsync(project, wiql[..wiql.LastIndexOf(" ORDER BY", StringComparison.Ordinal)], 1000, ct)).Ids; }
        var items = await session.Work.GetAsync(ids, relations: false,
            ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.AssignedTo", "System.TeamProject", columnField], ct);
        var cards = items.GroupBy(w => WorkItems.Field(w, columnField) ?? WorkItems.Field(w, "System.State") ?? "")
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        return (cards, RecentDays);
    }

    public static async Task<string> BacklogAsync(Lens lens, string? connection, string? project, string? team, string? level, int top,
        int? move, int? above, int? below, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var boards = new Boards(session);
        var t = await boards.TeamAsync(name, team, ct);
        var backlogs = await boards.BacklogsAsync(name, t, ct);
        var want = (level ?? Levels.Requirement).Trim();
        var backlog = backlogs.FirstOrDefault(b => (Json.Str(b, "name") ?? "").Equals(want, StringComparison.OrdinalIgnoreCase))
            is { ValueKind: JsonValueKind.Object } byName ? byName
            : backlogs.FirstOrDefault(b => (Json.Str(b, "type") ?? "").Equals(want, StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } byType ? byType
            : throw new InvalidOperationException($"no backlog '{level}'; backlogs: {string.Join(", ", backlogs.Select(b => $"{Json.Str(b, "name")} ({Json.Str(b, "type")})"))}");
        var backlogId = Json.Str(backlog, "id")!;
        var backlogName = Json.Str(backlog, "name") ?? backlogId;
        var order = await boards.BacklogOrderAsync(name, t, backlogId, ct);

        if (move is { } id)
        {
            if (above is null == (below is null)) throw new ArgumentException("give either above or below");
            var target = above ?? below!.Value;
            var rest = order.Where(x => x != id).ToList();
            var i = rest.IndexOf(target);
            if (i < 0) throw new InvalidOperationException($"#{target} is not on the {backlogName} backlog of {t}");
            var (prev, next) = above is not null ? (i > 0 ? rest[i - 1] : 0, target) : (target, i + 1 < rest.Count ? rest[i + 1] : 0);
            await boards.ReorderAsync(name, t, id, prev, next, ct);
            var heads = await session.Work.GetAsync([id, target], relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.TeamProject"], ct);
            var h = heads.ToDictionary(WorkItems.Id, WorkItemFormat.Header);
            return $"moved {h.GetValueOrDefault(id, $"#{id}")} {(above is not null ? "above" : "below")} {h.GetValueOrDefault(target, $"#{target}")} " +
                   $"(position {rest.IndexOf(target) + (above is not null ? 1 : 2)} of {order.Count})";
        }

        top = Math.Clamp(top, 1, 200);
        var s = (await session.Meta.GetAsync(name, ct)).Snapshot;
        var estimate = Estimates.FirstOrDefault(e => s.Fields.ContainsKey(e));
        var fields = new List<string> { "System.Id", "System.WorkItemType", "System.State", "System.Title", "System.AssignedTo", "System.TeamProject", "System.IterationPath" };
        if (estimate is not null) fields.Add(estimate);
        var items = await session.Work.GetAsync(order.Take(top).ToList(), relations: false, fields, ct);
        var sb = new StringBuilder($"{backlogName} backlog ({t}): {(order.Count > items.Count ? $"top {items.Count} of {order.Count}" : $"{order.Count}")} in priority order\n");
        var pos = 0;
        foreach (var w in items)
        {
            pos++;
            var it = WorkItems.Field(w, "System.IterationPath");
            var sprint = it is not null && it.Contains('\\') ? " | " + it[(it.IndexOf('\\') + 1)..] : "";
            var est = estimate is not null && Number(w, estimate) is { } e ? $" | {Num(e)}" : "";
            sb.AppendLine($"{pos}. {WorkItemFormat.Header(w)}{Who(w)}{sprint}{est}");
        }
        return sb.ToString().TrimEnd();
    }

    public static async Task<string> MyDayAsync(Lens lens, string? connection, string? project, string? since, DateTime? now = null, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var boards = new Boards(session);
        var today = (now ?? DateTime.Now).Date;
        // Default: since the start of the previous working day (on Monday: since Friday).
        var from = since is not null ? Formatting.ParseSince(since, now?.ToUniversalTime()).ToLocalTime().Date
            : today.AddDays(today.DayOfWeek == DayOfWeek.Monday ? -3 : today.DayOfWeek == DayOfWeek.Sunday ? -2 : -1);
        var date = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (meId, meName) = await boards.MeAsync(ct);

        var sections = new (string Title, string Wiql)[]
        {
            ("mentioned you", $"SELECT [System.Id] FROM WorkItems WHERE [System.Id] IN (@RecentMentions) AND [System.ChangedDate] >= '{date}' ORDER BY [System.ChangedDate] DESC"),
            ("your items changed by others", $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me AND [System.ChangedDate] >= '{date}' AND [System.ChangedBy] <> @Me ORDER BY [System.ChangedDate] DESC"),
            ("followed items changed by others", $"SELECT [System.Id] FROM WorkItems WHERE [System.Id] IN (@Follows) AND [System.ChangedDate] >= '{date}' AND [System.ChangedBy] <> @Me ORDER BY [System.ChangedDate] DESC"),
        };
        var sb = new StringBuilder($"{meName} - since {date}:\n");
        var seen = new HashSet<int>();
        foreach (var (title, wiql) in sections)
        {
            List<int> ids;
            try { ids = (await session.Work.WiqlAsync(name, wiql, 200, ct)).Ids.Where(seen.Add).ToList(); }
            catch (AdoApiException ex) { sb.AppendLine($"{title}: not available ({Formatting.Collapse(ex.Message, 100)})"); continue; }
            if (ids.Count == 0) { sb.AppendLine($"{title}: none"); continue; }
            var items = await session.Work.GetAsync(ids.Take(25).ToList(), relations: false,
                ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.TeamProject", "System.ChangedBy", "System.ChangedDate"], ct);
            sb.AppendLine($"{title} ({ids.Count}):");
            foreach (var w in items)
                sb.AppendLine($"  {WorkItemFormat.Header(w)} - {WorkItems.Field(w, "System.ChangedBy")}, {Changed(w)?.ToLocalTime():MM-dd HH:mm}");
            if (ids.Count > 25) sb.AppendLine($"  … {ids.Count - 25} more");
        }
        try
        {
            var prs = await boards.PullRequestsToReviewAsync(name, meId, ct);
            if (prs.Count == 0) sb.AppendLine("pull requests waiting for your review: none");
            else
            {
                sb.AppendLine($"pull requests waiting for your review ({prs.Count}):");
                foreach (var pr in prs.Take(15))
                    sb.AppendLine($"  PR {pr.GetProperty("pullRequestId").GetInt32()} {Json.Str(pr, "title")} - {Json.Str(Json.Obj(pr, "createdBy"), "displayName")}, " +
                                  $"{Json.Str(Json.Obj(pr, "repository"), "name")}: {Short(Json.Str(pr, "sourceRefName"))} -> {Short(Json.Str(pr, "targetRefName"))}");
            }
        }
        catch (AdoApiException) { sb.AppendLine("pull requests: not readable with this PAT (needs Code: Read)"); }
        return sb.ToString().TrimEnd();
    }

    static string Short(string? refName) => refName?.Replace("refs/heads/", "") ?? "";
}
