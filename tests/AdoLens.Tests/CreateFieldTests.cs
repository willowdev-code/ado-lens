namespace AdoLens.Tests;

/// <summary>Creating items in processes that mark system fields required; loose field values; readable errors.</summary>
public class CreateFieldTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-create-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly Lens lens;

    public CreateFieldTests()
    {
        var ado = new DemoItems().Routes(demo.Routes(new FakeAdo()));
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
    public async Task Ids_azure_devops_fills_itself_are_never_asked_for()
    {
        demo.SystemIdsRequired = true;
        demo.CustomerRequired = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkQueries.CreateAsync(lens, null, null, "Product Backlog Item", new WriteRequest { Title = "x", Iteration = "Sprint 6" }));
        Assert.Contains("Customer [Custom.Customer] (Alpha | Beta)", error.Message);
        Assert.DoesNotContain("Iteration ID", error.Message);
        Assert.DoesNotContain("Area ID", error.Message);
        Assert.True(WorkItems.FilledByAzureDevOps("System.AreaLevel3"));
    }

    [Fact]
    public void Field_values_come_as_text_numbers_or_a_json_string()
    {
        Assert.Equal("2", McpTools.Fields(System.Text.Json.JsonDocument.Parse("""{"Priority": 2}""").RootElement)!["priority"]);
        Assert.Equal("true", McpTools.Fields(System.Text.Json.JsonDocument.Parse("""{"Blocked": true}""").RootElement)!["Blocked"]);
        Assert.Equal("Beta", McpTools.Fields(System.Text.Json.JsonDocument.Parse("\"{\\\"Customer\\\": \\\"Beta\\\"}\"").RootElement)!["Customer"]);
        Assert.Equal("3", McpTools.Fields(System.Text.Json.JsonDocument.Parse("\"Priority=3; Customer=Beta\"").RootElement)!["Priority"]);
        Assert.Null(McpTools.Fields(null));
    }

    [Fact]
    public async Task Unexpected_errors_still_say_what_happened()
    {
        var error = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            McpTools.Run("ado_test", new { }, () => throw new KeyNotFoundException("no key x")));
        Assert.Equal("ado_test failed (KeyNotFoundException): no key x", error.Message);
    }
}
