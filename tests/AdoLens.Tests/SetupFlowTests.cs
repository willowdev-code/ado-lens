using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoLens.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.3.0-beta.2", "0.3.0-beta.2")]
    [InlineData("v1.0.0+abc", "1.0.0")]
    public void Versions_are_read_from_tags(string tag, string expected) =>
        Assert.Equal(expected, Updater.ParseVersion(tag)!.ToString());

    [Fact]
    public void Test_releases_come_before_their_version()
    {
        Assert.True(Updater.ParseVersion("0.3.0-beta.2") < Updater.ParseVersion("0.3.0"));
        Assert.True(Updater.ParseVersion("0.3.0-beta.10") > Updater.ParseVersion("0.3.0-beta.9"));
        Assert.True(Updater.ParseVersion("0.10.0") > Updater.ParseVersion("0.9.5"));
    }

    [Fact]
    public void The_package_and_its_published_digest_are_found()
    {
        var r = Updater.ParseRelease("""
            {"tag_name":"v0.3.0","html_url":"https://github.com/x/releases/v0.3.0","body":"- new","prerelease":false,
             "assets":[{"name":"ado-lens-0.3.0.zip.sha256","browser_download_url":"https://x/s"},
                       {"name":"ado-lens-0.3.0.zip","browser_download_url":"https://x/z","digest":"sha256:ABC123"},
                       {"name":"ado-lens-setup-0.3.0.exe","browser_download_url":"https://x/e","digest":"sha256:DEF456"}]}
            """)!;
        Assert.Equal("0.3.0", r.Version.ToString());
        Assert.Equal("ado-lens-0.3.0.zip", r.AssetName);
        Assert.Equal("abc123", r.Sha256);
        Assert.Equal(("ado-lens-setup-0.3.0.exe", "https://x/e", "def456"), r.Setup);
    }
}

public class TeamSettingsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-team-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly List<(string? IfMatch, string Body)> puts = [];
    string? page;
    readonly Settings settings;
    readonly Lens lens;

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
        return r;
    }

    public TeamSettingsTests()
    {
        var ado = demo.Routes(new FakeAdo())
            .On("/contoso/Demo/_apis/wiki/wikis/w1/pages", r =>
            {
                if (r.Method == HttpMethod.Put)
                {
                    var body = (string)JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!["content"]!;
                    puts.Add((r.Headers.IfMatch.FirstOrDefault()?.ToString(), body));
                    page = body;
                    return Json("""{"path":"/ADO Lens","remoteUrl":"https://dev.azure.com/contoso/Demo/_wiki/x"}""", HttpStatusCode.OK, "v2");
                }
                return page is null ? Json("""{"message":"not found"}""", HttpStatusCode.NotFound)
                    : Json(new JsonObject { ["path"] = "/ADO Lens", ["content"] = page }.ToJsonString(), HttpStatusCode.OK, "v1");
            })
            .On("/contoso/Demo/_apis/wiki/wikis", """{"value":[{"id":"w1","name":"Demo.wiki","type":"projectWiki"}]}""")
            .On("/contoso/Demo/_apis/release/deployments", """
                {"value":[{"releaseDefinition":{"name":"Shop"},"releaseEnvironment":{"name":"lcs"},"completedOn":"2099-01-01T00:00:00Z",
                  "release":{"artifacts":[{"definitionReference":{"branch":{"id":"refs/heads/staging"},"sourceVersion":{"id":"x"}}}]}}]}
                """)
            .On("/contoso/Demo/_apis/git/repositories", """{"value":[{"id":"r1","name":"portal","size":100}]}""")
            .On("/contoso/_apis/git/repositories/r1/refs", """{"value":[{"name":"refs/heads/dev"},{"name":"refs/heads/master"},{"name":"refs/heads/feature/x"}]}""");
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
    public void The_wiki_table_round_trips_in_order()
    {
        var map = new Dictionary<string, string> { ["dev"] = "DEV (sbx01)", ["staging"] = "UAT (sbx03)", ["Main-Deploy/Stage 1"] = "UAT + SCM" };
        var parsed = TeamSettings.Parse(TeamSettings.Render(map))!;
        Assert.Equal(map.Keys, parsed.Keys);
        Assert.Equal("UAT + SCM", parsed["main-deploy/stage 1"]);
        Assert.Null(TeamSettings.Parse("# no table here"));
        // People edit the page: extra spaces, backticks and text around the table are fine.
        var edited = TeamSettings.Parse("Intro\n\n| Branch | Env |\n|:--|--:|\n|  `dev` |  DEV  |\n\nMore text\n| x | y |")!;
        Assert.Equal(["dev"], edited.Keys);
    }

    [Fact]
    public async Task Without_any_map_the_flow_is_detected_from_releases_and_branches()
    {
        var text = await Queries.SettingsAsync(lens, null, null, null, null);
        Assert.Contains("detected in Demo: dev (looks like DEV), staging (looks like UAT), master (looks like PROD)", text);
        Assert.DoesNotContain("feature", text);
    }

    [Fact]
    public async Task The_team_map_is_saved_on_the_wiki_and_used_by_everyone_unless_overridden_locally()
    {
        var saved = await Queries.SettingsAsync(lens, null, null, "dev=DEV (sbx01); staging=UAT (sbx03); master=PROD", "team");
        Assert.StartsWith("saved for the whole team on the wiki page \"/ADO Lens\" of Demo: DEV (sbx01) = branch dev -> UAT (sbx03) = branch staging -> PROD = branch master", saved);
        Assert.Null(puts.Single().IfMatch); // a new page
        Assert.Contains("| staging | UAT (sbx03) |", page);

        // Someone else's session reads the page.
        var other = new Lens(settings, "0.0.0", dir, _ => null, _ => "pat", _ => new FakeAdo().Client());
        var session = lens.For(null);
        var (map, source) = await EnvironmentFlow.EffectiveAsync(session, "Demo", default);
        Assert.Equal(["dev", "staging", "master"], map.Keys);
        Assert.Contains("team page", source);

        // A second save replaces the page with the version just read.
        await Queries.SettingsAsync(lens, null, null, "dev=DEV", "team");
        Assert.Equal("\"v1\"", puts[1].IfMatch);

        // A local map wins over the team's.
        Queries.SettingsAnswer(lens, null, "staging=My UAT");
        var (local, localSource) = await EnvironmentFlow.EffectiveAsync(lens.For(null), "Demo", default);
        Assert.Equal("My UAT", local["staging"]);
        Assert.Equal("local settings", localSource);
        _ = other;
    }
}
