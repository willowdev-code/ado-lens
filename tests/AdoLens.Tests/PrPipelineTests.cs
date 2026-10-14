using System.Net;
using System.Text;

namespace AdoLens.Tests;

public class PrPipelineTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-pr-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly DemoItems items = new();
    readonly List<(string Method, string Path, string Body)> writes = [];
    readonly Settings settings;
    readonly Lens lens;

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    HttpResponseMessage Record(HttpRequestMessage r, string answer)
    {
        lock (writes) writes.Add((r.Method.Method, r.RequestUri!.AbsolutePath, r.Content?.ReadAsStringAsync().Result ?? ""));
        return Json(answer);
    }

    const string Pr = """
        {"pullRequestId":482,"status":"active","title":"Login form","isDraft":false,"mergeStatus":"succeeded","description":"Adds the form.",
         "creationDate":"2026-10-14T08:00:00Z","createdBy":{"id":"ben","displayName":"Ben Kowalski"},
         "sourceRefName":"refs/heads/feature/1-login","targetRefName":"refs/heads/dev","lastMergeSourceCommit":{"commitId":"head1"},
         "repository":{"id":"r1","name":"portal","project":{"id":"p1","name":"Demo"}},
         "reviewers":[{"id":"me-1","displayName":"Anna Nowak","vote":0,"isRequired":true},{"id":"eva","displayName":"Eva Kowalska","vote":10}]}
        """;

    public PrPipelineTests()
    {
        var ado = items.Routes(demo.Routes(new FakeAdo()))
            .On("/contoso/_apis/connectionData", """{"authenticatedUser":{"id":"me-1","providerDisplayName":"Anna Nowak"}}""")
            .On("/contoso/Demo/_apis/git/pullrequests", "{\"value\":[" + Pr + "]}")
            .On("/contoso/_apis/git/pullrequests/482", Pr)
            .On("/contoso/_apis/git/repositories/r1/pullRequests/482/threads", r => r.Method == HttpMethod.Post
                ? Record(r, """{"id":77}""")
                : Json("""
                    {"value":[
                      {"id":1,"status":"active","threadContext":{"filePath":"/src/M/AxClass/LoginService.xml","rightFileStart":{"line":42}},
                       "comments":[{"commentType":"text","author":{"displayName":"Eva Kowalska"},"content":"Check the [limit](https://x/y) here"},
                                   {"commentType":"text","author":{"displayName":"Ben Kowalski"},"content":"Done"}]},
                      {"id":2,"status":"active","comments":[{"commentType":"text","author":{"displayName":"Ben"},"content":"Submitted conflict resolution for the file(s)."}]},
                      {"id":3,"status":"fixed","comments":[{"commentType":"text","author":{"displayName":"Eva Kowalska"},"content":"Typo"}]},
                      {"id":4,"status":"active","comments":[{"commentType":"system","content":"Ben voted 10"}]}]}
                    """))
            .On("/contoso/_apis/git/repositories/r1/pullRequests/482/workitems", """{"value":[{"id":"1"}]}""")
            .On("/contoso/_apis/git/repositories/r1/pullrequests/482/reviewers/me-1", r => Record(r, "{}"))
            .On("/contoso/_apis/git/repositories/r1/pullrequests", r => Record(r, Pr))
            .On("/contoso/Demo/_apis/git/repositories/portal", """{"id":"r1","name":"portal","project":{"name":"Demo"}}""")
            .On("/contoso/Demo/_apis/policy/evaluations", """{"value":[{"configuration":{"type":{"displayName":"Build"}},"status":"rejected"},{"configuration":{"type":{"displayName":"Work item linking"}},"status":"approved"}]}""")
            .On("/contoso/Demo/_apis/build/builds/900/timeline", """
                {"records":[
                  {"type":"Job","name":"Build","result":"failed"},
                  {"type":"Task","name":"Build the solution","result":"failed","log":{"id":7},
                   "issues":[{"type":"warning","message":"Package folder already exists"},{"type":"error","message":"Process 'msbuild.exe' exited with code '1'."}]},
                  {"type":"Task","name":"Restore","result":"succeeded"}]}
                """)
            .On("/contoso/Demo/_apis/build/builds/900/logs/7", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    2026-10-14T20:48:25.1234567Z Starting build
                    2026-10-14T20:48:26.0000000Z warning: package folder exists
                    2026-10-14T20:50:00.0000000Z C:\SDK\Build.targets(40,5): error : Fatal: dynamics://Table/DemoTable - metadata error [C:\SDK\BuildMetadata.proj]
                    2026-10-14T20:50:00.0000000Z C:\SDK\Build.targets(40,5): error : Fatal: dynamics://Table/DemoTable - metadata error [C:\SDK\BuildMetadata.proj]
                    2026-10-14T20:50:01.0000000Z Compilation failed.
                    2026-10-14T20:50:02.0000000Z Build complete: 0 error(s)? no
                    """, Encoding.UTF8, "text/plain"),
            })
            .On("/contoso/Demo/_apis/build/builds/900", """
                {"id":900,"buildNumber":"2026.10.14.1","status":"completed","result":"failed","sourceBranch":"refs/heads/dev","sourceVersion":"abcdef1234",
                 "reason":"individualCI","definition":{"name":"Demo CI"},"requestedFor":{"displayName":"Ben Kowalski"},
                 "startTime":"2026-10-14T20:40:00Z","finishTime":"2026-10-14T20:59:00Z"}
                """)
            .On("/contoso/Demo/_apis/build/definitions", """{"value":[{"id":5,"name":"Demo CI"},{"id":6,"name":"Demo CI nightly"}]}""")
            .On("/contoso/Demo/_apis/build/builds", r => r.Method == HttpMethod.Post
                ? Record(r, """{"id":901,"buildNumber":"2026.10.14.2","status":"notStarted","sourceBranch":"refs/heads/dev","definition":{"name":"Demo CI"},"requestedFor":{"displayName":"Anna Nowak"},"queueTime":"2026-10-14T21:00:00Z"}""")
                : Json("""{"value":[{"id":900,"buildNumber":"2026.10.14.1","result":"failed","sourceBranch":"refs/heads/dev","definition":{"name":"Demo CI"},"requestedFor":{"displayName":"Ben Kowalski"},"finishTime":"2026-10-14T20:59:00Z"}]}"""));
        settings = new Settings { DefaultConnection = "contoso" };
        settings.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
        lens = new Lens(settings, "0.0.0", dir, _ => null, _ => "pat", _ => ado.Client());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Pull_requests_waiting_for_me_are_listed_first()
    {
        var text = await PullRequestQueries.ListAsync(lens, null, null, null);
        Assert.Contains("waiting for your review (1):\n  PR 482 [portal] Login form: dev <- feature/1-login (Ben Kowalski,", text.Replace("\r", ""));
        Assert.Contains("1 approved, 1 no vote", text);
    }

    [Fact]
    public async Task One_pull_request_shows_votes_checks_work_items_and_human_threads_only()
    {
        var text = (await PullRequestQueries.ShowAsync(lens, null, 482, null, null)).Replace("\r", "");
        Assert.Contains("reviewers: Anna Nowak no vote (required); Eva Kowalska approved", text);
        Assert.Contains("checks: Build rejected; Work item linking approved", text);
        Assert.Contains("work items: #1 Product Backlog Item [New] Login page", text);
        Assert.Contains("comments: 1 open thread(s), 1 resolved", text);
        Assert.Contains("- thread 1 [active] LoginService (class):42 - Eva Kowalska: Check the limit here (1 reply, last: Done)", text);
        Assert.DoesNotContain("conflict resolution", text);
        Assert.DoesNotContain("voted", text);
    }

    [Fact]
    public async Task Pull_request_actions_send_what_azure_devops_expects()
    {
        Assert.Equal("voted on PR 482: approved", await PullRequestQueries.ActAsync(lens, null, new() { Action = "vote", Id = 482, Vote = "approve" }));
        Assert.Contains("\"vote\":10", writes.Single(w => w.Method == "PUT").Body);

        await PullRequestQueries.ActAsync(lens, null, new() { Action = "comment", Id = 482, Text = "@[Anna] see line", File = "src/M/AxClass/LoginService.xml", Line = 42 });
        var comment = writes.Single(w => w.Path.EndsWith("/threads")).Body;
        Assert.Contains("@\\u003Cp-anna\\u003E see line", comment);
        Assert.Contains("\"filePath\":\"/src/M/AxClass/LoginService.xml\",\"rightFileStart\":{\"line\":42", comment);

        var created = await PullRequestQueries.ActAsync(lens, null, new()
            { Action = "create", Repo = "portal", Source = "feature/1-login", Target = "dev", Title = "Login form", Text = "Adds the form.", WorkItems = "1" });
        var body = writes.Last(w => w.Path.EndsWith("/pullrequests")).Body;
        Assert.Contains("\"sourceRefName\":\"refs/heads/feature/1-login\"", body);
        Assert.Contains("\"workItemRefs\":[{\"id\":\"1\"}]", body);
        Assert.StartsWith("created PR 482", created);

        await Assert.ThrowsAsync<ArgumentException>(() => PullRequestQueries.ActAsync(lens, null, new() { Action = "merge", Id = 482 }));
    }

    [Fact]
    public async Task A_failed_build_shows_errors_and_the_error_lines_of_its_log()
    {
        var text = (await PipelineQueries.BuildAsync(lens, null, null, 900, null)).Replace("\r", "");
        Assert.StartsWith("build 900 Demo CI 2026.10.14.1: failed, dev, Ben Kowalski,", text);
        Assert.Contains("failed: Build > Build the solution", text);
        Assert.Contains("  error: Process 'msbuild.exe' exited with code '1'.", text);
        Assert.DoesNotContain("Package folder already exists", text);
        Assert.Contains("  3: error : Fatal: dynamics://Table/DemoTable - metadata error\n", text + "\n");
        Assert.Contains("  5: Compilation failed.", text);
        Assert.Single(text.Split('\n'), l => l.Contains("DemoTable"));
    }

    [Fact]
    public async Task Pipelines_are_listed_and_run_by_name()
    {
        Assert.Contains("build 900 Demo CI 2026.10.14.1: failed, dev", await PipelineQueries.ListAsync(lens, null, null, null, null, 10));
        var queued = await PipelineQueries.RunAsync(lens, null, null, "demo ci", "dev", null);
        Assert.StartsWith("queued build 901 Demo CI", queued);
        Assert.Contains("\"definition\":{\"id\":5},\"sourceBranch\":\"refs/heads/dev\"", writes.Single().Body);
        var ambiguous = await Assert.ThrowsAsync<InvalidOperationException>(() => PipelineQueries.RunAsync(lens, null, null, "nightly x", null, null));
        Assert.Contains("matches 2 pipelines", ambiguous.Message);
    }

    [Fact]
    public void Environments_are_saved_for_good_in_flow_order()
    {
        var answer = Queries.SettingsAnswer(lens, null, "dev=DEV (sbx01); staging=UAT (sbx03); master=PROD");
        Assert.Contains("environments (in flow order): DEV (sbx01) [dev] -> UAT (sbx03) [staging] -> PROD [master]", answer);
        var saved = Settings.Load(lens.SettingsPath);
        Assert.Equal(["dev", "staging", "master"], saved.Connections["contoso"].Environments.Keys);
        Assert.Throws<ArgumentException>(() => Queries.SettingsAnswer(lens, null, "staging"));
    }

    [Fact]
    public void The_flow_is_guessed_from_branch_and_stage_names()
    {
        var flow = TraceQueries.ProposeFlow(["master", "R350", "staging", "dev", "Main-Deploy/Stage 1", "feature/x"]);
        Assert.Equal([("dev", "DEV"), ("staging", "UAT"), ("master", "PROD"), ("Main-Deploy/Stage 1", (string?)null)], flow);
    }
}
