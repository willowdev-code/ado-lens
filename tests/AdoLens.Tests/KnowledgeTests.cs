using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoLens.Tests;

/// <summary>Phase 4: search, files and their history, undo, backlog health, release notes, recipes.</summary>
public class KnowledgeTests : IDisposable
{
    static readonly DateTime Today = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Local);

    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-knowledge-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly DemoItems items = new();
    readonly List<string> deletes = [];
    readonly List<(int Id, string Body)> patches = [];
    int[] wiql = [4, 2];
    readonly Lens lens;

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static string Body(HttpRequestMessage r) => r.Content!.ReadAsStringAsync().Result;

    const string Aot = """
        <?xml version="1.0" encoding="utf-8"?>
        <AxClass xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>CreditCheck</Name>
          <SourceCode>
            <Declaration><![CDATA[class CreditCheck {}]]></Declaration>
            <Methods>
              <Method><Name>validate</Name><Source><![CDATA[public boolean validate()
        {
            return limit >= amount;
        }]]></Source></Method>
              <Method><Name>run</Name><Source><![CDATA[public void run() {}]]></Source></Method>
            </Methods>
          </SourceCode>
        </AxClass>
        """;

    public KnowledgeTests()
    {
        foreach (var (id, item) in items.Items) item["rev"] = 3;
        var ado = new FakeAdo()
            // Patches change the stored item (and its revision), so undo sees what is there now.
            .On("/contoso/_apis/wit/workitems/", r =>
            {
                var id = int.Parse(r.RequestUri!.AbsolutePath.Split('/')[^1]);
                var body = Body(r);
                lock (patches) patches.Add((id, body));
                var item = items.Items[id];
                foreach (var op in JsonNode.Parse(body)!.AsArray())
                {
                    var path = (string)op!["path"]!;
                    if (!path.StartsWith("/fields/")) continue;
                    if ((string)op["op"]! == "add") item["fields"]![path[8..]] = op["value"]!.DeepClone();
                    else ((JsonObject)item["fields"]!).Remove(path[8..]);
                }
                item["rev"] = (int)item["rev"]! + 1;
                return Json(item.ToJsonString());
            })
            .On("/contoso/Demo/_apis/wit/workItems/1/comments", r =>
            {
                if (r.Method == HttpMethod.Delete) { deletes.Add(r.RequestUri!.AbsolutePath); return Json(""); }
                return Json("""{"id":77}""");
            })
            .On("/contoso/Demo/_apis/wit/workItems/", """{"id":78}""")
            .On("/contoso/Demo/Demo Team/_apis/wit/wiql", _ => Json(new JsonObject { ["workItems"] = new JsonArray(wiql.Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()) }.ToJsonString()));
        ado = items.Routes(demo.Routes(ado))
            .On("/contoso/Demo/_apis/search/workitemsearchresults", """
                {"count":7,"results":[{"fields":{"system.id":"1","system.workitemtype":"Product Backlog Item","system.state":"New","system.title":"Login page"},
                  "hits":[{"fieldReferenceName":"system.title","highlights":["<highlighthit>Login</highlighthit> page"]},
                          {"fieldReferenceName":"microsoft.vsts.common.acceptancecriteria","fieldName":"Acceptance Criteria","highlights":["user can sign in with <highlighthit>login</highlighthit>"]}]}]}
                """)
            .On("/contoso/Demo/_apis/search/wikisearchresults", """{"count":1,"results":[{"path":"/Portal/Sign-in.md","hits":[{"highlights":["the <highlighthit>login</highlighthit> uses SSO"]}]}]}""")
            .On("/contoso/Demo/_apis/search/codesearchresults", r => Body(r).Contains("file:")
                ? Json("""{"count":2,"results":[{"path":"/src/AxClass/CreditCheck.xml","repository":{"name":"erp"}},{"path":"/old/CreditCheckOld.xml","repository":{"name":"erp"}}]}""")
                : Json("""{"message":"Code search is not available"}""", HttpStatusCode.NotFound))
            .On("/contoso/Demo/_apis/git/repositories/erp", """{"id":"r9","name":"erp","project":{"name":"Demo"}}""")
            .On("/contoso/_apis/git/repositories/r9/items", Json_(new JsonObject { ["content"] = Aot }))
            .On("/contoso/_apis/git/repositories/r9/commits", """
                {"value":[
                  {"commitId":"aaaaaaaa11","author":{"name":"Ben Kowalski","date":"2026-10-10T10:00:00Z"},"comment":"Merged PR 482: Credit limit check"},
                  {"commitId":"bbbbbbbb22","author":{"name":"Anna Nowak","date":"2026-09-01T10:00:00Z"},"comment":"Typo"}]}
                """)
            .On("/contoso/_apis/git/repositories/r9/pullRequests/482/workitems", """{"value":[{"id":"1"}]}""")
            .On("/contoso/Demo/Demo Team/_apis/work/teamsettings/teamfieldvalues", """{"field":{"referenceName":"System.AreaPath"},"values":[{"value":"Demo","includeChildren":true}]}""")
            .On("/contoso/Demo/Demo Team/_apis/work/teamsettings/iterations", """
                {"value":[
                  {"id":"s1","name":"Sprint 1","path":"Demo\\Sprint 1","attributes":{"startDate":"2026-09-21T00:00:00Z","finishDate":"2026-10-02T00:00:00Z","timeFrame":"past"}},
                  {"id":"s2","name":"Sprint 2","path":"Demo\\Sprint 2","attributes":{"startDate":"2026-10-05T00:00:00Z","finishDate":"2026-10-16T00:00:00Z","timeFrame":"current"}}]}
                """);
        var s = new Settings { DefaultConnection = "contoso" };
        s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
        lens = new Lens(s, "0.0.0", dir, _ => null, _ => "pat", _ => ado.Client());
    }

    static string Json_(JsonObject o) => o.ToJsonString();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task One_search_groups_work_items_wiki_and_code_and_survives_a_missing_part()
    {
        var text = await Knowledge.SearchAsync(lens, null, null, "login", null, 10);
        Assert.Contains("work items: 1 of 7\n- #1 Product Backlog Item [New] Login page | Acceptance Criteria: user can sign in with login", text);
        Assert.Contains("wiki: 1\n- /Portal/Sign in: the login uses SSO", text);
        Assert.Contains("Code search is not available", text);
        await Assert.ThrowsAsync<ArgumentException>(() => Knowledge.SearchAsync(lens, null, null, "x", "docs", 10));
    }

    [Fact]
    public async Task A_file_found_by_name_shows_X_methods_and_one_method_on_request()
    {
        var text = await Knowledge.FileAsync(lens, null, null, "CreditCheck.xml", null, null, null, false, 15, null);
        Assert.Contains("erp: /src/AxClass/CreditCheck.xml", text);
        Assert.Contains("CreditCheck (class): 3 methods", text);
        Assert.Contains("- validate (4 lines)", text);

        var method = await Knowledge.FileAsync(lens, null, null, "/src/AxClass/CreditCheck.xml", "erp", null, "validate", false, 15, null);
        Assert.Contains("--- validate\npublic boolean validate()", method.Replace("\r", ""));
        Assert.DoesNotContain("run()", method);
    }

    [Fact]
    public async Task File_history_names_the_work_items_behind_each_change()
    {
        var text = await Knowledge.FileAsync(lens, null, null, "/src/AxClass/CreditCheck.xml", "erp", null, null, true, 15, null);
        Assert.Contains("history of erp: /src/AxClass/CreditCheck.xml: 2 changes", text);
        Assert.Contains("- 2026-10-10 Ben Kowalski - PR 482: Credit limit check -> #1 Login page (New)", text);
        Assert.Contains("- 2026-09-01 Anna Nowak - commit bbbbbbb: Typo", text);
        Assert.Contains("1 of 2 changes have no work item", text);
    }

    [Fact]
    public async Task Undo_previews_then_restores_the_values_before_the_change()
    {
        await WorkQueries.UpdateAsync(lens, null, "1", null, new WriteRequest { State = "Committed", Title = "Sign-in page" }, null, null, 3);
        Assert.Equal("Committed", (string)items.Items[1]["fields"]!["System.State"]!);

        var preview = await Audit.UndoAsync(lens, null, null);
        Assert.StartsWith("UNDO PREVIEW", preview);
        Assert.Contains("State Committed -> New", preview);
        var token = preview[(preview.LastIndexOf("confirm=", StringComparison.Ordinal) + 8)..].TrimEnd('.');

        var done = await Audit.UndoAsync(lens, null, token);
        Assert.Contains("reverted #1", done);
        Assert.Equal("New", (string)items.Items[1]["fields"]!["System.State"]!);
        Assert.Equal("Login page", (string)items.Items[1]["fields"]!["System.Title"]!);
        Assert.Equal("nothing to undo: no changes by ADO Lens on this connection", await Audit.UndoAsync(lens, null, null));
    }

    [Fact]
    public async Task Undo_leaves_what_someone_else_changed_since()
    {
        await WorkQueries.UpdateAsync(lens, null, "1", null, new WriteRequest { State = "Committed", Title = "Sign-in page" }, null, null, 3);
        // Someone renames it afterwards.
        items.Items[1]["fields"]!["System.Title"] = "Sign-in page v2";
        items.Items[1]["rev"] = (int)items.Items[1]["rev"]! + 1;

        var preview = await Audit.UndoAsync(lens, null, null);
        var token = preview[(preview.LastIndexOf("confirm=", StringComparison.Ordinal) + 8)..].TrimEnd('.');
        var done = await Audit.UndoAsync(lens, null, token);
        Assert.Contains("left as changed since by others: Title", done);
        Assert.Equal("New", (string)items.Items[1]["fields"]!["System.State"]!);
        Assert.Equal("Sign-in page v2", (string)items.Items[1]["fields"]!["System.Title"]!);
    }

    [Fact]
    public async Task Undo_of_a_comment_deletes_it()
    {
        await WorkQueries.CommentAsync(lens, null, 1, "Looks good");
        Assert.Contains("#1 Login page: comment 77 deleted", Audit.List(lens, null, 5));
        var preview = await Audit.UndoAsync(lens, null, null);
        var token = preview[(preview.LastIndexOf("confirm=", StringComparison.Ordinal) + 8)..].TrimEnd('.');
        await Audit.UndoAsync(lens, null, token);
        Assert.EndsWith("/workItems/1/comments/77", deletes.Single());
    }

    [Fact]
    public async Task Health_lists_what_needs_tidying()
    {
        items.Items[4]["fields"]!["System.IterationPath"] = "Demo\\Sprint 1";
        wiql = [1, 4];
        var text = await BoardQueries.HealthAsync(lens, null, null, null, Today);
        Assert.Contains("backlog health of Demo Team (Demo): 2 open", text);
        Assert.Contains("left in a finished sprint (1):\n- #4 Bug [New] Error on save - Sprint 1", text.Replace("\r", ""));
        Assert.Contains("no description", text);
        Assert.DoesNotContain("no acceptance criteria", text); // #1 has criteria
    }

    [Fact]
    public async Task Release_notes_group_requirements_by_feature_and_list_fixed_bugs()
    {
        wiql = [1, 4];
        var text = (await BoardQueries.ReleaseNotesAsync(lens, null, null, null, null, null, null, Today)).Replace("\r", "");
        Assert.StartsWith("# Release notes - Sprint 2 (2026-10-05 - 2026-10-16)", text);
        Assert.Contains("## New and changed\n\n**Portal**\n\n- Login page (#1)", text);
        Assert.Contains("## Fixed\n\n- Error on save (#4)", text);
    }

    [Fact]
    public async Task A_recipe_is_saved_previewed_and_run()
    {
        var saved = Recipes.Save(lens, "Close Sprint", """
            {"description":"move what is left to the next sprint","steps":[
              {"query":"SELECT [System.Id] FROM WorkItems WHERE [System.IterationPath] = '{sprint}'","tags":"+carried over","comment":"Moved from {sprint}"},
              {"ids":"1","child":{"type":"Task","title":"Test {title}"}}]}
            """);
        Assert.Contains("saved recipe close-sprint: 2 steps, parameters sprint", saved);
        Assert.Contains("- close-sprint (sprint): move what is left", Recipes.List(lens));
        await Assert.ThrowsAsync<ArgumentException>(() => Recipes.RunAsync(lens, null, "close-sprint", null, null, false));

        var values = new Dictionary<string, string> { ["sprint"] = "Demo\\Sprint 1" };
        var preview = await Recipes.RunAsync(lens, null, "close-sprint", values, null, false);
        Assert.Contains("step 1: tags +carried over, comment -> 2 items", preview);
        Assert.Contains("step 2: new Task 'Test {title}' under each -> 1 items", preview);
        Assert.Empty(patches);

        var token = preview[(preview.LastIndexOf("confirm=", StringComparison.Ordinal) + 8)..].TrimEnd('.');
        var run = await Recipes.RunAsync(lens, null, "close-sprint", values, token, false);
        Assert.Contains("step 1: updated 2 of 2", run);
        Assert.Contains("step 2: created 1 Task item(s)", run);
        Assert.Contains("Test Login page", items.Created.Single());
        Assert.Contains("carried over", (string)items.Items[4]["fields"]!["System.Tags"]!);
        Assert.Equal("deleted recipe close-sprint", Recipes.Delete(lens, "close-sprint"));
    }

    [Fact]
    public async Task A_large_attachment_gets_a_link_and_is_not_downloaded()
    {
        ((JsonArray)items.Items[1]["relations"]!).Add(JsonNode.Parse("""
            {"rel":"AttachedFile","url":"https://dev.azure.com/contoso/_apis/wit/attachments/big","attributes":{"name":"trace.etl","resourceSize":52428800}}
            """));
        var (list, _) = await Attachments.ReadAsync(lens, null, 1, null, null);
        Assert.Contains("1. trace.etl (50 MB)", list);
        var (text, images) = await Attachments.ReadAsync(lens, null, 1, "trace.etl", null);
        Assert.Contains("larger than 20 MB", text);
        Assert.Contains("save=true saves it to disk", text);
        Assert.Contains("https://dev.azure.com/contoso/_apis/wit/attachments/big", text);
        Assert.Empty(images);
    }
}
