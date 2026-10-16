namespace AdoLens.Tests;

public class MergedPrTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-merged-{Guid.NewGuid():N}");
    readonly FakeAdo ado;
    readonly Lens lens;

    public MergedPrTests()
    {
        ado = new DemoItems().Routes(new DemoProject().Routes(new FakeAdo()))
            .On("/contoso/Demo/_apis/git/pullrequests", """
                {"value":[
                  {"pullRequestId":12,"title":"Login form","closedDate":"2026-10-14T10:00:00Z","repository":{"id":"r1","name":"portal"},
                   "createdBy":{"displayName":"Anna Nowak"},"sourceRefName":"refs/heads/feature/login"},
                  {"pullRequestId":11,"title":"Old change","closedDate":"2026-09-01T10:00:00Z","repository":{"id":"r1","name":"portal"},
                   "createdBy":{"displayName":"Ben Kowalski"},"sourceRefName":"refs/heads/feature/old"}]}
                """)
            .On("/contoso/_apis/git/repositories/r1/pullRequests/12/workitems", """{"value":[{"id":"1"}]}""")
            .On("/contoso/_apis/git/repositories/r1/pullRequests/11/workitems", """{"value":[]}""");
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
    public async Task Pull_requests_merged_into_a_branch_come_with_their_work_items()
    {
        var all = await PullRequestQueries.MergedAsync(lens, null, null, "staging", null, null, 30);
        Assert.Contains("2 pull request(s) completed into staging in Demo", all);
        Assert.Contains("- PR 12 2026-10-14 portal: Login form (Anna Nowak, from feature/login) -> #1 Login page (New)", all);
        Assert.Contains("- PR 11 2026-09-01 portal: Old change (Ben Kowalski, from feature/old) - no work item", all);
        Assert.Contains(ado.Requests, r => r.Contains("searchCriteria.status=completed") && r.Contains("searchCriteria.targetRefName=refs/heads/staging"));

        var recent = await PullRequestQueries.MergedAsync(lens, null, null, "staging", null, "2026-10-01", 30);
        Assert.Contains("1 pull request(s) completed into staging in Demo since 2026-10-01", recent);
        Assert.DoesNotContain("PR 11", recent);
    }
}
