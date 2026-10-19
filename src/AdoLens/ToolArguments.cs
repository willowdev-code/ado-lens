using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AdoLens;

/// <summary>
/// How tool arguments are read. Claude does not always send the JSON type of the schema: ids=714800 for a text
/// parameter, workItems=[1,2], file=1, top="20", draft="true". Without this the MCP SDK fails before the tool runs and
/// Claude only gets "An error occurred invoking …". A number or a list given for text becomes that text ("714800",
/// "1;2"), a number given as text is read as a number, "true"/"false" as a yes/no.
/// </summary>
public static class ToolArguments
{
    public static readonly JsonSerializerOptions Json = Create();

    static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        o.Converters.Add(new LenientString());
        o.Converters.Add(new LenientBool());
        o.MakeReadOnly();
        return o;
    }

    /// <summary>
    /// A tool that reads its arguments leniently but shows Claude the plain schema (with a custom converter the schema
    /// would lose its types, and number-as-text would add patterns to every number).
    /// </summary>
    public static McpServerTool Tool(MethodInfo method, IServiceProvider services)
    {
        var tool = McpServerTool.Create(method, options: new McpServerToolCreateOptions { Services = services, SerializerOptions = Json });
        tool.ProtocolTool.InputSchema = McpServerTool.Create(method, options: new McpServerToolCreateOptions { Services = services }).ProtocolTool.InputSchema;
        return tool;
    }

    /// <summary>Text of a value given in any JSON shape: a list becomes "a;b".</summary>
    public static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Array => string.Join(";", e.EnumerateArray().Select(Text).Where(s => s.Length > 0)),
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => e.GetRawText(),
    };

    sealed class LenientString : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            if (reader.TokenType == JsonTokenType.Null) return null;
            using var doc = JsonDocument.ParseValue(ref reader);
            return Text(doc.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    sealed class LenientBool : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.GetDouble() != 0,
            JsonTokenType.String => (reader.GetString() ?? "").Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" or "tak" => true,
                "false" or "no" or "0" or "nie" or "" => false,
                var s => throw new JsonException($"'{s}' is not true or false"),
            },
            _ => throw new JsonException("expected true or false"),
        };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    static IEnumerable<string> Parameters(JsonElement schema) =>
        schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p.EnumerateObject().Select(x => x.Name) : [];

    internal static List<string> Unknown(JsonElement schema, IEnumerable<string> given)
    {
        var known = Parameters(schema).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return known.Count == 0 ? [] : given.Where(g => !known.Contains(g)).ToList();
    }

    /// <summary>
    /// A failure outside the tool's own code (an argument that cannot be read at all) comes back as text Claude can act
    /// on instead of the SDK's bare "An error occurred".
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Errors => next => async (context, ct) =>
    {
        // A parameter the tool does not have would be dropped without a word (a session started before an update
        // still has the old descriptions): say so with the parameters there are.
        if (context.Params is { Name: { } name, Arguments: { Count: > 0 } args }
            && context.Server.ServerOptions.ToolCollection?.TryGetPrimitive(name, out var called) == true
            && Unknown(called.ProtocolTool.InputSchema, args.Keys) is { Count: > 0 } unknown)
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"{name}: no parameter {string.Join(", ", unknown)}; its parameters: {string.Join(", ", Parameters(called.ProtocolTool.InputSchema))}. " +
                                                         "If ADO Lens was updated during this conversation, a new conversation (or restarting Claude) shows the current ones." }],
            };
        try { return await next(context, ct); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            var tool = context.Params?.Name ?? "tool";
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"{tool}: the arguments could not be read ({ex.GetType().Name}): {ex.Message}" }],
            };
        }
    };
}
