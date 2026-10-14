using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdoLens;

/// <summary>Acceptance tests: Test Cases written from criteria, and how the tests of an item or a sprint stand.</summary>
public static partial class TestCases
{
    public const string StepsRef = "Microsoft.VSTS.TCM.Steps";
    const string TestedBy = "Microsoft.VSTS.Common.TestedBy-Forward";
    const string Tests = "Microsoft.VSTS.Common.TestedBy-Reverse";

    public sealed record Case(string Title, List<(string Action, string Expected)> Steps);

    [GeneratedRegex(@"^\s*#{1,3}\s+(?<title>.+?)\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(?:\d+[.)]|[-*])\s+(?<step>.+?)\s*$")]
    private static partial Regex StepLine();

    [GeneratedRegex(@"\s*(?:=>|->|→|\|)\s*")]
    private static partial Regex Arrow();

    /// <summary>
    /// "## Title" starts a case; "1. action => expected" (also "->", "→" or "|") is a step; a step without an expected
    /// result is fine.
    /// </summary>
    public static List<Case> Parse(string markdown)
    {
        var cases = new List<Case>();
        Case? current = null;
        foreach (var line in markdown.Replace("\r", "").Split('\n'))
        {
            if (Heading().Match(line) is { Success: true } h) cases.Add(current = new Case(h.Groups["title"].Value, []));
            else if (StepLine().Match(line) is { Success: true } s)
            {
                if (current is null) throw new ArgumentException("start each test case with a heading: '## <title>', then numbered steps '1. action => expected'");
                var parts = Arrow().Split(s.Groups["step"].Value, 2);
                current.Steps.Add((parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : ""));
            }
        }
        if (cases.Count == 0) throw new ArgumentException("no test cases: write '## <title>' and numbered steps '1. action => expected'");
        if (cases.FirstOrDefault(c => c.Steps.Count == 0) is { } empty) throw new ArgumentException($"test case '{empty.Title}' has no steps");
        return cases;
    }

