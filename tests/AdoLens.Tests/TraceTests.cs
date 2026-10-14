using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoLens.Tests;

public class DiffTests
{
    [Fact]
    public void Unified_diff_shows_changes_with_context_and_line_numbers()
    {
        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var after = before.Replace("line 10", "line ten").Replace("line 20", "line 20\nline 21");
        var diff = Diff.Unified(before, after, context: 2)!.Replace("\r", "");

        Assert.Equal("@@ -8,5 +8,5 @@\n line 8\n line 9\n-line 10\n+line ten\n line 11\n line 12\n@@ -19,2 +19,3 @@\n line 19\n line 20\n+line 21", diff);
        Assert.Null(Diff.Unified("same\ntext", "same\ntext"));
    }

    [Fact]
    public void Diff_of_completely_different_texts_lists_both_sides()
    {
        var ops = Diff.Lines(["a", "b"], ["c"]);
        Assert.Equal([(Diff.Op.Del, "a"), (Diff.Op.Del, "b"), (Diff.Op.Add, "c")], ops);
    }

    const string FormV1 = """
        <AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>DemoForm</Name>
          <SourceCode>
            <Methods>
              <Method><Name>init</Name><Source><![CDATA[public void init()
        {
            super();
        }]]></Source></Method>
              <Method><Name>old</Name><Source><![CDATA[void old() {}]]></Source></Method>
            </Methods>
            <DataSources>
              <DataSource><Name>SalesLine</Name><Methods>
                <Method><Name>validateWrite</Name><Source><![CDATA[public boolean validateWrite()
        {
            return super();
        }]]></Source></Method>
              </Methods></DataSource>
            </DataSources>
          </SourceCode>
          <Design><Caption>Old</Caption></Design>
        </AxForm>
        """;

    static string FormV2 => FormV1
        .Replace("return super();", "boolean ret = super();\n    return ret && this.checkLimit();")
        .Replace("<Method><Name>old</Name><Source><![CDATA[void old() {}]]></Source></Method>", "<Method><Name>checkLimit</Name><Source><![CDATA[boolean checkLimit() { return true; }]]></Source></Method>")
        .Replace("<Caption>Old</Caption>", "<Caption>New</Caption>");

    [Fact]
    public void Aot_changes_are_reported_per_method_with_data_source_names()
    {
        Assert.Equal("+checkLimit, ~SalesLine.validateWrite, -old, metadata changed", AotDiff.Summary(FormV1, FormV2));
        Assert.Equal("new, 3 method(s): init, old, SalesLine.validateWrite", AotDiff.Summary(null, FormV1));
        Assert.Equal("deleted", AotDiff.Summary(FormV1, null));

        var detail = AotDiff.Detail(FormV1, FormV2).Replace("\r", "");
        Assert.Contains("~ SalesLine.validateWrite\n@@", detail);
        Assert.Contains("+    return ret && this.checkLimit();", detail);
        Assert.Contains("+ checkLimit (new)\n+boolean checkLimit() { return true; }", detail);
        Assert.Contains("- old (removed)", detail);
        Assert.Contains("-    <Caption>Old</Caption>", detail);
        Assert.DoesNotContain("super();\n+", detail.Split("metadata")[1]);
    }

    [Theory]
    [InlineData("$/P/Main/Metadata/M/M/AxClass/SalesTable_Extension.xml", "SalesTable_Extension (class)")]
    [InlineData("/src/M/AxTableExtension/CustTable.Demo.xml", "CustTable.Demo (table extension)")]
    [InlineData("/src/M/AxEdt/DemoAmount.xml", "DemoAmount (EDT)")]
    [InlineData("/src/M/AxMenuItemDisplay/DemoForm.xml", "DemoForm (display menu item)")]
    public void Aot_paths_become_object_names(string path, string expected)
    {
        Assert.True(AotDiff.IsAot(path));
        Assert.Equal(expected, AotDiff.ObjectName(path));
    }

    [Fact]
    public void Non_aot_files_are_not_mistaken_for_aot() => Assert.False(AotDiff.IsAot("/web/Axios/config.xml"));
}

