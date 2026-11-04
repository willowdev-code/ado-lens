using System.Text.Json;
using System.Xml.Linq;

namespace AdoLens.Tests;

public class ImprovementTests
{
    [Theory]
    [InlineData("Anna Nowak (Contoso)", "Anna Nowak")]
    [InlineData("Anna Nowak", "Anna Nowak")]
    [InlineData("  Ben Kowalski (Demo Ltd) ", "Ben Kowalski")]
    public void Display_names_are_compared_without_a_company_suffix(string name, string plain) =>
        Assert.Equal(plain, Identities.Plain(name));

    [Fact]
    public void A_repository_with_its_own_keys_uses_only_those()
    {
        var env = new EnvMap(new Dictionary<string, string>
        {
            ["dev"] = "DEV", ["portal:staging"] = "UAT", ["staging"] = "UAT-old", ["Deploy/Stage 1"] = "UAT + SCM", ["portal:master"] = "PROD", ["lib:master"] = "PROD",
        });
        Assert.Equal("portal:staging", env.BranchKey("portal", "staging"));
        Assert.Null(env.BranchKey("portal", "dev"));            // portal has keys of its own: "dev" means nothing there
        Assert.Equal("dev", env.BranchKey("other", "dev"));      // a repository without own keys reads the shared ones
        Assert.Equal("staging", env.BranchKey(null, "staging"));
        Assert.Equal(["master"], env.BranchesOf("lib").Select(b => b.Branch));
        Assert.Equal(["staging", "master"], env.BranchesOf("portal").Select(b => b.Branch));
        Assert.Equal(["dev", "staging"], env.BranchesOf("other").Select(b => b.Branch));
        Assert.True(env.Order("portal:staging") < env.Order("portal:master"));
        Assert.Equal("UAT = branch staging of portal", Queries.Flow("portal:staging", "UAT"));
        Assert.Equal("UAT + SCM = release stage Deploy/Stage 1", Queries.Flow("Deploy/Stage 1", "UAT + SCM"));
    }

