namespace AdoLens.Tests;

public class MetadataTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-meta-{Guid.NewGuid():N}");
    readonly DemoProject demo = new();
    readonly FakeAdo ado = new();

    public MetadataTests() => demo.Routes(ado);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    MetadataService Service() => new(ado.Client(), new MetaStore(Path.Combine(dir, "contoso.db")));

    Lens NewLens()
    {
        var s = new Settings { DefaultConnection = "contoso" };
        s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
        return new Lens(s, "0.0.0", dir, _ => null, _ => "pat", _ => ado.Client());
    }

    [Fact]
    public async Task Refresh_reads_types_levels_states_and_fields()
    {
        var (snap, changes) = await Service().RefreshAsync("Demo");

        Assert.Empty(changes);
        Assert.Equal("Contoso Scrum (based on Scrum)", snap.Project.Process);
        Assert.Equal("Git + TFVC", snap.Project.SourceControl);
        Assert.Equal(Levels.Requirement, snap.FindType("product backlog item")!.Level);
        Assert.Equal(Levels.Bug, snap.FindType("Bug")!.Level);
        Assert.Equal(Levels.Task, snap.FindType("Task")!.Level);
        Assert.Equal(Levels.Portfolio, snap.FindType("Feature")!.Level);
        Assert.Equal(["Committed"], snap.FindType("Product Backlog Item")!.Transitions["New"]);
        Assert.Equal("3 - Medium", snap.FieldsOf("Bug").Single(f => f.Name == "Severity").Default);
        // disabled types are listed but their fields are not fetched
        Assert.DoesNotContain(ado.Requests, r => r.Contains("Old Thing/fields"));
    }

    [Fact]
    public async Task Second_refresh_records_what_changed()
    {
        var svc = Service();
        await svc.RefreshAsync("Demo");
        demo.CustomerRequired = true;
        demo.ExtraState = true;
        demo.Severity = """["1 - Critical","2 - High","3 - Medium","5 - Cosmetic"]""";

        var (_, changes) = await svc.RefreshAsync("Demo");

        Assert.Contains(changes, c => c.Kind == "field required" && c.Detail == "Customer: now required");
        Assert.Contains(changes, c => c.Kind == "state added" && c.Detail.StartsWith("Review"));
        Assert.Contains(changes, c => c.Kind == "values changed" && c.Detail == "Severity: +5 - Cosmetic, -4 - Low");
        Assert.Equal(changes.Count, svc.Store.Changes("Demo", DateTime.UtcNow.AddDays(-1)).Count);
    }

    [Fact]
    public async Task Answers_are_compact_and_hide_housekeeping_fields()
    {
        var lens = NewLens();

        var types = await Queries.FieldsAsync(lens, null, null, null, null, null, false, false);
        var pbi = await Queries.FieldsAsync(lens, null, null, "Product Backlog Item", null, null, false, false);
        var pbiAll = await Queries.FieldsAsync(lens, null, null, "Product Backlog Item", null, null, true, false);
        var customer = await Queries.FieldsAsync(lens, null, null, null, "cust*", null, false, false);

        Assert.Contains("requirement: Product Backlog Item", types);
        Assert.Contains("portfolio: Feature", types);
        Assert.Contains("disabled: Old Thing", types);
        Assert.Contains("transitions: New -> Committed; Committed -> Done, New; Done -> Committed", pbi);
        Assert.Contains("Customer (Custom.Customer), string list: Alpha | Beta", pbi);
        Assert.DoesNotContain("WEF_", pbi);
        Assert.DoesNotContain("System.Id", pbi);
        Assert.Contains("WEF_ABC_Kanban.Column", pbiAll);
        Assert.Contains("used by 2 type(s): Product Backlog Item, Bug", customer);
        Assert.Contains("values: Alpha | Beta", customer);
        Assert.True(pbi.Length < 1200, $"type answer has {pbi.Length} characters");
    }

    [Fact]
    public async Task Unknown_type_lists_the_real_ones() =>
        Assert.Contains("types: Bug, Feature, Product Backlog Item, Task",
            await Queries.FieldsAsync(NewLens(), null, null, "User Story", null, null, false, false));

    [Fact]
    public void Levels_fall_back_to_categories_without_backlog_configuration()
    {
        var categories = System.Text.Json.JsonDocument.Parse("""
            {"value":[
              {"referenceName":"Microsoft.RequirementCategory","workItemTypes":[{"name":"Requirement"}]},
              {"referenceName":"Microsoft.BugCategory","workItemTypes":[{"name":"Bug"}]},
              {"referenceName":"Microsoft.HiddenCategory","workItemTypes":[{"name":"Code Review Request"}]}
            ]}
            """).RootElement;
        var map = MetadataService.LevelsByType(null, categories);
        Assert.Equal(Levels.Requirement, map["Requirement"]);
        Assert.Equal(Levels.Bug, map["Bug"]);
        Assert.Equal(Levels.Hidden, map["Code Review Request"]);
    }

    [Theory]
    [InlineData("30d", -30 * 24)]
    [InlineData("12h", -12)]
    [InlineData("2w", -14 * 24)]
    public void Since_accepts_short_forms(string text, int hours)
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddHours(hours), Formatting.ParseSince(text, now));
    }
}
