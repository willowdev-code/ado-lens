using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>Backlog hygiene and release notes.</summary>
public static partial class BoardQueries
{
    const int StaleBacklogDays = 14;
    const int MaxHealthItems = 1000;
    const int ShownPerRule = 5;
    const int DoneWithoutCodeDays = 30;

    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    static string InList(IEnumerable<string> values) => "(" + string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase).Select(Quote)) + ")";

    /// <summary>Requirement and bug types of the team's backlog (the ones the hygiene rules are about).</summary>
    static List<TypeInfo> BacklogTypes(Snapshot s) =>
        s.Types.Where(t => !t.Disabled && t.Level is Levels.Requirement or Levels.Bug).ToList();

    static async Task<string> AreaAsync(Boards boards, string project, string team, CancellationToken ct)
    {
        var area = await boards.TeamAreaConditionAsync(project, team, ct);
        return area.Length == 0 ? "" : " AND " + area;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    static HashSet<string> Words(string? title) =>
        NonWord().Split((title ?? "").ToLowerInvariant()).Where(w => w.Length > 2).ToHashSet();

    /// <summary>
    /// What needs tidying on the team's open backlog: no acceptance criteria, no estimate, no parent, no change for a long
    /// time, left in a past sprint, look-alike titles; and recently finished items without any linked code.
    /// </summary>
    public static async Task<string> HealthAsync(Lens lens, string? connection, string? project, string? team, DateTime? today = null, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var boards = new Boards(session);
        var t = await boards.TeamAsync(name, team, ct);
        var day = (today ?? DateTime.Now).Date;
        var s = (await session.Meta.GetAsync(name, ct)).Snapshot;
        var types = BacklogTypes(s);
        if (types.Count == 0) return $"no requirement or bug types found in {name}";
        // Resolved (e.g. "Done" waiting for closing) is finished work for this purpose.
        var open = types.SelectMany(x => x.States).Where(x => x.Category is "Proposed" or "InProgress").Select(x => x.Name).ToList();
        var done = types.SelectMany(x => x.States).Where(x => x.Category == "Completed").Select(x => x.Name).ToList();
        var area = await AreaAsync(boards, name, t, ct);
        var typeList = InList(types.Select(x => x.Name));

        var (openIds, _) = await session.Work.WiqlAsync(name,
            $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN {typeList}" +
            (open.Count > 0 ? $" AND [System.State] IN {InList(open)}" : "") + area + " ORDER BY [System.ChangedDate] DESC", MaxHealthItems, ct);
        var items = openIds.Count == 0 ? [] : await session.Work.GetAsync(openIds.Take(MaxHealthItems).ToList(), relations: true, ct: ct);
        var iterations = await boards.IterationsAsync(name, t, ct);
        var past = iterations.Where(i => i.Finish is { } f && f < day).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var estimate = Estimates.FirstOrDefault(e => s.Fields.ContainsKey(e));
        var hasPortfolio = s.Types.Any(x => !x.Disabled && x.Level == Levels.Portfolio);

        var findings = new Dictionary<string, List<string>>();
        void Add(string rule, JsonElement w, string? extra = null)
        {
            if (!findings.TryGetValue(rule, out var l)) findings[rule] = l = [];
            l.Add($"{WorkItemFormat.Header(w)}{Who(w)}{(extra is null ? "" : " - " + extra)}");
        }
        foreach (var w in items)
        {
            var type = s.FindType(WorkItems.Field(w, "System.WorkItemType") ?? "");
            if (type is null) continue;
            var requirement = type.Level == Levels.Requirement;
            var fields = s.FieldsOf(type.Name);
            if (requirement && fields.Any(f => f.Ref == WorkItems.AcceptanceRef) && Html.ToMarkdown(WorkItems.Field(w, WorkItems.AcceptanceRef)).Length == 0)
                Add("no acceptance criteria", w);
            if (Html.ToMarkdown(WorkItems.Field(w, WorkItems.DescriptionRef) ?? WorkItems.Field(w, WorkItems.ReproStepsRef)).Length == 0)
                Add("no description", w);
            if (requirement && estimate is not null && fields.Any(f => f.Ref == estimate) && Number(w, estimate) is null)
                Add("no estimate", w);
            if (requirement && hasPortfolio && !Json.Arr(w, "relations").Any(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Reverse"))
                Add("no parent (feature/epic)", w);
            if (Changed(w) is { } ch && (day - ch.ToLocalTime().Date).Days >= StaleBacklogDays && Category(s, w) != "Proposed")
                Add($"in progress, no change for {StaleBacklogDays}+ days", w, $"{(day - ch.ToLocalTime().Date).Days} days");
            if (WorkItems.Field(w, "System.IterationPath") is { } it && past.Contains(it))
                Add("left in a finished sprint", w, it[(it.LastIndexOf('\\') + 1)..]);
        }

        // Look-alike titles: most words shared.
        var titled = items.Select(w => (W: w, Words: Words(WorkItems.Field(w, "System.Title")))).Where(x => x.Words.Count >= 2).ToList();
        for (var i = 0; i < titled.Count; i++)
            for (var j = i + 1; j < titled.Count; j++)
            {
                var a = titled[i].Words;
                var b = titled[j].Words;
                var common = a.Intersect(b).Count();
                // "Sprint 202" vs "Sprint 203": a series, not a duplicate.
                var series = a.Count == b.Count && a.Except(b).Concat(b.Except(a)).All(w => w.All(char.IsDigit));
                if (!series && common >= 2 && common >= 0.8 * Math.Max(a.Count, b.Count))
                    Add("possible duplicates", titled[i].W, "#" + WorkItems.Id(titled[j].W));
            }

        // Finished recently without code: neither the item nor its tasks have a commit, changeset or pull request.
        var doneNoCode = new List<string>();
        if (done.Count > 0)
        {
            var since = day.AddDays(-DoneWithoutCodeDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var (doneIds, _) = await session.Work.WiqlAsync(name,
                $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN {typeList}" +
                $" AND [System.State] IN {InList(done)} AND [Microsoft.VSTS.Common.StateChangeDate] >= '{since}'" + area +
                " ORDER BY [Microsoft.VSTS.Common.StateChangeDate] DESC", 200, ct);
            var doneItems = doneIds.Count == 0 ? [] : await session.Work.GetAsync(doneIds, relations: true, ct: ct);
            var childIds = doneItems.SelectMany(w => Json.Arr(w, "relations")).Where(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Forward")
                .Select(r => Audit.TargetOf(Json.Str(r, "url")) ?? 0).Where(i => i > 0).Distinct().ToList();
            var children = childIds.Count == 0 ? [] : await session.Work.GetAsync(childIds, relations: true, ct: ct);
            var withCode = children.Where(HasCode).Select(WorkItems.Id).ToHashSet();
            foreach (var w in doneItems)
            {
                var kids = Json.Arr(w, "relations").Where(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Forward").Select(r => Audit.TargetOf(Json.Str(r, "url")) ?? 0);
                if (!HasCode(w) && !kids.Any(withCode.Contains)) doneNoCode.Add($"{WorkItemFormat.Header(w)}{Who(w)}");
            }
        }

        var sb = new StringBuilder($"backlog health of {t} ({name}): {items.Count}{(openIds.Count > items.Count ? $" most recently changed of {openIds.Count}" : "")} open " +
                                   $"{string.Join("/", types.Select(x => x.Name))} items");
        if (findings.Count == 0 && doneNoCode.Count == 0) return sb.Append(" - nothing to tidy").ToString();
        sb.AppendLine();
        void Rule(string title, List<string> list)
        {
            sb.AppendLine($"{title} ({list.Count}){(list.Count > ShownPerRule ? ", latest" : "")}:");
            foreach (var l in list.Take(ShownPerRule)) sb.AppendLine("- " + l);
        }
        // Items come newest first, so each rule shows the ones most likely still relevant.
        foreach (var (rule, list) in findings.OrderByDescending(f => f.Value.Count)) Rule(rule, list);
        if (doneNoCode.Count > 0) Rule($"done in the last {DoneWithoutCodeDays} days without linked code - fine for non-code work, else check with ado_trace", doneNoCode);
        return sb.Append("Ask before changing anything; ado_query lists all items of a rule.").ToString();
    }

    static bool HasCode(JsonElement wi) =>
        Json.Arr(wi, "relations").Any(r => Json.Str(r, "rel") == "ArtifactLink" && Json.Str(r, "url") is { } url &&
            (url.Contains("/VersionControl/", StringComparison.OrdinalIgnoreCase) || url.Contains("/Git/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Release notes in markdown: requirements and bugs finished in a sprint or since a date, grouped under their
    /// feature, ready for a wiki page.
    /// </summary>
    public static async Task<string> ReleaseNotesAsync(Lens lens, string? connection, string? project, string? team, string? sprint, string? since, string? until,
        DateTime? today = null, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var boards = new Boards(session);
        var t = await boards.TeamAsync(name, team, ct);
        var day = (today ?? DateTime.Now).Date;
        var s = (await session.Meta.GetAsync(name, ct)).Snapshot;
        var types = BacklogTypes(s);
        var done = types.SelectMany(x => x.States).Where(x => x.Category == "Completed").Select(x => x.Name).ToList();
        if (types.Count == 0 || done.Count == 0) return $"no requirement or bug types with a completed state in {name}";

        string title, condition;
        if (since is not null)
        {
            var from = Formatting.ParseSince(since, day);
            var to = until is null ? day : Formatting.ParseSince(until, day);
            title = $"{from:yyyy-MM-dd} - {to:yyyy-MM-dd}";
            condition = $" AND [Microsoft.VSTS.Common.StateChangeDate] >= '{from:yyyy-MM-dd}' AND [Microsoft.VSTS.Common.StateChangeDate] < '{to.AddDays(1):yyyy-MM-dd}'" +
                        await AreaAsync(boards, name, t, ct);
        }
        else
        {
            var it = Boards.Pick(await boards.IterationsAsync(name, t, ct), sprint, day);
            title = it.Name + (it.Start is { } a && it.Finish is { } b ? $" ({a:yyyy-MM-dd} - {b:yyyy-MM-dd})" : "");
            condition = $" AND [System.IterationPath] = {Quote(it.Path)}";
        }
        var (ids, _) = await session.Work.WiqlAsync(name,
            $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN {InList(types.Select(x => x.Name))}" +
            $" AND [System.State] IN {InList(done)}{condition} ORDER BY [System.Id]", 500, ct);
        if (ids.Count == 0) return $"nothing finished in {title} ({t})";
        var items = await session.Work.GetAsync(ids, relations: true, ct: ct);
        var parents = items.Select(w => Json.Arr(w, "relations").Where(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Reverse")
            .Select(r => Audit.TargetOf(Json.Str(r, "url")) ?? 0).FirstOrDefault()).Where(p => p > 0).Distinct().ToList();
        var parentTitles = parents.Count == 0 ? new Dictionary<int, string>()
            : (await session.Work.GetAsync(parents, relations: false, ["System.Id", "System.Title"], ct)).ToDictionary(WorkItems.Id, w => WorkItems.Field(w, "System.Title") ?? "");

        string Line(JsonElement w) => $"- {WorkItems.Field(w, "System.Title")?.Trim()} (#{WorkItems.Id(w)})";
        int ParentOf(JsonElement w) => Json.Arr(w, "relations").Where(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Reverse")
            .Select(r => Audit.TargetOf(Json.Str(r, "url")) ?? 0).FirstOrDefault();

        var sb = new StringBuilder($"# Release notes - {title}\n\n");
        var requirements = items.Where(w => Level(s, w) == Levels.Requirement).ToList();
        var bugs = items.Where(w => Level(s, w) == Levels.Bug).ToList();
        if (requirements.Count > 0)
        {
            sb.AppendLine("## New and changed\n");
            foreach (var g in requirements.GroupBy(ParentOf).OrderBy(g => g.Key == 0))
            {
                if (g.Key != 0 && parentTitles.TryGetValue(g.Key, out var pt)) sb.AppendLine($"**{pt.Trim()}**\n");
                else if (g.Key == 0 && requirements.Any(w => ParentOf(w) != 0)) sb.AppendLine("**Other**\n");
                foreach (var w in g) sb.AppendLine(Line(w));
                sb.AppendLine();
            }
        }
        if (bugs.Count > 0)
        {
            sb.AppendLine("## Fixed\n");
            foreach (var w in bugs) sb.AppendLine(Line(w));
            sb.AppendLine();
        }
        sb.Append($"({items.Count} items, {t}. Rewrite titles into user language before publishing; ado_wiki_write saves it as a page.)");
        return sb.ToString();
    }
}
