using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoLens.Tests;

public class BoardTests : IDisposable
{
    // Wednesday of the second week of sprint 2 (Mon 2026-10-05 .. Fri 2026-10-16).
    static readonly DateTime Today = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Local);

    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-boards-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly DemoItems items = new();
    readonly List<string> reorders = [];
    readonly Lens lens;

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public BoardTests()
    {
        var f1 = (JsonObject)items.Items[1]["fields"]!;
        f1["System.State"] = "Committed";
        f1["System.ChangedDate"] = "2026-10-06T10:00:00Z";
        f1["Microsoft.VSTS.Scheduling.Effort"] = 5;
        var f2 = (JsonObject)items.Items[2]["fields"]!;
        f2["Microsoft.VSTS.Scheduling.RemainingWork"] = 10;
        f2["System.ChangedDate"] = "2026-10-13T10:00:00Z";
        ((JsonObject)items.Items[4]["fields"]!)["System.ChangedDate"] = "2026-10-13T10:00:00Z";

        const string team = "/contoso/Demo/Demo Team/_apis/work/";
        var ado = items.Routes(demo.Routes(new FakeAdo()))
            .On(team + "teamsettings/iterations", """
                {"value":[
                  {"id":"s1","name":"Sprint 1","path":"Demo\\Sprint 1","attributes":{"startDate":"2026-09-21T00:00:00Z","finishDate":"2026-10-02T00:00:00Z","timeFrame":"past"}},
                  {"id":"s2","name":"Sprint 2","path":"Demo\\Sprint 2","attributes":{"startDate":"2026-10-05T00:00:00Z","finishDate":"2026-10-16T00:00:00Z","timeFrame":"current"}},
                  {"id":"s3","name":"Sprint 3","path":"Demo\\Sprint 3","attributes":{"startDate":"2026-10-19T00:00:00Z","finishDate":"2026-10-30T00:00:00Z","timeFrame":"future"}}]}
                """)
            .On(team + "teamsettings/iterations/s2/workitems", """
                {"workItemRelations":[
                  {"rel":null,"source":null,"target":{"id":1}},
                  {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":1},"target":{"id":2}},
                  {"rel":null,"source":null,"target":{"id":4}}]}
                """)
            .On(team + "teamsettings/iterations/s2/capacities", """
                {"teamMembers":[
                  {"teamMember":{"displayName":"Anna Nowak"},"activities":[{"capacityPerDay":6,"name":"Development"}],"daysOff":[]},
                  {"teamMember":{"displayName":"Ben Kowalski"},"activities":[{"capacityPerDay":4,"name":"Testing"}],
                   "daysOff":[{"start":"2026-10-15T00:00:00Z","end":"2026-10-15T00:00:00Z"}]},
                  {"teamMember":{"displayName":"Eva Kowalska"},"activities":[],"daysOff":[]}]}
                """)
            .On(team + "teamsettings/iterations/s2/teamdaysoff", """{"daysOff":[]}""")
            .On(team + "teamsettings/teamfieldvalues", """{"field":{"referenceName":"System.AreaPath"},"values":[{"value":"Demo\\Portal","includeChildren":true}]}""")
            .On(team + "teamsettings", """{"workingDays":["monday","tuesday","wednesday","thursday","friday"]}""")
            .On(team + "boards/b1", """
                {"id":"b1","name":"Backlog items",
                 "fields":{"columnField":{"referenceName":"WEF_ABC_Kanban.Column"},"doneField":{"referenceName":"WEF_ABC_Kanban.Column.Done"}},
                 "columns":[
                   {"name":"New","columnType":"incoming","itemLimit":0,"stateMappings":{"Product Backlog Item":"New","Bug":"New"}},
                   {"name":"Doing","columnType":"inProgress","itemLimit":1,"isSplit":true,"stateMappings":{"Product Backlog Item":"Committed","Bug":"New"}},
                   {"name":"Done","columnType":"outgoing","itemLimit":0,"stateMappings":{"Product Backlog Item":"Done","Bug":"Done"}}]}
                """)
            .On(team + "boards", """{"value":[{"id":"b1","name":"Backlog items"}]}""")
            .On(team + "backlogs/Microsoft.RequirementCategory/workItems", """
                {"workItems":[
                  {"source":null,"target":{"id":1}},{"source":{"id":1},"target":{"id":2}},{"source":null,"target":{"id":4}},{"source":null,"target":{"id":3}}]}
                """)
            .On(team + "backlogs", """{"value":[{"id":"Microsoft.RequirementCategory","name":"Backlog items","type":"requirement"},{"id":"Microsoft.FeatureCategory","name":"Features","type":"portfolio"}]}""")
            .On(team + "workitemsorder", r => { reorders.Add(r.Content!.ReadAsStringAsync().Result); return Json("""{"value":[]}"""); })
            .On("/contoso/_apis/connectionData", """{"authenticatedUser":{"id":"me-1","providerDisplayName":"Anna Nowak"}}""")
            .On("/contoso/Demo/_apis/git/pullrequests", """
                {"value":[
                  {"pullRequestId":482,"title":"Login form","isDraft":false,"createdBy":{"displayName":"Ben Kowalski"},"repository":{"name":"portal"},
                   "sourceRefName":"refs/heads/feature/1-login","targetRefName":"refs/heads/dev","reviewers":[{"id":"me-1","vote":0}]},
                  {"pullRequestId":483,"title":"Already approved","isDraft":false,"createdBy":{"displayName":"Ben Kowalski"},"repository":{"name":"portal"},
                   "sourceRefName":"refs/heads/x","targetRefName":"refs/heads/dev","reviewers":[{"id":"me-1","vote":10}]}]}
                """);
        var s = new Settings { DefaultConnection = "contoso" };
        s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
        lens = new Lens(s, "0.0.0", dir, _ => null, _ => "pat", _ => ado.Client());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Sprint_shows_progress_capacity_and_what_needs_attention()
    {
        var text = (await BoardQueries.SprintAsync(lens, null, null, null, null, null, Today)).Replace("\r", "");

        Assert.StartsWith("Sprint 2 (Demo Team): 2026-10-05 - 2026-10-16, working day 8 of 10, 2 left after today\n", text);
        Assert.Contains("items: 2 (done 0, in progress 1, not started 1); effort 0 of 5 done", text);
        // today .. Friday: Anna 3 days x 6 h, Ben 2 days x 4 h (one day off); Eva has no capacity set
        Assert.Contains("remaining work 10 h, capacity left 26 h (2 people) - fits, 16 h spare", text);
        Assert.Contains("#1 Product Backlog Item [Committed] Login page (Anna Nowak): no change for 8 days", text);
        Assert.Contains("#4 Bug [New] Error on save: unassigned", text);
        Assert.Contains("#1 Product Backlog Item [Committed] Login page (Anna Nowak) | 5 | tasks 0/1 done, 10 h left", text);
        Assert.DoesNotContain("not started (1):\n  #2", text);
    }

    [Theory]
    [InlineData("next", "Sprint 3")]
    [InlineData("previous", "Sprint 1")]
    [InlineData("sprint 1", "Sprint 1")]
    public void Sprints_are_picked_by_word_or_name(string which, string expected)
    {
        var all = new List<Iteration>
        {
            new("s1", "Sprint 1", "Demo\\Sprint 1", new(2026, 9, 21), new(2026, 10, 2), "past"),
            new("s2", "Sprint 2", "Demo\\Sprint 2", new(2026, 10, 5), new(2026, 10, 16), "current"),
            new("s3", "Sprint 3", "Demo\\Sprint 3", new(2026, 10, 19), new(2026, 10, 30), "future"),
        };
        Assert.Equal(expected, Boards.Pick(all, which, Today.Date).Name);
    }

    [Fact]
    public void An_old_current_sprint_is_reported_as_ended()
    {
        var all = new List<Iteration> { new("s", "Sprint 12", "Demo\\Sprint 12", new(2023, 12, 11), new(2023, 12, 22), "current") };
        Assert.Equal("Sprint 12", Boards.Pick(all, null, Today.Date).Name);
    }

    [Fact]
    public async Task Board_counts_cards_per_column_with_wip_limits()
    {
        // The query returns the bug (New) and a task; tasks have no column on the requirement board.
        var text = await BoardQueries.BoardAsync(lens, null, null, null, null, null, null, "summary");
        Assert.Equal("Backlog items board (Demo Team): New 1 | Doing 0/1 | Done 0 (last 14 days)", text.Trim());
    }

    [Fact]
    public async Task Moving_a_card_sets_the_column_and_the_state_mapped_for_its_type()
    {
        // #1 is Committed: "Doing" maps to the same state, so only the column (and its Doing/Done half) changes.
        var text = await BoardQueries.BoardAsync(lens, null, null, null, null, 1, "doing", null);
        var patch = items.Patches[0].Body;
        Assert.Contains("\"/fields/WEF_ABC_Kanban.Column\",\"value\":\"Doing\"", patch);
        Assert.Contains("\"/fields/WEF_ABC_Kanban.Column.Done\",\"value\":false", patch);
        Assert.DoesNotContain("System.State", patch);
        Assert.StartsWith("moved #1 Product Backlog Item [Committed] Login page to Doing", text);

        // Back to "New": the state follows the column's mapping for the type.
        await BoardQueries.BoardAsync(lens, null, null, null, null, 1, "New", null);
        Assert.Contains("\"/fields/System.State\",\"value\":\"New\"", items.Patches[1].Body);

        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => BoardQueries.BoardAsync(lens, null, null, null, null, 2, "Doing", null));
        Assert.Contains("Task items are not on the Backlog items board", wrong.Message);
    }

    [Fact]
    public async Task Backlog_lists_priority_order_and_reorders()
    {
        var list = (await BoardQueries.BacklogAsync(lens, null, null, null, null, 30, null, null, null)).Replace("\r", "");
        Assert.StartsWith("Backlog items backlog (Demo Team): 3 in priority order\n1. #1 Product Backlog Item", list);
        Assert.Contains("2. #4 Bug [New] Error on save", list);
        Assert.Contains("3. #3 Feature [New] Portal", list);

        var moved = await BoardQueries.BacklogAsync(lens, null, null, null, null, 30, 3, 4, null);
        Assert.Contains("moved #3 Feature [New] Portal above #4 Bug [New] Error on save (position 2 of 3)", moved);
        Assert.Contains("\"previousId\":1,\"nextId\":4", reorders.Single());

        await BoardQueries.BacklogAsync(lens, null, null, null, null, 30, 1, null, 3);
        Assert.Contains("\"previousId\":3,\"nextId\":0", reorders[1]);
    }

    [Fact]
    public async Task My_day_lists_mentions_changes_and_reviews_without_repeats()
    {
        var text = (await BoardQueries.MyDayAsync(lens, null, null, null, Today)).Replace("\r", "");

        Assert.StartsWith("Anna Nowak - since 2026-10-13:\nmentioned you (2):", text);
        Assert.Contains("your items changed by others: none", text);
        Assert.Contains("pull requests waiting for your review (1):\n  PR 482 Login form - Ben Kowalski, portal: feature/1-login -> dev", text);
        Assert.DoesNotContain("Already approved", text);
    }

    [Fact]
    public void Working_days_skip_weekends_and_days_off()
    {
        var week = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        Assert.Equal(10, Boards.WorkingDays(new(2026, 10, 5), new(2026, 10, 16), week, []));
        Assert.Equal(9, Boards.WorkingDays(new(2026, 10, 5), new(2026, 10, 16), week, [(new(2026, 10, 7), new(2026, 10, 7))]));
    }
}
