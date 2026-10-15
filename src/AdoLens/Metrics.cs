using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AdoLens;

/// <summary>Team flow for a Product Owner: velocity and throughput per sprint, cycle time, what is stuck longest.</summary>
public static partial class BoardQueries
{
    const string ActivatedRef = "Microsoft.VSTS.Common.ActivatedDate";
    const string ClosedRef = "Microsoft.VSTS.Common.ClosedDate";
    const string StateChangeRef = "Microsoft.VSTS.Common.StateChangeDate";

    static DateTime? DateField(JsonElement wi, string reference) =>
        DateTime.TryParse(WorkItems.Field(wi, reference), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var i = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }

    public static async Task<string> MetricsAsync(Lens lens, string? connection, string? project, string? team, int sprints, DateTime? today = null, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var boards = new Boards(session);
        var t = await boards.TeamAsync(name, team, ct);
        var day = (today ?? DateTime.Now).Date;
        var s = (await session.Meta.GetAsync(name, ct)).Snapshot;
        var estimate = Estimates.FirstOrDefault(e => s.Fields.ContainsKey(e));
        sprints = Math.Clamp(sprints, 1, 12);

        var sb = new StringBuilder($"flow of {t} ({name})\n");

        // Per finished sprint: what was done in it (requirements and bugs), and their estimate.
        var past = (await boards.IterationsAsync(name, t, ct)).Where(i => i.Finish is { } f && f < day).OrderByDescending(i => i.Finish).Take(sprints).Reverse().ToList();
        if (past.Count > 0)
        {
            sb.AppendLine(estimate is null ? "sprint | done items" : $"sprint | done items | done {s.Fields[estimate].Name.ToLowerInvariant()}");
            var counts = new List<double>();
            var points = new List<double>();
            foreach (var it in past)
            {
                var (ids, parent) = await boards.SprintItemsAsync(name, t, it, ct);
                var top = ids.Where(i => !parent.ContainsKey(i)).ToList();
                var fields = new List<string> { "System.Id", "System.WorkItemType", "System.State" };
                if (estimate is not null) fields.Add(estimate);
                var items = top.Count == 0 ? [] : await session.Work.GetAsync(top, relations: false, fields, ct);
                var done = items.Where(w => Category(s, w) == "Completed" && Level(s, w) is Levels.Requirement or Levels.Bug).ToList();
                counts.Add(done.Count);
                var sum = estimate is null ? 0 : done.Sum(w => Number(w, estimate) ?? 0);
                points.Add(sum);
                sb.AppendLine($"{it.Name} | {done.Count}{(estimate is null ? "" : $" | {Num(sum)}")}");
            }
            sb.Append($"average: {Num(Math.Round(counts.Average(), 1))} items");
            if (estimate is not null) sb.Append($", {Num(Math.Round(points.Average(), 1))} {s.Fields[estimate].Name.ToLowerInvariant()}");
            sb.AppendLine(" per sprint");
        }
        else sb.AppendLine("no finished sprints of the team");

        // Cycle time: from start of work to done, for what was finished in the period of those sprints.
        var since = past.FirstOrDefault()?.Start ?? day.AddDays(-90);
        var types = BacklogTypes(s);
        var doneStates = types.SelectMany(x => x.States).Where(x => x.Category == "Completed").Select(x => x.Name).ToList();
        var area = await AreaAsync(boards, name, t, ct);
        if (types.Count > 0 && doneStates.Count > 0)
        {
            var (doneIds, _) = await session.Work.WiqlAsync(name,
                $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN {InList(types.Select(x => x.Name))}" +
                $" AND [System.State] IN {InList(doneStates)} AND [{StateChangeRef}] >= '{since:yyyy-MM-dd}'" + area, 1000, ct);
            var finished = doneIds.Count == 0 ? [] : await session.Work.GetAsync(doneIds, relations: false,
                ["System.Id", "System.WorkItemType", "System.Title", "System.CreatedDate", ActivatedRef, ClosedRef, StateChangeRef], ct);
            var cycle = finished.Select(w => (Start: DateField(w, ActivatedRef), End: DateField(w, ClosedRef) ?? DateField(w, StateChangeRef)))
                .Where(x => x.Start is not null && x.End is not null && x.End >= x.Start).Select(x => (x.End!.Value - x.Start!.Value).TotalDays).Order().ToList();
            var lead = finished.Select(w => (Start: DateField(w, "System.CreatedDate"), End: DateField(w, ClosedRef) ?? DateField(w, StateChangeRef)))
                .Where(x => x.Start is not null && x.End is not null && x.End >= x.Start).Select(x => (x.End!.Value - x.Start!.Value).TotalDays).Order().ToList();
            if (cycle.Count > 0)
                sb.AppendLine($"cycle time (work started -> done), {cycle.Count} items since {since:yyyy-MM-dd}: median {Num(Math.Round(Percentile(cycle, 0.5), 1))} days, 85% within {Num(Math.Round(Percentile(cycle, 0.85), 1))} days");
            if (lead.Count > 0)
                sb.AppendLine($"lead time (created -> done): median {Num(Math.Round(Percentile(lead, 0.5), 1))} days, 85% within {Num(Math.Round(Percentile(lead, 0.85), 1))} days");
        }

        // In progress for longest.
        var inProgress = types.SelectMany(x => x.States).Where(x => x.Category is "InProgress" or "Resolved").Select(x => x.Name).ToList();
        if (inProgress.Count > 0)
        {
            var (openIds, _) = await session.Work.WiqlAsync(name,
                $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN {InList(types.Select(x => x.Name))}" +
                $" AND [System.State] IN {InList(inProgress)}" + area + $" ORDER BY [{ActivatedRef}] ASC", 1000, ct);
            var open = openIds.Count == 0 ? [] : await session.Work.GetAsync(openIds.Take(200).ToList(), relations: false,
                ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.AssignedTo", ActivatedRef, "System.CreatedDate"], ct);
            var aged = open.Select(w => (W: w, Since: DateField(w, ActivatedRef) ?? DateField(w, "System.CreatedDate")))
                .Where(x => x.Since is not null).OrderBy(x => x.Since).Take(5).ToList();
            if (aged.Count > 0)
            {
                sb.AppendLine($"in progress longest ({openIds.Count}{(openIds.Count >= 1000 ? "+" : "")} in progress):");
                foreach (var (w, start) in aged) sb.AppendLine($"- {WorkItemFormat.Header(w)}{Who(w)}: {(day - start!.Value.ToLocalTime().Date).Days} days");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
