using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoLens.Tests;

/// <summary>Work items of the Demo project, served by FakeAdo; patches are applied and recorded.</summary>
sealed class DemoItems
{
    public readonly Dictionary<int, JsonObject> Items = new()
    {
        [1] = Item(1, "Product Backlog Item", "New", "Login page", """
            "System.AssignedTo":{"displayName":"Anna Nowak","uniqueName":"anna@contoso.com"},
            "System.AreaPath":"Demo\\Portal","System.IterationPath":"Demo\\Sprint 6","System.Tags":"UAT",
            "WEF_ABC_Kanban.Column":"Doing","System.Watermark":42,"Custom.Customer":"Alpha","System.CommentCount":2,
            "System.Description":"<div><b>Goal:</b> users sign in with their company account.</div>",
            "Microsoft.VSTS.Common.AcceptanceCriteria":"<ul><li>user can sign in</li><li>wrong password shows an error</li></ul>"
            """, """
            {"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/3","attributes":{}},
            {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/2","attributes":{}},
            {"rel":"ArtifactLink","url":"vstfs:///VersionControl/Changeset/523","attributes":{"name":"Fixed in Changeset","comment":"Fix login"}},
            {"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/p1%2Fr1%2F482","attributes":{"name":"Pull Request"}},
            {"rel":"ArtifactLink","url":"vstfs:///Build/Build/900","attributes":{"name":"Integrated in build","comment":"This build was automatically linked to this work item based on this work item's linked commits"}}
            """),
        [2] = Item(2, "Task", "To Do", "Build the form", "", ""),
        [3] = Item(3, "Feature", "New", "Portal", "", ""),
        [4] = Item(4, "Bug", "New", "Error on save", "", ""),
    };

    public readonly List<(int Id, string Body)> Patches = [];
    public readonly List<string> Created = [];
    public readonly List<string> Comments = [];

    static JsonObject Item(int id, string type, string state, string title, string fields, string relations) =>
        (JsonObject)JsonNode.Parse($$"""
            {"id":{{id}},"fields":{"System.Id":{{id}},"System.TeamProject":"Demo","System.WorkItemType":"{{type}}",
             "System.State":"{{state}}","System.Title":"{{title}}"{{(fields.Length > 0 ? "," + fields : "")}}},
             "relations":[{{relations}}]}
            """)!;

    static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static string Body(HttpRequestMessage r) => r.Content!.ReadAsStringAsync().Result;

    public FakeAdo Routes(FakeAdo ado) => ado
        .On("/contoso/_apis/wit/workitemsbatch", r =>
        {
            var body = JsonNode.Parse(Body(r))!;
            var ids = body["ids"]!.AsArray().Select(i => (int)i!).Where(Items.ContainsKey);
            return Json(new JsonObject { ["value"] = new JsonArray(ids.Select(i => (JsonNode)Items[i].DeepClone()).ToArray()) }.ToJsonString());
        })
        .On("/contoso/_apis/wit/workitems/", r =>
        {
            var id = int.Parse(r.RequestUri!.AbsolutePath.Split('/')[^1]);
            var body = Body(r);
            lock (Patches) Patches.Add((id, body));
            var item = (JsonObject)Items[id].DeepClone();
            foreach (var op in JsonNode.Parse(body)!.AsArray())
            {
                var path = (string)op!["path"]!;
                if (path.StartsWith("/fields/") && (string)op["op"]! == "add") item["fields"]![path[8..]] = op["value"]!.DeepClone();
            }
            return Json(item.ToJsonString());
        })
        .On("/contoso/Demo/_apis/wit/workitems/$", r =>
        {
            var body = Body(r);
            Created.Add(body);
            var type = Uri.UnescapeDataString(r.RequestUri!.AbsolutePath.Split('$')[^1]);
            var item = Item(10, type, "New", "x", "", "");
            foreach (var op in JsonNode.Parse(body)!.AsArray())
                if (((string)op!["path"]!).StartsWith("/fields/")) item["fields"]![((string)op["path"]!)[8..]] = op["value"]!.DeepClone();
            return Json(item.ToJsonString());
        })
        .On("/contoso/Demo/_apis/wit/workItems/1/comments", r =>
        {
            if (r.Method == HttpMethod.Post) { Comments.Add(Body(r)); return Json("""{"id":1}"""); }
            return Json("""
                {"comments":[
                  {"text":"<div>Second</div>","createdBy":{"displayName":"Ben Kowalski"},"createdDate":"2026-10-02T10:00:00Z"},
                  {"text":"<div>First <b>idea</b></div>","createdBy":{"displayName":"Anna Nowak"},"createdDate":"2026-10-01T10:00:00Z"}]}
                """);
        })
        .On("/contoso/_apis/identities", r => Json(r.RequestUri!.Query.Contains("anna", StringComparison.OrdinalIgnoreCase)
            ? """{"value":[{"id":"p-anna","providerDisplayName":"Anna Nowak","properties":{"Mail":{"$value":"anna@contoso.com"},"Account":{"$value":"anna@contoso.com"}}}]}"""
            : r.RequestUri!.Query.Contains("kowal", StringComparison.OrdinalIgnoreCase)
                ? """{"value":[{"id":"p1","providerDisplayName":"Ben Kowalski"},{"id":"p2","providerDisplayName":"Eva Kowalska"}]}"""
                : """{"value":[]}"""))
        .On("/contoso/Demo/Demo Team/_apis/wit/wiql", """{"workItems":[{"id":4},{"id":2}],"columns":[{"referenceName":"System.Id"},{"referenceName":"Custom.Customer"}]}""");
}

