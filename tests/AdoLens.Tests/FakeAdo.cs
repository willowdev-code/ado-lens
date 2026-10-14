using System.Net;
using System.Text;

namespace AdoLens.Tests;

/// <summary>In-memory Azure DevOps: answers requests by path prefix (unescaped, without the query) and records what was asked.</summary>
sealed class FakeAdo : HttpMessageHandler
{
    readonly List<(string Prefix, Func<HttpRequestMessage, HttpResponseMessage> Respond)> routes = [];
    public List<string> Requests { get; } = [];

    public FakeAdo On(string pathPrefix, Func<string> body, HttpStatusCode status = HttpStatusCode.OK)
    {
        routes.Add((pathPrefix, _ => new HttpResponseMessage(status) { Content = new StringContent(body(), Encoding.UTF8, "application/json") }));
        return this;
    }

    public FakeAdo On(string pathPrefix, string body, HttpStatusCode status = HttpStatusCode.OK) => On(pathPrefix, () => body, status);

    public FakeAdo On(string pathPrefix, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        routes.Add((pathPrefix, respond));
        return this;
    }

    public AdoClient Client() =>
        new(new HttpClient(this) { BaseAddress = new Uri("https://dev.azure.com/contoso/") }, "contoso") { Delay = (_, _) => Task.CompletedTask };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        lock (Requests) Requests.Add(url);
        var path = url.Split('?')[0];
        // Longest prefix first, so "/x/_apis/wit/workitemtypes/Bug/fields" beats "/x/_apis/wit/workitemtypes".
        foreach (var (prefix, respond) in routes.OrderByDescending(r => r.Prefix.Length))
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var res = respond(request);
                res.RequestMessage = request;
                return Task.FromResult(res);
            }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request,
            Content = new StringContent("""{"message":"no route"}""", Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>A small project in the style of a customized Scrum process: neutral names only.</summary>
sealed class DemoProject
{
    public string Severity = """["1 - Critical","2 - High","3 - Medium","4 - Low"]""";
    public bool CustomerRequired;
    public bool ExtraState;
    public bool TestCaseType;

    public FakeAdo Routes(FakeAdo ado) => ado
        .On("/contoso/_apis/projects/Demo", """
            {"id":"p1","name":"Demo","defaultTeam":{"name":"Demo Team"},
             "capabilities":{"versioncontrol":{"sourceControlType":"Git","gitEnabled":"True","tfvcEnabled":"True"}}}
            """)
        .On("/contoso/_apis/projects/p1/properties", """{"value":[{"name":"System.Process Template","value":"Scrum"},{"name":"System.ProcessTemplateType","value":"proc-1"}]}""")
        .On("/contoso/_apis/work/processes/proc-1", """{"typeId":"proc-1","name":"Contoso Scrum"}""")
        .On("/contoso/Demo/_apis/wit/workitemtypecategories", """{"value":[{"referenceName":"Microsoft.BugCategory","workItemTypes":[{"name":"Bug"}]}]}""")
        .On("/contoso/_apis/wit/fields", """
            {"value":[
              {"referenceName":"System.Title","name":"Title","type":"string"},
              {"referenceName":"System.State","name":"State","type":"string"},
              {"referenceName":"System.Id","name":"ID","type":"integer","readOnly":true},
              {"referenceName":"System.Description","name":"Description","type":"html"},
              {"referenceName":"Microsoft.VSTS.Common.AcceptanceCriteria","name":"Acceptance Criteria","type":"html"},
              {"referenceName":"Microsoft.VSTS.Common.Severity","name":"Severity","type":"string","isPicklist":true},
              {"referenceName":"Custom.Customer","name":"Customer","type":"string","isPicklist":true},
              {"referenceName":"WEF_ABC_Kanban.Column","name":"Board Column","type":"string"},
              {"referenceName":"Microsoft.VSTS.Scheduling.Effort","name":"Effort","type":"double"},
              {"referenceName":"Microsoft.VSTS.Scheduling.RemainingWork","name":"Remaining Work","type":"double"}
            ]}
            """)
        .On("/contoso/Demo/_apis/wit/workitemtypes", () => $$$"""
            {"value":[
              {"name":"Product Backlog Item","referenceName":"Contoso.PBI","isDisabled":false,
               "states":[{"name":"New","category":"Proposed"},{"name":"Committed","category":"InProgress"},{"name":"Done","category":"Completed"}{{{(ExtraState ? ",{\"name\":\"Review\",\"category\":\"InProgress\"}" : "")}}}],
               "transitions":{"":[{"to":"New"}],"New":[{"to":"Committed"},{"to":"New"}],"Committed":[{"to":"Done"},{"to":"New"}],"Done":[{"to":"Committed"}]}},
              {"name":"Bug","referenceName":"Microsoft.VSTS.WorkItemTypes.Bug","isDisabled":false,
               "states":[{"name":"New","category":"Proposed"},{"name":"Done","category":"Completed"}],"transitions":{}},
              {"name":"Task","referenceName":"Microsoft.VSTS.WorkItemTypes.Task","isDisabled":false,
               "states":[{"name":"To Do","category":"Proposed"},{"name":"Done","category":"Completed"}],"transitions":{}},
              {"name":"Feature","referenceName":"Microsoft.VSTS.WorkItemTypes.Feature","isDisabled":false,"states":[],"transitions":{}},
              {"name":"Old Thing","referenceName":"Contoso.Old","isDisabled":true,"states":[],"transitions":{}}{{{(TestCaseType ? ",{\"name\":\"Test Case\",\"referenceName\":\"Microsoft.VSTS.WorkItemTypes.TestCase\",\"isDisabled\":false,\"states\":[{\"name\":\"Design\",\"category\":\"Proposed\"}],\"transitions\":{}}" : "")}}}
            ]}
            """)
        .On("/contoso/Demo/_apis/wit/workitemtypes/Test Case/fields", """
            {"value":[{"referenceName":"System.Title","name":"Title","alwaysRequired":true},{"referenceName":"Microsoft.VSTS.TCM.Steps","name":"Steps"}]}
            """)
        .On("/contoso/Demo/_apis/wit/workitemtypes/Product Backlog Item/fields", () => $$$"""
            {"value":[
              {"referenceName":"System.Title","name":"Title","alwaysRequired":true},
              {"referenceName":"System.Id","name":"ID"},
              {"referenceName":"System.Description","name":"Description"},
              {"referenceName":"Microsoft.VSTS.Common.AcceptanceCriteria","name":"Acceptance Criteria"},
              {"referenceName":"Custom.Customer","name":"Customer","alwaysRequired":{{{(CustomerRequired ? "true" : "false")}}},"allowedValues":["Alpha","Beta"]},
              {"referenceName":"WEF_ABC_Kanban.Column","name":"Board Column"}
            ]}
            """)
        .On("/contoso/Demo/_apis/wit/workitemtypes/Bug/fields", () => $$$"""
            {"value":[
              {"referenceName":"System.Title","name":"Title","alwaysRequired":true},
              {"referenceName":"Microsoft.VSTS.Common.Severity","name":"Severity","defaultValue":"3 - Medium","allowedValues":{{{Severity}}}},
              {"referenceName":"Custom.Customer","name":"Customer","allowedValues":["Alpha","Beta"]}
            ]}
            """)
        .On("/contoso/Demo/_apis/wit/workitemtypes/Task/fields", """{"value":[{"referenceName":"System.Title","name":"Title","alwaysRequired":true}]}""")
        .On("/contoso/Demo/_apis/wit/workitemtypes/Feature/fields", """{"value":[{"referenceName":"System.Title","name":"Title","alwaysRequired":true}]}""")
        .On("/contoso/Demo/Demo Team/_apis/work/backlogconfiguration", """
            {"portfolioBacklogs":[{"name":"Features","workItemTypes":[{"name":"Feature"}]}],
             "requirementBacklog":{"name":"Backlog items","workItemTypes":[{"name":"Product Backlog Item"},{"name":"Bug"}]},
             "taskBacklog":{"name":"Tasks","workItemTypes":[{"name":"Task"}]}}
            """);
}
