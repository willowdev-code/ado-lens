using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoLens.Tests;

public class WikiTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-wiki-{Guid.NewGuid():N}");
    readonly List<(string Url, string? IfMatch, string Body)> puts = [];
    string pageContent = "# Setup\n\nEnvironments and their settings.";
    string pageTag = "v1";
    readonly Lens lens;

    static HttpResponseMessage Json(string body, string? etag = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
        return res;
    }

    public WikiTests()
    {
        var ado = new FakeAdo()
            .On("/contoso/Demo/_apis/wiki/wikis/w1/pages", r =>
            {
                var path = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["path"];
                if (r.Method == HttpMethod.Put)
                {
                    var body = (string)JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["content"]!;
                    var ifMatch = r.Headers.IfMatch.FirstOrDefault()?.ToString();
                    puts.Add((Uri.UnescapeDataString(r.RequestUri.Query), ifMatch, body));
                    if (path == "/New page" && ifMatch is null) return Json($$"""{"path":"/New page"}""", "n1", HttpStatusCode.Created);
                    if (path == "/Setup" && ifMatch is null) return Json("""{"message":"The page '/Setup' specified in the add operation already exists in the wiki."}""", null, HttpStatusCode.PreconditionFailed);
                    if (ifMatch != $"\"{pageTag}\"") return Json("""{"message":"The version of the page does not match."}""", null, HttpStatusCode.PreconditionFailed);
                    pageContent = body;
                    pageTag = "v2";
                    return Json("""{"path":"/Setup","remoteUrl":"https://dev.azure.com/contoso/Demo/_wiki/wikis/w1?pagePath=%2FSetup"}""", pageTag);
                }
                if (path == "/")
                    return Json("""
                        {"path":"/","subPages":[
                          {"path":"/Setup","order":1,"subPages":[{"path":"/Setup/Dual Write","order":0,"subPages":[{"path":"/Setup/Dual Write/Errors","order":0}]}]},
                          {"path":"/Home","order":0,"subPages":[]}]}
                        """);
                if (path == "/Setup")
                    return Json(new JsonObject { ["path"] = "/Setup", ["content"] = pageContent, ["subPages"] = new JsonArray(new JsonObject { ["path"] = "/Setup/Dual Write" }) }.ToJsonString(), pageTag);
                return Json("""{"message":"page not found"}""", null, HttpStatusCode.NotFound);
            })
            .On("/contoso/Demo/_apis/wiki/wikis", """{"value":[{"id":"w0","name":"Code.wiki","type":"codeWiki","versions":[{"version":"main"}]},{"id":"w1","name":"Demo.wiki","type":"projectWiki","versions":[{"version":"wikiMaster"}]}]}""")
            .On("/contoso/Demo/_apis/search/wikisearchresults", """
                {"count":1,"results":[{"fileName":"Dual-Write.md","path":"/Setup/Dual-Write.md","wiki":{"name":"Demo.wiki"},
                 "hits":[{"highlights":["Configure <highlighthit>dual</highlighthit> write per legal entity"]}]}]}
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
    public async Task Tree_lists_pages_in_order_with_hidden_depth_counted()
    {
        var text = (await WikiQueries.ReadAsync(lens, null, null, null, null, null, 2, null)).Replace("\r", "");
        Assert.StartsWith("wiki Demo.wiki (project wiki) - 4 pages\n- Home\n- Setup\n  - Dual Write (+1)\n", text);
    }

    [Fact]
    public async Task Page_comes_as_markdown_with_version_and_subpages()
    {
        var text = await WikiQueries.ReadAsync(lens, null, null, null, "Setup", null, 2, null);
        Assert.Contains("/Setup (wiki Demo.wiki, version v1)", text);
        Assert.Contains("subpages (1): /Setup/Dual Write", text);
        Assert.Contains("# Setup\n\nEnvironments and their settings.", text.Replace("\r", ""));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => WikiQueries.ReadAsync(lens, null, null, null, "Nope", null, 2, null));
        Assert.Contains("no page /Nope", missing.Message);
    }

    [Fact]
    public async Task Replace_needs_the_version_and_never_overwrites_a_newer_page()
    {
        var noVersion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WikiQueries.WriteAsync(lens, null, null, null, "Setup", "x", "replace", null, null));
        Assert.Contains("replace needs the page version", noVersion.Message);
        Assert.Empty(puts);

        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WikiQueries.WriteAsync(lens, null, null, null, "Setup", "x", "replace", "v0", null));
        Assert.Contains("was changed since it was read (version v0)", stale.Message);
        Assert.Equal("# Setup\n\nEnvironments and their settings.", pageContent);

        var ok = await WikiQueries.WriteAsync(lens, null, null, null, "Setup", "# Setup\n\nNew text.", "replace", "v1", "Rewrite intro");
        Assert.StartsWith("replaced /Setup (wiki Demo.wiki, new version v2", ok);
        Assert.Equal("\"v1\"", puts[^1].IfMatch);
        Assert.Contains("comment=Rewrite intro", puts[^1].Url);
    }

    [Fact]
    public async Task Append_keeps_the_current_text_and_uses_its_version()
    {
        await WikiQueries.WriteAsync(lens, null, null, null, "/Setup.md", "## Changes\n- added UAT", "append", null, null);
        Assert.Equal("# Setup\n\nEnvironments and their settings.\n\n## Changes\n- added UAT", pageContent);
        Assert.Equal("\"v1\"", puts.Single().IfMatch);
    }

    [Fact]
    public async Task Create_refuses_to_overwrite_and_creates_new_pages()
    {
        var exists = await Assert.ThrowsAsync<InvalidOperationException>(() => WikiQueries.WriteAsync(lens, null, null, null, "Setup", "x", null, null, null));
        Assert.Contains("/Setup already exists: use mode=append", exists.Message);

        var created = await WikiQueries.WriteAsync(lens, null, null, null, "New page", "# New", null, null, null);
        Assert.StartsWith("created /New page (wiki Demo.wiki, new version n1", created);
    }

    [Fact]
    public async Task Search_shows_paths_and_plain_snippets()
    {
        var text = await WikiQueries.ReadAsync(lens, null, null, null, null, "dual write", 2, null);
        Assert.Contains("- /Setup/Dual Write (wiki Demo.wiki): Configure dual write per legal entity", text);
    }
}