public class WorkItemTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-work-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly DemoItems items = new();
    readonly Lens lens;

    public WorkItemTests()
    {
        var ado = items.Routes(demo.Routes(new FakeAdo()));
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
    public async Task Item_is_compact_with_links_code_and_markdown()
    {
        var text = await WorkQueries.ItemAsync(lens, null, "#1", null, null);

        Assert.StartsWith("#1 Product Backlog Item [New] Login page\nhttps://dev.azure.com/contoso/Demo/_workitems/edit/1", text.Replace("\r", ""));
        Assert.Contains("assigned: Anna Nowak | area: Portal | iteration: Sprint 6 | tags: UAT | board: Doing", text);
        Assert.Contains("Customer: Alpha", text);
        Assert.Contains("**Goal:** users sign in", text);
        Assert.Contains("- user can sign in", text);
        Assert.Contains("parent: #3 Feature [New] Portal", text);
        Assert.Contains("#2 Task [To Do] Build the form", text);
        Assert.Contains("code (3): changeset 523 \"Fix login\"; PR 482; build 900", text);
        Assert.Contains("comments: 2 (include=comments)", text);
        Assert.DoesNotContain("WEF_", text);
        Assert.DoesNotContain("Watermark", text);
        Assert.True(text.Length < 900, $"{text.Length} characters");
    }

    [Fact]
    public async Task Comments_come_oldest_first_as_markdown()
    {
        var text = await WorkQueries.ItemAsync(lens, null, "1", "comments", "summary");
        var first = text.IndexOf("Anna Nowak: First **idea**", StringComparison.Ordinal);
        Assert.True(first > 0 && first < text.IndexOf("Ben Kowalski: Second", StringComparison.Ordinal), text);
    }

    [Fact]
    public async Task Create_checks_required_fields_and_values_before_writing()
    {
        demo.CustomerRequired = true;
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkQueries.CreateAsync(lens, null, null, "product backlog item", new WriteRequest { Title = "Export" }));
        Assert.Contains("needs: Customer (Alpha | Beta)", missing.Message);

        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkQueries.CreateAsync(lens, null, null, "Product Backlog Item", new WriteRequest { Title = "Export", Fields = new() { ["Customer"] = "Gamma" } }));
        Assert.Contains("values: Alpha | Beta", wrong.Message);
        Assert.Empty(items.Created);
    }

    [Fact]
    public async Task Create_writes_markdown_as_html_people_and_canonical_values()
    {
        var text = await WorkQueries.CreateAsync(lens, null, null, "Product Backlog Item", new WriteRequest
        {
            Title = "Export to CSV", Description = "Users export the list.\n\n- all columns\n- current filter",
            AcceptanceCriteria = "- file opens in Excel", AssignedTo = "anna", Parent = 3, Fields = new() { ["custom.customer"] = "alpha" },
        });

        var ops = items.Created.Single();
        Assert.Contains("\"/fields/Custom.Customer\",\"value\":\"Alpha\"", ops);
        Assert.Contains("\"/fields/System.AssignedTo\",\"value\":\"anna@contoso.com\"", ops);
        Assert.Contains("<ul><li>all columns</li><li>current filter</li></ul>", ops.Replace("\\u003C", "<").Replace("\\u003E", ">"));
        Assert.Contains("System.LinkTypes.Hierarchy-Reverse", ops);
        Assert.StartsWith("created #10 Product Backlog Item", text);
    }

    [Fact]
    public async Task Criteria_go_into_the_description_when_the_type_has_no_field()
    {
        var text = await WorkQueries.CreateAsync(lens, null, null, "Task", new WriteRequest { Title = "Form", AcceptanceCriteria = "- saves" });
        Assert.Contains("Acceptance criteria", items.Created.Single());
        Assert.Contains("note: Task has no Acceptance Criteria field", text);
    }

    [Fact]
    public async Task State_changes_follow_the_process_transitions()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkQueries.UpdateAsync(lens, null, "1", null, new WriteRequest { State = "done" }, null, null, 3));
        Assert.Contains("#1: Product Backlog Item cannot go from New to Done; allowed from New: Committed", ex.Message);

        var ok = await WorkQueries.UpdateAsync(lens, null, "1", null, new WriteRequest { State = "committed" }, null, null, 3);
        Assert.StartsWith("updated #1 Product Backlog Item [Committed] Login page", ok);
        Assert.Contains("\"value\":\"Committed\"", items.Patches.Single().Body);
    }

    [Fact]
    public async Task Bulk_updates_are_previewed_and_written_only_with_the_token()
    {
        var request = new WriteRequest { Tags = "+reviewed" };
        var preview = await WorkQueries.UpdateAsync(lens, null, "1;2;3;4", null, request, null, null, 3);

        Assert.StartsWith("PREVIEW - nothing written yet. 4 work items would change:", preview);
        Assert.Contains("#1 Product Backlog Item [New] Login page: Tags: UAT -> UAT; reviewed", preview);
        Assert.Empty(items.Patches);

        var token = preview[(preview.LastIndexOf("confirm=", StringComparison.Ordinal) + 8)..].TrimEnd('.');
        var stale = await WorkQueries.UpdateAsync(lens, null, "1;2;3;4", null, new WriteRequest { Tags = "+other" }, null, token, 3);
        Assert.StartsWith("the confirm token does not match", stale);
        Assert.Empty(items.Patches);

        var done = await WorkQueries.UpdateAsync(lens, null, "1;2;3;4", null, request, null, token, 3);
        Assert.StartsWith("updated 4 of 4:", done);
        Assert.Equal(4, items.Patches.Count);
    }

    [Fact]
    public async Task Wiql_selects_items_to_update()
    {
        var preview = await WorkQueries.UpdateAsync(lens, null, null, "SELECT [System.Id] FROM WorkItems", new WriteRequest { Tags = "x" }, null, null, 1);
        Assert.Contains("2 work items would change", preview);
    }

    [Fact]
    public async Task Mentions_notify_and_ambiguous_names_are_not_guessed()
    {
        var text = await WorkQueries.CommentAsync(lens, null, 1, "@[Anna] please confirm the **wording**");
        Assert.Contains("1 mention(s) notified", text);
        Assert.Contains("data-vss-mention=\\u0022version:2.0,p-anna\\u0022", items.Comments.Single());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkQueries.CommentAsync(lens, null, 1, "@[kowal] hi"));
        Assert.Contains("matches 2 people: Ben Kowalski; Eva Kowalska", ex.Message);
    }

    [Fact]
    public async Task Links_respect_the_single_parent_and_skip_existing_ones()
    {
        var parent = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkQueries.LinkAsync(lens, null, 1, "4", "parent", false));
        Assert.Contains("already has a parent", parent.Message);

        var text = await WorkQueries.LinkAsync(lens, null, 1, "4;2", "related", false);
        Assert.Contains("added related: #4 Bug [New] Error on save; #2 Task [To Do] Build the form", text);
        var children = await WorkQueries.LinkAsync(lens, null, 1, "2", "child", false);
        Assert.Contains("skipped: #2 (already linked)", children);
    }

    [Fact]
    public async Task Query_lists_one_line_per_item_with_extra_columns()
    {
        var text = await WorkQueries.QueryAsync(lens, null, null, "SELECT [System.Id], [Custom.Customer] FROM WorkItems", null, 50);
        Assert.Equal("query: 2\n#4 Bug [New] Error on save\n#2 Task [To Do] Build the form", text.Replace("\r", ""));
    }
}

