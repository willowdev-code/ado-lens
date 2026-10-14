using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace AdoLens.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("contoso", "contoso")]
    [InlineData("  my-org ", "my-org")]
    [InlineData("https://dev.azure.com/contoso", "contoso")]
    [InlineData("https://dev.azure.com/contoso/My%20Project/_workitems", "contoso")]
    [InlineData("https://contoso.visualstudio.com/", "contoso")]
    public void Organization_is_normalized(string input, string expected) =>
        Assert.Equal(expected, Settings.NormalizeOrganization(input));

    [Theory]
    [InlineData("https://example.com/contoso")]
    [InlineData("con toso")]
    [InlineData("-contoso")]
    public void Invalid_organization_is_rejected(string input) =>
        Assert.Throws<ArgumentException>(() => Settings.NormalizeOrganization(input));

    [Theory]
    [InlineData("https://dev.azure.com/contoso/My%20Project/_workitems/edit/1", "My Project")]
    [InlineData("https://dev.azure.com/contoso/_usersSettings/tokens", null)]
    [InlineData("https://dev.azure.com/contoso", null)]
    [InlineData("https://contoso.visualstudio.com/Demo", "Demo")]
    [InlineData("contoso", null)]
    public void Project_is_read_from_a_pasted_link(string input, string? expected) =>
        Assert.Equal(expected, Settings.ProjectFromUrl(input));

    [Fact]
    public void Settings_round_trip_without_secrets_and_keep_case_insensitive_names()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adolens-{Guid.NewGuid():N}.json");
        try
        {
            var s = new Settings { DefaultConnection = "contoso", Mode = "dev" };
            s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
            s.Save(path);
            Assert.DoesNotContain("pat", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            var loaded = Settings.Load(path);
            Assert.Equal("dev", loaded.Mode);
            Assert.Equal("Demo", loaded.Connections["CONTOSO"].Project);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Mode_is_checked() =>
        Assert.Throws<ArgumentException>(() => Settings.NormalizeMode("admin"));
}

public class ConfigTests
{
    static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    static Settings Two()
    {
        var s = new Settings { DefaultConnection = "contoso" };
        s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
        s.Connections["fabrikam"] = new Connection { Organization = "fabrikam" };
        return s;
    }

    [Fact]
    public void Credential_manager_comes_first_and_a_differing_variable_is_reported()
    {
        var cfg = Config.Resolve(Two(), null, Env((Config.PatVar, "from-env")), c => c == "contoso" ? "stored" : null);

        Assert.Equal(("contoso", "contoso", "stored", "Demo"), (cfg.Connection, cfg.Organization, cfg.Pat, cfg.Project));
        Assert.Contains("Credential Manager", cfg.PatSource);
        Assert.Contains(Config.PatVar, cfg.Warning);
    }

    [Fact]
    public void Variables_are_used_without_a_stored_pat_connection_variable_first()
    {
        var both = Config.Resolve(Two(), "fabrikam", Env(("ADOLENS_PAT_FABRIKAM", "own"), (Config.PatVar, "shared")), _ => null);
        var shared = Config.Resolve(Two(), "fabrikam", Env((Config.PatVar, "shared")), _ => null);

        Assert.Equal("own", both.Pat);
        Assert.Contains("ADOLENS_PAT_FABRIKAM", both.PatSource);
        Assert.Equal("shared", shared.Pat);
        Assert.Null(shared.Warning);
    }

    [Fact]
    public void Environment_alone_describes_a_connection_for_ci()
    {
        var cfg = Config.Resolve(new Settings(), null,
            Env((Config.OrgVar, "https://dev.azure.com/contoso"), (Config.PatVar, "p"), (Config.ProjectVar, "Demo")), _ => null);
        Assert.Equal(("contoso", "contoso", "Demo"), (cfg.Connection, cfg.Organization, cfg.Project));
    }

    [Fact]
    public void Missing_or_ambiguous_setup_points_to_the_fix()
    {
        var none = Assert.Throws<InvalidOperationException>(() => Config.Resolve(new Settings(), null, Env(), _ => null));
        var noPat = Assert.Throws<InvalidOperationException>(() => Config.Resolve(Two(), null, Env(), _ => null));
        var unknown = Assert.Throws<InvalidOperationException>(() => Config.Resolve(Two(), "nope", Env(), _ => "x"));
        var two = Two();
        two.DefaultConnection = null;
        var ambiguous = Assert.Throws<InvalidOperationException>(() => Config.Resolve(two, null, Env(), _ => "x"));

        Assert.Contains("adolens login", none.Message);
        Assert.Contains("adolens login --connection contoso", noPat.Message);
        Assert.Contains("ADOLENS_PAT_CONTOSO", noPat.Message);
        Assert.Contains("known: contoso, fabrikam", unknown.Message);
        Assert.Contains("--default-connection", ambiguous.Message);
    }

    [Fact]
    public void ToString_never_prints_the_pat() =>
        Assert.DoesNotContain("secret-token", new Config("c", "contoso", "secret-token", null, null, "test").ToString());

    [Fact]
    public async Task Unconfigured_setup_reaches_claude_as_a_tool_error()
    {
        var lens = new Lens(new Settings(), "0.0.0", Path.GetTempPath(), _ => null, _ => null);
        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => CoreTools.Fields(lens));
        Assert.Contains("adolens login", ex.Message);
    }
}