    [Fact]
    public void Code_can_live_in_another_project_than_the_work_items()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"adolens-cp-{Guid.NewGuid():N}");
        try
        {
            var s = new Settings { DefaultConnection = "contoso" };
            s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo", CodeProject = "ERP" };
            var session = new Lens(s, "0.0.0", dir, _ => null, _ => "pat", _ => new FakeAdo().Client()).For(null);
            Assert.Equal("Demo", session.Project(null));
            Assert.Equal("ERP", session.CodeProject(null));
            Assert.Equal("Other", session.CodeProject("Other"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_Claude_Desktop_config_is_found_where_the_installed_version_keeps_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adolens-claude-{Guid.NewGuid():N}");
        var appData = Path.Combine(root, "Roaming");
        var local = Path.Combine(root, "Local");
        try
        {
            // An older Claude Desktop: %APPDATA%\Claude.
            Directory.CreateDirectory(Path.Combine(appData, "Claude"));
            Assert.Equal([Path.Combine(appData, "Claude", "claude_desktop_config.json")], ClaudeConfigs.DesktopConfigPaths(appData, local));

            // A newer one moved its data to %LOCALAPPDATA%\Claude-Data\<app> and left a marker: the old place is not read.
            Directory.CreateDirectory(Path.Combine(local, "Claude-Data", "claude_abc"));
            File.WriteAllText(Path.Combine(appData, "Claude", ".user-data-left.claude_abc"), "{}");
            File.WriteAllText(Path.Combine(appData, "Claude", "claude_desktop_config.json"), "{}");
            Assert.Equal([Path.Combine(local, "Claude-Data", "claude_abc", "claude_desktop_config.json")], ClaudeConfigs.DesktopConfigPaths(appData, local));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_4.1_x64__abc\app\Claude.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\AnthropicClaude\app-1.2.3\resources\claude-code\claude.exe", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Claude-Data\claude_abc\claude-code\2.1\claude.exe", false)]
    [InlineData(@"C:\Users\x\.local\bin\claude.exe", false)]
    public void Only_the_desktop_app_counts_as_a_running_Claude_Desktop(string path, bool desktop) =>
        Assert.Equal(desktop, ClaudeConfigs.IsDesktopPath(path));

    [Theory]
    [InlineData("Main", "DEV")]
    [InlineData("SIT", "UAT")]
    [InlineData("Release", "PROD")]
    [InlineData("SIT_20261021", null)]
    [InlineData("joma", null)]
    public void Tfvc_branches_get_a_first_guess(string branch, string? guess) =>
        Assert.Equal(guess, EnvironmentFlow.TfvcGuess(branch));

    [Fact]
    public void Work_branches_are_not_environments()
    {
        var flow = TraceQueries.ProposeFlow(["portal:dev", "portal:000000_TestBranch", "123456_LoginForm_AB", "portal:master", "lib:staging"]);
        Assert.Equal(["portal:dev", "lib:staging", "portal:master"], flow.Select(f => f.Key));
        Assert.Equal("UAT", flow.Single(f => f.Key == "lib:staging").Guess);
    }

    [Fact]
    public void Numbered_lists_keep_their_start()
    {
        var md = Html.ToMarkdown("<ol><li>one</li><li>two</li></ol><p>between</p><ol start=\"3\"><li>three</li></ol>").Replace("\r", "");
        Assert.Contains("1. one\n2. two", md);
        Assert.Contains("3. three", md);
    }

    [Fact]
    public void Unknown_tool_parameters_are_named()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"action":{},"id":{},"reviewers":{}}}""").RootElement;
        Assert.Equal(["requiredReviewers"], ToolArguments.Unknown(schema, ["action", "ID", "requiredReviewers"]));
    }

    [Fact]
    public void A_table_is_shown_as_a_skeleton_of_fields_indexes_and_relations()
    {
        var doc = XDocument.Parse("""
            <AxTable>
              <Name>DemoTable</Name><Label>@Demo:Table</Label><TableGroup>Main</TableGroup>
              <SourceCode><Methods /></SourceCode>
              <Fields>
                <AxTableField><Name>Code</Name><ExtendedDataType>DemoCode</ExtendedDataType><Mandatory>Yes</Mandatory></AxTableField>
                <AxTableField><Name>CustAccount</Name><ExtendedDataType>CustAccount</ExtendedDataType></AxTableField>
              </Fields>
              <Indexes><AxTableIndex><Name>CodeIdx</Name><Fields><AxTableIndexField><DataField>Code</DataField></AxTableIndexField></Fields></AxTableIndex></Indexes>
              <Relations>
                <AxTableRelation><Name>Cust</Name><RelatedTable>CustTable</RelatedTable>
                  <Constraints><AxTableRelationConstraint><Field>CustAccount</Field><RelatedField>AccountNum</RelatedField></AxTableRelationConstraint></Constraints>
                </AxTableRelation>
              </Relations>
            </AxTable>
            """);
        var text = AotDiff.Skeleton(doc).Replace("\r", "");
        Assert.StartsWith("Label @Demo:Table; TableGroup Main\n", text);
        Assert.Contains("Fields (2):\n- Code: DemoCode, Mandatory Yes\n- CustAccount: CustAccount", text);
        Assert.Contains("Indexes (1):\n- CodeIdx: (Code)", text);
        Assert.Contains("Relations (1):\n- Cust: CustTable, (CustAccount)", text);
    }

    [Fact]
    public void Release_stage_links_name_the_release_and_stage_when_known()
    {
        const string url = "vstfs:///ReleaseManagement/ReleaseEnvironment/p1:9012:9034";
        Assert.Equal("release 9012, stage 9034", WorkItemFormat.Artifact(url, "Integrated in release environment", null));
        WorkItemFormat.ReleaseStages["9012:9034"] = "Release-12 / UAT: succeeded 2026-10-10";
        Assert.Equal("release Release-12 / UAT: succeeded 2026-10-10", WorkItemFormat.Artifact(url, "Integrated in release environment", null));
        Assert.Equal("commit 0123456789abcdef0123456789abcdef01234567",
            WorkItemFormat.Artifact("vstfs:///Git/Commit/p1%2Fr1%2F0123456789abcdef0123456789abcdef01234567", null, null));
    }

    [Fact]
    public void Process_rules_apply_only_when_their_conditions_hold()
    {
        var onResolve = new WorkItems.RequiredRule("Custom.Area", "on moving to Resolved", [("whenStateChangedTo", null, "Resolved")]);
        Assert.True(onResolve.Applies(new Dictionary<string, object?>(), "Resolved", created: false));
        Assert.False(onResolve.Applies(new Dictionary<string, object?>(), "Active", created: false));

        var always = new WorkItems.RequiredRule("Custom.Area", "always", []);
        Assert.True(always.Applies(new Dictionary<string, object?>(), "New", created: true));

        var whenBug = new WorkItems.RequiredRule("Custom.Area", "when Custom.Kind = Bug", [("when", "Custom.Kind", "Bug")]);
        Assert.True(whenBug.Applies(new Dictionary<string, object?> { ["Custom.Kind"] = "bug" }, "New", created: true));
        Assert.False(whenBug.Applies(new Dictionary<string, object?>(), "New", created: true));

        // "whenWas State = (empty)": before the item had a state, i.e. on create.
        var onCreate = new WorkItems.RequiredRule("Custom.Area", "on create", [("whenWas", "System.State", "")]);
        Assert.True(onCreate.Applies(new Dictionary<string, object?>(), "New", created: true));
        Assert.False(onCreate.Applies(new Dictionary<string, object?>(), "Active", created: false));

        // A condition that cannot be known before saving does not make the field required.
        var unknown = new WorkItems.RequiredRule("Custom.Area", "whenCurrentUserIsMemberOfGroup", [("whenCurrentUserIsMemberOfGroup", null, "g1")]);
        Assert.False(unknown.Applies(new Dictionary<string, object?>(), "New", created: true));
    }
}
