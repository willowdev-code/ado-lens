using System.Text.Json;

namespace AdoLens.Tests;

public class ArgumentTests
{
    static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, ToolArguments.Json);

    [Fact]
    public void Arguments_in_another_JSON_shape_are_still_read()
    {
        // Claude sends ids=714800 or workItems=[1,2] for text parameters, numbers and yes/no as text.
        Assert.Equal("714800", Read<string>("714800"));
        Assert.Equal("1;2", Read<string>("[1, 2]"));
        Assert.Equal("a;b", Read<string>("[\"a\", \"b\"]"));
        Assert.Equal("true", Read<string>("true"));
        Assert.Null(Read<string>("null"));
        Assert.Equal(5, Read<int?>("\"5\""));
        Assert.Equal(3, Read<int>("3"));
        Assert.True(Read<bool>("\"true\""));
        Assert.False(Read<bool>("\"no\""));
        Assert.True(Read<bool>("1"));
        Assert.Throws<JsonException>(() => Read<bool>("\"maybe\""));
    }

    [Fact]
    public void Lenient_tools_still_show_typed_schemas()
    {
        var method = typeof(WorkTools).GetMethod(nameof(WorkTools.Update))!;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services,
            new Lens(new Settings(), "0.0.0", Path.GetTempPath(), _ => null, _ => null));
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var schema = ToolArguments.Tool(method, sp).ProtocolTool.InputSchema.GetRawText();
        Assert.Contains("\"type\":[\"string\",\"null\"]", schema.Replace(" ", ""));
        Assert.DoesNotContain("\"pattern\"", schema);
        Assert.DoesNotContain("\"lens\"", schema);
    }

    [Theory]
    [InlineData("Done", "Completed")]
    [InlineData("Closed", "Completed")]
    [InlineData("Removed", "Removed")]
    [InlineData("New", "Proposed")]
    [InlineData("Do wdrożenia", "InProgress")]
    public void Unknown_states_get_a_category_by_their_usual_name(string state, string category) =>
        Assert.Equal(category, BoardQueries.GuessCategory(state));
}