public class CommandLineTests
{
    [Fact]
    public void Options_in_both_forms_and_positionals()
    {
        var cl = CommandLine.Parse(["fields", "-type=Bug", "--all", "--", "--literal"]);
        Assert.Equal("fields", cl.Command);
        Assert.Equal("Bug", cl.Opt("type"));
        Assert.True(cl.Flag("all"));
        Assert.Equal(["--literal"], cl.Positional);
    }

    [Fact]
    public void Unknown_option_and_missing_value_fail()
    {
        Assert.Contains("unknown option", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["status", "--nope"])).Message);
        Assert.Contains("needs a value", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["login", "--org"])).Message);
    }

    [Fact]
    public void No_arguments_run_the_server() => Assert.Equal("mcp", CommandLine.Parse([]).Command);
}

public class ClaudeConfigsTests : IDisposable
{
    const string Exe = @"C:\Users\x\AppData\Local\Programs\ADO Lens\bin\adolens.exe";
    readonly string path = Path.Combine(Path.GetTempPath(), $"claude-config-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(path);

    [Fact]
    public void Register_keeps_other_servers_and_drops_pat_variables()
    {
        File.WriteAllText(path, """
            {
              "globalShortcut": "Ctrl+Space",
              "mcpServers": {
                "azureDevOps": { "command": "npx", "env": { "AZURE_DEVOPS_PAT": "plain-secret", "AZURE_DEVOPS_ORG_URL": "https://dev.azure.com/contoso" } },
                "ado-lens": { "command": "old.exe", "args": ["mcp"], "env": { "AZURE_DEVOPS_PAT": "x", "HTTPS_PROXY": "http://proxy:8080" } }
              }
            }
            """);

        Assert.Equal(["azureDevOps", "ado-lens"], ClaudeConfigs.EntriesWithPlainTextPat(path));
        Assert.True(ClaudeConfigs.Register(path, "ado-lens", Exe));

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var entry = root["mcpServers"]!["ado-lens"]!;
        Assert.Equal(Exe, (string?)entry["command"]);
        Assert.Equal("mcp", (string?)entry["args"]![0]);
        Assert.Equal("http://proxy:8080", (string?)entry["env"]!["HTTPS_PROXY"]);
        Assert.Null(entry["env"]!["AZURE_DEVOPS_PAT"]);
        // another server's settings are reported, never changed
        Assert.Equal("plain-secret", (string?)root["mcpServers"]!["azureDevOps"]!["env"]!["AZURE_DEVOPS_PAT"]);
        Assert.Equal("Ctrl+Space", (string?)root["globalShortcut"]);

        Assert.True(ClaudeConfigs.IsUpToDate(path, "ado-lens", Exe));
        Assert.False(ClaudeConfigs.Register(path, "ado-lens", Exe));
    }

    [Fact]
    public void Unregister_leaves_other_installations_alone_unless_asked()
    {
        File.WriteAllText(path, """{ "mcpServers": { "ado-lens": { "command": "D:\\elsewhere\\adolens.exe", "args": ["mcp"] } } }""");

        Assert.Equal(ClaudeConfigs.UnregisterResult.OtherInstallation, ClaudeConfigs.Unregister(path, "ado-lens", Exe, any: false));
        Assert.Equal(ClaudeConfigs.UnregisterResult.Removed, ClaudeConfigs.Unregister(path, "ado-lens", Exe, any: true));
        Assert.Equal(ClaudeConfigs.UnregisterResult.NotRegistered, ClaudeConfigs.Unregister(path, "ado-lens", Exe, any: true));
    }
}

public class CredentialStoreTests
{
    [Fact]
    public void Pat_round_trips_through_windows_credential_manager()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = $"test-{Guid.NewGuid():N}"[..30];
        try
        {
            Assert.Null(CredentialStore.Read(name));
            CredentialStore.Write(name, "contoso", "token-Ä…Ä™-123");
            Assert.Equal("token-Ä…Ä™-123", CredentialStore.Read(name));
            CredentialStore.Write(name, "contoso", "replaced");
            Assert.Equal("replaced", CredentialStore.Read(name));
            Assert.True(CredentialStore.Delete(name));
            Assert.Null(CredentialStore.Read(name));
        }
        finally
        {
            CredentialStore.Delete(name);
        }
    }
}

