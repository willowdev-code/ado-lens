using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AdoLens.Tests;

/// <summary>Phase 5: Test Cases from criteria and the state of tests; team templates and process rules.</summary>
public class TestCaseTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-tests-{Guid.NewGuid():N}");
    readonly DemoProject demo = new() { TestCaseType = true };
    readonly DemoItems items = new();
    readonly List<string> suiteAdds = [];
    readonly Lens lens;

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public TestCaseTests()
    {
        // #1 is tested by #20 (failed in plan R1) and #21 (never run).
        ((JsonArray)items.Items[1]["relations"]!).Add(JsonNode.Parse("""{"rel":"Microsoft.VSTS.Common.TestedBy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/20"}"""));
        ((JsonArray)items.Items[1]["relations"]!).Add(JsonNode.Parse("""{"rel":"Microsoft.VSTS.Common.TestedBy-Forward","url":"https://dev.azure.com/contoso/_apis/wit/workItems/21"}"""));
        items.Items[20] = (JsonObject)JsonNode.Parse("""
            {"id":20,"fields":{"System.Id":20,"System.TeamProject":"Demo","System.WorkItemType":"Test Case","System.State":"Ready","System.Title":"Sign in works",
             "Microsoft.VSTS.TCM.Steps":"<steps id=\"0\" last=\"3\"><step id=\"2\" type=\"ValidateStep\"><parameterizedString isformatted=\"true\">&lt;DIV&gt;&lt;P&gt;Open the portal&lt;/P&gt;&lt;/DIV&gt;</parameterizedString><parameterizedString isformatted=\"true\">&lt;DIV&gt;&lt;P&gt;Sign-in page&lt;/P&gt;&lt;/DIV&gt;</parameterizedString><description/></step></steps>"},
             "relations":[]}
            """)!.AsObject();
        items.Items[21] = (JsonObject)JsonNode.Parse("""
            {"id":21,"fields":{"System.Id":21,"System.TeamProject":"Demo","System.WorkItemType":"Test Case","System.State":"Design","System.Title":"Wrong password"},"relations":[]}
            """)!.AsObject();

        var ado = items.Routes(demo.Routes(new FakeAdo()))
            .On("/contoso/Demo/_apis/test/points", """
                {"points":[{"id":1,"outcome":"Failed","testCase":{"id":"20"},"testPlan":{"name":"R1"},"suite":{"name":"Login"},
                            "lastResultDetails":{"dateCompleted":"2026-10-10T10:00:00Z","runBy":{"displayName":"Anna Nowak"}}}]}
                """)
            .On("/contoso/Demo/_apis/testplan/plans", """{"value":[{"id":7,"name":"R1"}]}""")
            .On("/contoso/Demo/_apis/testplan/Plans/7/suites", """{"value":[{"id":9,"name":"Login"}]}""")
            .On("/contoso/Demo/_apis/testplan/Plans/7/Suites/9/TestCase", r => { suiteAdds.Add(r.Content!.ReadAsStringAsync().Result); return Json("""{"value":[]}"""); })
            .On("/contoso/Demo/Demo Team/_apis/wit/templates/t1", """{"id":"t1","name":"Customer bug","fields":{"Custom.Customer":"Beta","System.Tags":"customer"}}""")
            .On("/contoso/Demo/Demo Team/_apis/wit/templates", """{"value":[{"id":"t1","name":"Customer bug","workItemTypeName":"Bug"}]}""")
            .On("/contoso/_apis/work/processes/proc-1/workItemTypes/Microsoft.VSTS.WorkItemTypes.Bug/rules", """
                {"value":[
                  {"name":"severity on done","conditions":[{"conditionType":"whenStateChangedTo","value":"Done"}],"actions":[{"actionType":"makeRequired","targetField":"Microsoft.VSTS.Common.Severity"}],"customizationType":"custom"},
                  {"name":"system","conditions":[],"actions":[{"actionType":"makeReadOnly","targetField":"System.Id"}],"customizationType":"system"}]}
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
    public void Test_cases_are_read_from_markdown_and_steps_round_trip()
    {
        var cases = TestCases.Parse("""
            ## Sign in with a company account
            1. Open the portal => the sign-in page is shown
            2. Enter a valid account -> the dashboard opens
            3. Sign out
            ## Wrong password
            - Enter a wrong password | an error is shown
            """);
        Assert.Equal(["Sign in with a company account", "Wrong password"], cases.Select(c => c.Title));
        Assert.Equal(("Enter a valid account", "the dashboard opens"), cases[0].Steps[1]);
        Assert.Equal(("Sign out", ""), cases[0].Steps[2]);

        var xml = TestCases.StepsXml(cases[0].Steps);
        Assert.Contains("type=\"ActionStep\"", xml); // the step without an expected result
        Assert.Equal(cases[0].Steps, TestCases.ReadSteps(xml));
        Assert.Throws<ArgumentException>(() => TestCases.Parse("1. a step without a case"));
        Assert.Throws<ArgumentException>(() => TestCases.Parse("## Empty case"));
    }

    [Fact]
    public async Task Test_cases_are_created_linked_and_added_to_a_suite()
    {
        var text = await TestCases.CreateAsync(lens, null, 1, "## Sign in\n1. Open => Page\n2. Sign in => Dashboard", "R1", "Login");
        Assert.Contains("created 1 test case(s) for #1 Product Backlog Item [New] Login page, added to suite 9 of plan 7", text);
        var body = items.Created.Single();
        Assert.Contains("Microsoft.VSTS.TCM.Steps", body);
        Assert.Contains("TestedBy-Reverse", body);
        Assert.Contains("Demo\\\\Sprint 6", body); // the story's iteration
        Assert.Contains("\"id\":10", suiteAdds.Single());
        Assert.Contains("ado_test_create", Audit.List(lens, null, 5));
    }

    [Fact]
    public async Task Test_status_shows_each_case_with_its_latest_outcome()
    {
        var text = (await TestCases.StatusAsync(lens, null, "1;4", null, null, "full")).Replace("\r", "");
        Assert.Contains("#1 Product Backlog Item [New] Login page: 2 test case(s) - failed 1, not planned 1", text);
        Assert.Contains("  - #20 Sign in works: Failed 2026-10-10 Anna Nowak (R1 / Login)", text);
        Assert.Contains("    1. Open the portal => Sign-in page", text);
        Assert.Contains("  - #21 Wrong password: not in any test plan", text);
        Assert.Contains("without test cases (1): #4 Error on save", text);
    }

    [Fact]
    public async Task A_team_template_fills_a_new_item_and_rules_are_listed()
    {
        await WorkQueries.CreateAsync(lens, null, null, "Bug", new WriteRequest { Title = "Crash on save", Template = "Customer bug" });
        var body = items.Created.Single();
        Assert.Contains("\"/fields/Custom.Customer\",\"value\":\"Beta\"", body);
        Assert.Contains("customer", body);

        var fields = await Queries.FieldsAsync(lens, null, null, "Bug", null, null, false, false);
        Assert.Contains("team templates: Customer bug", fields);
        Assert.Contains("- when state changed to = Done: make required Severity", fields);
        Assert.DoesNotContain("make read only", fields);
    }
}