public class TraceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-trace-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly DemoItems items = new();
    readonly Lens lens;

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static string Content(string source) =>
        new JsonObject { ["path"] = "x", ["content"] = $"<AxClass><Name>Foo</Name><SourceCode><Methods><Method><Name>run</Name><Source><![CDATA[{source}]]></Source></Method></Methods></SourceCode></AxClass>" }.ToJsonString();

    public TraceTests()
    {
        var ado = items.Routes(demo.Routes(new FakeAdo()))
            // Changes come in pages until an empty one.
            .On("/contoso/_apis/tfvc/changesets/523/changes", r => Json(Uri.UnescapeDataString(r.RequestUri!.Query).Contains("$skip=0") ? """
                {"value":[{"item":{"path":"$/Demo/Main/Metadata/M/M/AxClass/Foo.xml"},"changeType":"edit"},
                          {"item":{"path":"$/Demo/Main/Readme.txt"},"changeType":"edit"}]}
                """ : """{"value":[]}"""))
            .On("/contoso/_apis/tfvc/changesets/523", """{"changesetId":523,"author":{"displayName":"Ben Kowalski"},"createdDate":"2026-10-01T10:00:00Z","comment":"Fix login"}""")
            .On("/contoso/Demo/_apis/tfvc/branches", """{"value":[{"path":"$/Demo/Main","children":[{"path":"$/Demo/Release"}]}]}""")
            .On("/contoso/_apis/tfvc/items", r => Json(r.RequestUri!.Query.Contains("version=522")
                ? Content("void run()\n{\n    login();\n}")
                : r.RequestUri!.Query.Contains("Readme") ? """{"content":"old readme"}""" : Content("void run()\n{\n    if (this.check())\n        login();\n}")))
            .On("/contoso/_apis/git/repositories/r1/pullrequests/482", """
                {"pullRequestId":482,"status":"completed","title":"Login form","createdBy":{"displayName":"Ben Kowalski"},"closedDate":"2026-10-02T10:00:00Z",
                 "sourceRefName":"refs/heads/feature/1-login","targetRefName":"refs/heads/dev","lastMergeCommit":{"commitId":"c-merge"},
                 "repository":{"id":"r1","name":"portal","project":{"name":"Demo"}}}
                """)
            .On("/contoso/Demo/_apis/build/builds/900", """{"message":"The requested build 900 could not be found."}""", HttpStatusCode.NotFound)
            .On("/contoso/Demo/_apis/release/deployments", """
                {"value":[
                  {"releaseDefinition":{"name":"Main release"},"releaseEnvironment":{"name":"Stage 1"},"completedOn":"2026-10-03T01:00:00Z",
                   "release":{"artifacts":[{"isPrimary":true,"definitionReference":{"branch":{"id":"$/Demo/Main"},"sourceVersion":{"id":"600"},"version":{"name":"26.10.3.1"}}}]}},
                  {"releaseDefinition":{"name":"Shop staging"},"releaseEnvironment":{"name":"lcs"},"completedOn":"2026-10-04T01:00:00Z",
                   "release":{"artifacts":[{"isPrimary":true,"definitionReference":{"branch":{"id":"refs/heads/staging"},"sourceVersion":{"id":"staging-sha"},"version":{"name":"2026.10.4.1"},"repository":{"id":"r1"}}}]}},
                  {"releaseDefinition":{"name":"Shop master"},"releaseEnvironment":{"name":"lcs"},"completedOn":"2026-09-20T01:00:00Z",
                   "release":{"artifacts":[{"isPrimary":true,"definitionReference":{"branch":{"id":"refs/heads/master"},"sourceVersion":{"id":"master-sha"},"version":{"name":"2026.9.20.1"},"repository":{"id":"r1"}}}]}},
                  {"releaseDefinition":{"name":"Other"},"releaseEnvironment":{"name":"x"},"completedOn":"2026-10-04T01:00:00Z",
                   "release":{"artifacts":[{"definitionReference":{"branch":{"id":"$/Demo/Release"},"sourceVersion":{"id":"700"}}}]}}]}
                """)
            .On("/contoso/_apis/git/repositories/r1/diffs/commits", r =>
            {
                // c-merge is in staging (and the staging deployment) but not yet in master.
                var q = Uri.UnescapeDataString(r.RequestUri!.Query);
                return Json(q.Contains("targetVersion=staging") ? """{"commonCommit":"c-merge"}"""
                    : q.Contains("targetVersion=master") ? """{"commonCommit":"older"}"""
                    : q.Contains("staging-sha") ? """{"commonCommit":"c-merge"}""" : """{"commonCommit":"older"}""");
            })
            .On("/contoso/Demo/_apis/wit/workItems/1/updates", """
                {"value":[
                  {"id":1,"revisedBy":{"displayName":"Anna Nowak"},"fields":{"System.ChangedDate":{"newValue":"2026-09-28T10:00:00Z"},"Microsoft.VSTS.Common.AcceptanceCriteria":{"newValue":"<div>a</div>"}}},
                  {"id":2,"revisedBy":{"displayName":"Anna Nowak"},"fields":{"System.ChangedDate":{"newValue":"2026-10-05T10:00:00Z"},"Microsoft.VSTS.Common.AcceptanceCriteria":{"oldValue":"<div>a</div>","newValue":"<div>b</div>"}}}]}
                """);
        var s = new Settings { DefaultConnection = "contoso" };
        s.Connections["contoso"] = new Connection
        {
            Organization = "contoso", Project = "Demo",
            Environments = new(StringComparer.OrdinalIgnoreCase) { ["staging"] = "UAT", ["master"] = "PROD" },
        };
        lens = new Lens(s, "0.0.0", dir, _ => null, _ => "pat", _ => ado.Client());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Trace_puts_the_criteria_next_to_the_code_and_says_where_it_is()
    {
        var text = (await TraceQueries.TraceAsync(lens, null, 1, null)).Replace("\r", "");

        Assert.Contains("ASKED\nacceptance criteria:\n- user can sign in", text);
        Assert.Contains("- changeset 523 2026-10-01 Ben Kowalski: \"Fix login\" on $/Demo/Main", text);
        Assert.Contains("  2 file(s): Foo (class), Readme.txt", text);
        Assert.Contains("- PR 482 (portal) completed 2026-10-02: dev <- feature/1-login, \"Login form\", Ben Kowalski", text);
        Assert.Contains("- build 900: no longer kept (deleted by retention)", text);
        Assert.Contains("- Main release / Stage 1: YES - deployed 2026-10-03 (build 26.10.3.1)", text);
        Assert.Contains("- UAT (Shop staging / lcs): YES - deployed 2026-10-04 (build 2026.10.4.1)", text);
        Assert.Contains("- PROD (Shop master / lcs): NOT YET - last deployment 2026-09-20 (build 2026.9.20.1) does not contain it", text);
        Assert.Contains("- UAT branch staging (portal): merged", text);
        Assert.Contains("- PROD branch master (portal): NOT merged yet", text);
        Assert.DoesNotContain("Other / x", text); // a stage of a branch this change is not on
        Assert.Contains("acceptance criteria changed after the first code change (2026-10-05 Anna Nowak)", text);
        Assert.Contains("TASKS\n- #2 Task [To Do] Build the form", text);
    }

    [Fact]
    public async Task Changes_list_objects_with_methods_and_diff_one_file()
    {
        var list = (await TraceQueries.ChangesAsync(lens, null, "C523", null, null, null)).Replace("\r", "");
        Assert.Contains("- Foo (class) [edit]: ~run", list);
        Assert.Contains("- Readme.txt [edit]", list);

        var diff = (await TraceQueries.ChangesAsync(lens, null, "523", null, "foo.xml", null)).Replace("\r", "");
        Assert.Contains("~ run\n@@ -1,4 +1,5 @@\n void run()\n {\n-    login();\n+    if (this.check())\n+        login();\n }", diff);

        var none = await TraceQueries.ChangesAsync(lens, null, "523", null, "Bar.xml", null);
        Assert.Contains("no changed file matches 'Bar.xml'; files: Foo.xml, Readme.txt", none);
        await Assert.ThrowsAsync<ArgumentException>(() => TraceQueries.ChangesAsync(lens, null, "abc1234def", null, null, null));
    }
}
