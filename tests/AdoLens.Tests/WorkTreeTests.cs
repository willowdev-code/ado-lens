namespace AdoLens.Tests;

public class WorkTreeTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-tree-{Guid.NewGuid():N}");
    readonly DemoItems items = new();
    readonly Lens lens;

    public WorkTreeTests()
    {
        var ado = items.Routes(new DemoProject().Routes(new FakeAdo()));
        var s = new Settings { DefaultConnection = "contoso" };
        s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
        lens = new Lens(s, "0.0.0", dir, _ => null, _ => "pat", _ => ado.Client());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    const string Tree = """
        - Feature: Self-service returns
          - Product Backlog Item: Return form | customer picks the order line
            The form shows only delivered lines.
            - Task: Build the form @Anna Nowak
            - Task: Test the form
          - Bug: Old return link 404
        """;

    [Fact]
    public void An_indented_list_becomes_a_tree()
    {
        var roots = WorkTree.Parse(Tree);
        var feature = Assert.Single(roots);
        Assert.Equal(("Feature", "Self-service returns"), (feature.Type, feature.Title));
        var pbi = feature.Children[0];
        Assert.Equal("customer picks the order line\nThe form shows only delivered lines.", pbi.Description);
        Assert.Equal(["Build the form", "Test the form"], pbi.Children.Select(c => c.Title));
        Assert.Equal("Anna Nowak", pbi.Children[0].AssignedTo);
        Assert.Equal("Bug", feature.Children[1].Type);
        Assert.Throws<ArgumentException>(() => WorkTree.Parse("just text"));
    }

    [Fact]
    public async Task The_tree_is_previewed_then_created_top_down_and_undone_at_once()
    {
        var preview = await WorkTree.CreateAsync(lens, null, null, 3, Tree, null, null, null);
        Assert.StartsWith("PREVIEW - nothing written yet. 5 work item(s) under #3:", preview);
        Assert.Contains("    - Task: Build the form (@Anna Nowak)", preview);
        Assert.Empty(items.Created);

        var token = preview[(preview.LastIndexOf("confirm=", StringComparison.Ordinal) + 8)..].TrimEnd('.');
        var done = await WorkTree.CreateAsync(lens, null, null, 3, Tree, null, null, token);
        Assert.StartsWith("created 5 work item(s):", done);
        Assert.Equal(5, items.Created.Count);
        Assert.Contains("workItems/3", items.Created[0]); // the feature goes under #3
        Assert.Contains("ado_create_tree", Audit.List(lens, null, 5));

        await Assert.ThrowsAsync<ArgumentException>(() => WorkTree.CreateAsync(lens, null, null, null, "- Story: x", null, null, null));
    }
}