    /// <summary>The Steps field of a Test Case: XML whose step texts are HTML.</summary>
    public static string StepsXml(IReadOnlyList<(string Action, string Expected)> steps)
    {
        static XElement Text(string s) => new("parameterizedString", new XAttribute("isformatted", "true"),
            string.IsNullOrEmpty(s) ? "<DIV><P></P></DIV>" : $"<DIV><P>{WebUtility.HtmlEncode(s)}</P></DIV>");
        var root = new XElement("steps", new XAttribute("id", "0"), new XAttribute("last", (steps.Count + 1).ToString(CultureInfo.InvariantCulture)));
        for (var i = 0; i < steps.Count; i++)
            root.Add(new XElement("step", new XAttribute("id", (i + 2).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("type", string.IsNullOrEmpty(steps[i].Expected) ? "ActionStep" : "ValidateStep"),
                Text(steps[i].Action), Text(steps[i].Expected), new XElement("description")));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The steps of a Test Case as plain text.</summary>
    public static List<(string Action, string Expected)> ReadSteps(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        try
        {
            return XDocument.Parse(xml).Descendants("step").Select(s =>
            {
                var texts = s.Elements("parameterizedString").Select(p => Formatting.Collapse(p.Value, 300)).ToList();
                return (texts.ElementAtOrDefault(0) ?? "", texts.ElementAtOrDefault(1) ?? "");
            }).ToList();
        }
        catch (System.Xml.XmlException) { return []; }
    }

    /// <summary>
    /// Creates Test Cases for a work item from markdown, linked as "tested by", in the item's area and iteration;
    /// optionally added to a test suite (plan and suite by id or name).
    /// </summary>
    public static async Task<string> CreateAsync(Lens lens, string? connection, int id, string cases, string? plan, string? suite, CancellationToken ct = default)
    {
        var parsed = Parse(cases);
        var session = lens.For(connection);
        var work = session.Work;
        var items = await work.GetAsync([id], relations: false, ct: ct);
        if (items.Count == 0) return $"no work item #{id} visible with this PAT";
        var item = items[0];
        var project = WorkItems.Field(item, "System.TeamProject")!;
        var s = (await session.Meta.GetAsync(project, ct)).Snapshot;
        var type = s.Types.FirstOrDefault(t => !t.Disabled && s.FieldsOf(t.Name).Any(f => f.Ref == StepsRef) && t.Name.Contains("Test Case", StringComparison.OrdinalIgnoreCase))
                   ?? s.Types.FirstOrDefault(t => !t.Disabled && s.FieldsOf(t.Name).Any(f => f.Ref == StepsRef))
                   ?? throw new InvalidOperationException($"{project} has no Test Case type (Test Plans may be off for this project)");

        (int Plan, int Suite)? target = plan is null && suite is null ? null : await SuiteAsync(session.Client, project, plan, suite, ct);
        var batch = Audit.NewBatch();
        var created = new List<string>();
        foreach (var c in parsed)
        {
            var ops = new List<object>
            {
                new { op = "add", path = "/fields/System.Title", value = c.Title },
                new { op = "add", path = "/fields/" + StepsRef, value = StepsXml(c.Steps) },
                new { op = "add", path = "/relations/-", value = new { rel = Tests, url = $"{work.WebBase}_apis/wit/workItems/{id}" } },
            };
            foreach (var f in new[] { "System.AreaPath", "System.IterationPath" })
                if (WorkItems.Field(item, f) is { } v) ops.Add(new { op = "add", path = "/fields/" + f, value = v });
            var tc = await work.CreateAsync(project, type.Name, ops, ct);
            var tcId = WorkItems.Id(tc);
            Audit.Write(lens, session.Config.Connection, [new AuditEntry { Batch = batch, Tool = "ado_test_create", Kind = "create", Id = tcId, Project = project, Title = c.Title }]);
            if (target is { } t)
                await session.Client.PostAsync($"{AdoClient.Scope(project)}_apis/testplan/Plans/{t.Plan}/Suites/{t.Suite}/TestCase",
                    new[] { new { workItem = new { id = tcId } } }, ct);
            created.Add($"#{tcId} {c.Title} ({c.Steps.Count} steps)");
        }
        return $"created {created.Count} test case(s) for {WorkItemFormat.Header(item)}{(target is { } x ? $", added to suite {x.Suite} of plan {x.Plan}" : "")}:\n" +
               string.Join('\n', created.Select(l => "- " + l));
    }

    static async Task<(int Plan, int Suite)> SuiteAsync(AdoClient client, string project, string? plan, string? suite, CancellationToken ct)
    {
        if (plan is null || suite is null) throw new ArgumentException("give both plan and suite (id or name), or neither");
        var scope = AdoClient.Scope(project);
        int planId;
        if (!int.TryParse(plan, out planId))
        {
            var plans = await client.GetListAsync($"{scope}_apis/testplan/plans?filterActivePlans=true", ct);
            var hit = plans.FirstOrDefault(p => string.Equals(Json.Str(p, "name"), plan.Trim(), StringComparison.OrdinalIgnoreCase));
            planId = hit.ValueKind == JsonValueKind.Object && hit.TryGetProperty("id", out var pid) ? pid.GetInt32()
                : throw new InvalidOperationException($"no active test plan '{plan}'; plans: {string.Join(", ", plans.Select(p => Json.Str(p, "name")))}");
        }
        if (int.TryParse(suite, out var suiteId)) return (planId, suiteId);
        var suites = await client.GetListAsync($"{scope}_apis/testplan/Plans/{planId}/suites", ct);
        var s = suites.FirstOrDefault(x => string.Equals(Json.Str(x, "name"), suite.Trim(), StringComparison.OrdinalIgnoreCase));
        return s.ValueKind == JsonValueKind.Object && s.TryGetProperty("id", out var sid) ? (planId, sid.GetInt32())
            : throw new InvalidOperationException($"no suite '{suite}' in plan {planId}; suites: {string.Join(", ", suites.Select(x => Json.Str(x, "name")))}");
    }

    sealed record Point(int TestCase, string Outcome, string? Plan, string? Suite, string? Configuration, DateTime? When, string? Tester);

    static async Task<List<Point>> PointsAsync(AdoClient client, string project, IReadOnlyCollection<int> testCases, CancellationToken ct)
    {
        if (testCases.Count == 0) return [];
        JsonElement root;
        try
        {
            root = await client.PostAsync($"{AdoClient.Scope(project)}_apis/test/points", new { pointsFilter = new { testcaseIds = testCases } }, ct);
        }
        catch (AdoApiException) { return []; }
        var list = root.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray() : Json.Arr(root, "value");
        return list.Select(x =>
        {
            // The id is a string in this API ("123"), a number in others.
            var tc = Json.Obj(x, "testCase").TryGetProperty("id", out var idEl)
                ? idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : int.TryParse(idEl.GetString(), out var i) ? i : 0
                : 0;
            var outcome = Json.Str(x, "outcome") is { Length: > 0 } o && o != "Unspecified" ? o : "Not run";
            var when = DateTime.TryParse(Json.Str(Json.Obj(x, "lastResultDetails"), "dateCompleted") ?? Json.Str(x, "lastUpdatedDate"),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : (DateTime?)null;
            return new Point(tc, outcome, Json.Str(Json.Obj(x, "testPlan"), "name"), Json.Str(Json.Obj(x, "suite"), "name"),
                Json.Str(Json.Obj(x, "configuration"), "name"), when,
                Json.Str(Json.Obj(Json.Obj(x, "lastResultDetails"), "runBy"), "displayName") ?? Json.Str(Json.Obj(x, "assignedTo"), "displayName"));
        }).Where(x => x.TestCase > 0).ToList();
    }

    static IEnumerable<int> Linked(JsonElement wi, string rel) =>
        Json.Arr(wi, "relations").Where(r => Json.Str(r, "rel") == rel).Select(r => Audit.TargetOf(Json.Str(r, "url")) ?? 0).Where(i => i > 0);

    /// <summary>
    /// The tests of work items (ids) or of the items of a sprint: the Test Cases that test each one and their latest
    /// outcome in every plan and suite.
    /// </summary>
    public static async Task<string> StatusAsync(Lens lens, string? connection, string? ids, string? sprint, string? team, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var work = session.Work;
        List<int> wanted;
        string title;
        if (WorkQueries.ParseIds(ids) is { Count: > 0 } list) { wanted = list; title = ""; }
        else
        {
            var project = session.Project(null);
            var boards = new Boards(session);
            var t = await boards.TeamAsync(project, team, ct);
            var it = Boards.Pick(await boards.IterationsAsync(project, t, ct), sprint, DateTime.Now.Date);
            var s = (await session.Meta.GetAsync(project, ct)).Snapshot;
            var (sprintIds, _) = await boards.SprintItemsAsync(project, t, it, ct);
            var heads = await work.GetAsync(sprintIds, relations: false, ["System.Id", "System.WorkItemType"], ct);
            wanted = heads.Where(w => s.FindType(WorkItems.Field(w, "System.WorkItemType") ?? "")?.Level is Levels.Requirement or Levels.Bug).Select(WorkItems.Id).ToList();
            title = $"tests of {it.Name} ({t}): ";
            if (wanted.Count == 0) return title + "no requirements or bugs in this sprint";
        }

        var items = await work.GetAsync(wanted, relations: true, ct: ct);
        if (items.Count == 0) return "no work items visible with this PAT";
        var caseIds = items.SelectMany(w => Linked(w, TestedBy)).Concat(items.Where(w => WorkItems.Field(w, StepsRef) is not null || Json.Arr(w, "relations").Any(r => Json.Str(r, "rel") == Tests))
            .Select(WorkItems.Id)).Distinct().ToList();
        var cases = caseIds.Count == 0 ? new Dictionary<int, JsonElement>() : (await work.GetAsync(caseIds, relations: false, ct: ct)).ToDictionary(WorkItems.Id);
        var byProject = cases.Values.GroupBy(c => WorkItems.Field(c, "System.TeamProject") ?? "");
        var points = new List<Point>();
        foreach (var g in byProject) points.AddRange(await PointsAsync(session.Client, g.Key, g.Select(WorkItems.Id).ToList(), ct));
        var pointsOf = points.GroupBy(p => p.TestCase).ToDictionary(g => g.Key, g => g.ToList());
        var full = detail == "full";

        string Outcome(int tc) => pointsOf.TryGetValue(tc, out var ps) && ps.Count > 0
            ? string.Join("; ", ps.OrderByDescending(p => p.When).Select(p => $"{p.Outcome}{(p.When is { } w ? $" {w:yyyy-MM-dd}" : "")}" +
                $"{(p.Tester is null ? "" : $" {p.Tester}")} ({p.Plan} / {p.Suite}{(p.Configuration is null ? "" : $", {p.Configuration}")})"))
            : "not in any test plan";
        string Bucket(int tc) => pointsOf.TryGetValue(tc, out var ps) && ps.Count > 0
            ? ps.OrderByDescending(p => p.When).First().Outcome switch { "Passed" => "passed", "Failed" => "failed", "Blocked" => "blocked", _ => "not run" }
            : "not planned";

        var sb = new StringBuilder(title);
        var untested = new List<string>();
        var summary = new Dictionary<string, int>();
        foreach (var w in items)
        {
            var isCase = cases.ContainsKey(WorkItems.Id(w)) && !Linked(w, TestedBy).Any();
            var tcs = isCase ? [WorkItems.Id(w)] : Linked(w, TestedBy).Where(cases.ContainsKey).ToList();
            if (tcs.Count == 0) { untested.Add($"#{WorkItems.Id(w)} {Formatting.Collapse(WorkItems.Field(w, "System.Title") ?? "", 50)}"); continue; }
            var buckets = tcs.GroupBy(Bucket).ToDictionary(g => g.Key, g => g.Count());
            foreach (var (k, v) in buckets) summary[k] = summary.GetValueOrDefault(k) + v;
            sb.AppendLine($"{WorkItemFormat.Header(w)}: {tcs.Count} test case(s) - {string.Join(", ", buckets.Select(b => $"{b.Key} {b.Value}"))}");
            if (title.Length > 0 && !full && !buckets.Keys.Any(k => k is "failed" or "blocked")) continue;
            foreach (var tc in tcs)
            {
                sb.AppendLine($"  - #{tc} {WorkItems.Field(cases[tc], "System.Title")}: {Outcome(tc)}");
                if (full)
                    foreach (var (step, i) in ReadSteps(WorkItems.Field(cases[tc], StepsRef)).Select((x, i) => (x, i + 1)))
                        sb.AppendLine($"    {i}. {step.Action}{(step.Expected.Length > 0 ? $" => {step.Expected}" : "")}");
            }
        }
        if (title.Length > 0 && summary.Count > 0) sb.Insert(title.Length, string.Join(", ", summary.Select(x => $"{x.Key} {x.Value}")) + "\n");
        if (untested.Count > 0) sb.AppendLine($"without test cases ({untested.Count}): {string.Join("; ", untested.Take(10))}{(untested.Count > 10 ? "; …" : "")}");
        return sb.ToString().TrimEnd();
    }
}