public class AdoClientTests
{
    [Fact]
    public async Task Throttling_is_retried_and_errors_are_readable()
    {
        var calls = 0;
        var ado = new FakeAdo()
            .On("/contoso/_apis/connectionData", _ => ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(1)) } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"authenticatedUser":{"providerDisplayName":"Demo User"}}""") })
            .On("/contoso/_apis/denied", """{"message":"Access denied."}""", HttpStatusCode.Forbidden)
            .On("/contoso/_apis/expired", "<html>sign in</html>", HttpStatusCode.NonAuthoritativeInformation);
        var client = ado.Client();

        Assert.Equal("Demo User", await client.WhoAmIAsync());
        Assert.Equal(2, calls);
        Assert.EndsWith("api-version=7.1-preview.1", ado.Requests[0]);

        var denied = await Assert.ThrowsAsync<AdoApiException>(() => client.GetAsync("_apis/denied"));
        Assert.Contains("Access denied.", denied.Message);
        Assert.Contains("permission or scope", denied.Message);
        var expired = await Assert.ThrowsAsync<AdoApiException>(() => client.GetAsync("_apis/expired"));
        Assert.Contains("adolens login", expired.Message);
    }
}

public class ToolBudgetTests
{
    /// <summary>
    /// Instructions and tool definitions are sent in every session; keep them small. Estimate: descriptions and names
    /// plus the JSON schema around them (~100 characters per tool, ~40 per parameter), about 4 characters per token.
    /// </summary>
    [Theory]
    [InlineData("po", 3000)]
    [InlineData("dev", 3500)]
    public void Tool_definitions_stay_within_budget(string mode, int maxTokens)
    {
        var settings = new Settings { Mode = mode };
        var chars = McpTools.Instructions(settings).Length;
        foreach (var m in Toolsets.TypesFor(settings).SelectMany(t => t.GetMethods()).Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            chars += 100 + m.GetCustomAttribute<McpServerToolAttribute>()!.Name!.Length;
            chars += m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description.Length ?? 0;
            foreach (var p in m.GetParameters().Where(p => p.ParameterType != typeof(Lens) && p.ParameterType != typeof(CancellationToken)))
                chars += 40 + p.Name!.Length + (p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description.Length ?? 0);
        }
        Assert.True(chars / 4 < maxTokens, $"mode {mode}: tool definitions take ~{chars / 4} tokens ({chars} characters)");
    }

    [Fact]
    public void The_server_really_registers_the_tools_of_the_mode()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.McpServerServiceCollectionExtensions.AddMcpServer(services).WithActiveTools(new Settings { Mode = "po" });
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var names = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<McpServerTool>(sp)
            .Select(t => t.ProtocolTool.Name).Order().ToList();
        Assert.Equal(["ado_comment", "ado_create", "ado_fields", "ado_link", "ado_query", "ado_status", "ado_update", "ado_work_item"], names);
    }

    [Fact]
    public void Modes_choose_tool_sets_and_extras_are_added()
    {
        var po = Toolsets.Active(new Settings { Mode = "po" });
        var devPlus = Toolsets.Active(new Settings { Mode = "dev", ExtraToolsets = ["boards"] });
        Assert.Contains(Toolsets.Boards, po);
        Assert.DoesNotContain(Toolsets.Pr, po);
        Assert.Contains(Toolsets.Boards, devPlus);
        Assert.Contains(typeof(CoreTools), Toolsets.TypesFor(new Settings { Mode = "po" }));
        Assert.Contains(typeof(WorkTools), Toolsets.TypesFor(new Settings { Mode = "dev" }));
    }
}

