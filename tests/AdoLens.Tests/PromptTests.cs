using ModelContextProtocol.Server;

namespace AdoLens.Tests;

public class PromptTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-skills-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Prompts_carry_the_skill_and_the_request()
    {
        var text = Prompts.Verify("#37");
        Assert.Contains("# Checking work for a Product Owner", text); // read from skills/po-verify/SKILL.md, front matter left out
        Assert.DoesNotContain("name: po-verify", text);
        Assert.Contains("Request: Was work item #37 done as asked", text);
        Assert.Contains("Request: Pull request for PR 12.", Prompts.DevPr("PR 12"));
    }

    [Fact]
    public void Every_prompt_has_its_skill_in_the_repository()
    {
        var skills = Prompts.SkillsDir()!;
        foreach (var name in new[] { "po-verify", "po-explain", "po-story", "po-sprint", "po-refine", "po-test", "po-wiki", "dev-start", "dev-pr", "dev-build", "dev-why" })
            Assert.True(File.Exists(Path.Combine(skills, name, "SKILL.md")), name);
    }

    [Fact]
    public void The_server_registers_the_prompts()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.McpServerBuilderExtensions.WithPrompts(
            Microsoft.Extensions.DependencyInjection.McpServerServiceCollectionExtensions.AddMcpServer(services), [typeof(Prompts)]);
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var names = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<McpServerPrompt>(sp).Select(p => p.ProtocolPrompt.Name).Order().ToList();
        Assert.Equal(["dev_build", "dev_pr", "dev_start", "dev_why", "explain", "label", "refine", "sprint", "story", "test", "verify", "wiki"], names);
    }

    [Fact]
    public void Claude_Code_gets_only_the_skills_of_the_mode()
    {
        var source = Path.Combine(dir, "skills");
        var code = Path.Combine(dir, ".claude", "skills");
        foreach (var name in new[] { "po-verify", "dev-why" })
        {
            Directory.CreateDirectory(Path.Combine(source, name));
            File.WriteAllText(Path.Combine(source, name, "SKILL.md"), name);
        }
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        Assert.StartsWith("Claude Code skills for mode po: po-verify", Skills.Sync("po", source, code));
        Assert.True(File.Exists(Path.Combine(code, "po-verify", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(code, "dev-why")));

        Assert.Contains("removed: po-verify", Skills.Sync("dev", source, code));
        Assert.True(File.Exists(Path.Combine(code, "dev-why", "SKILL.md")));
        Assert.Contains("dev-why, po-verify", Skills.Sync("full", source, code).Replace("po-verify, dev-why", "dev-why, po-verify"));
    }
}