public class TextTests
{
    [Fact]
    public void Html_becomes_compact_markdown()
    {
        var md = Html.ToMarkdown("""
            <div><b>Goal</b></div><ul><li>first</li><li>second<ol><li>nested</li></ol></li></ul>
            <p>See <a href="https://example.com/doc">the docs</a> <img src="https://x/a.png"></p><style>.x{}</style>&nbsp;&amp;
            """);
        Assert.Equal("**Goal**\n\n- first\n- second\n  1. nested\n\nSee the docs (https://example.com/doc) [image]\n\n&", md);
    }

    [Fact]
    public void Markdown_becomes_simple_html()
    {
        var html = Html.FromMarkdown("# Scope\nText with **bold** and `code`.\n\n- a\n  - a1\n- b\n\n1. one\n2. two");
        Assert.Equal("<h2>Scope</h2><div>Text with <b>bold</b> and <code>code</code>.</div><ul><li>a<ul><li>a1</li></ul></li><li>b</li></ul><ol><li>one</li><li>two</li></ol>", html);
        Assert.Equal("<div>&lt;script&gt;</div>", Html.FromMarkdown("<script>"));
    }

    [Theory]
    [InlineData(null, "a; b", "a; b")]
    [InlineData("UAT; old", "+new; -old", "UAT; new")]
    [InlineData("UAT", "+uat", "UAT")]
    public void Tags_are_replaced_or_merged(string? current, string change, string expected) =>
        Assert.Equal(expected, WorkItems.MergeTags(current, change));

    [Fact]
    public void Rule_errors_are_explained() =>
        Assert.StartsWith("field 'Reason' is required for this change",
            AdoClient.Explain("TF401320: Rule Error for field Reason. Error code: Required, InvalidEmpty."));

    [Fact]
    public void Pat_is_never_sent_outside_azure_devops()
    {
        var client = new FakeAdo().Client();
        var ex = Assert.ThrowsAsync<AdoApiException>(() => client.GetAsync("https://example.com/_apis/x")).Result;
        Assert.Contains("refusing to send the PAT to example.com", ex.Message);
    }
}
