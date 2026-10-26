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
        Assert.Contains("Request: Pull request for PR 12.", DevPrompts.DevPr("PR 12"));
    }

    [Fact]
    public void Every_prompt_has_its_skill_in_the_repository()
    {
        var skills = Prompts.SkillsDir()!;
        foreach (var name in new[] { "po-verify", "po-explain", "po-story", "po-sprint", "po-refine", "po-test", "po-wiki", "dev-start", "dev-pr", "dev-build", "dev-why" })
            Assert.True(File.Exists(Path.Combine(skills, name, "SKILL.md")), name);
    }

    static List<string> PromptNames(string mode)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.McpServerBuilderExtensions.WithPrompts(
            Microsoft.Extensions.DependencyInjection.McpServerServiceCollectionExtensions.AddMcpServer(services), Prompts.TypesFor(new Settings { Mode = mode }));
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<McpServerPrompt>(sp).Select(p => p.ProtocolPrompt.Name).Order().ToList();
    }

    [Fact]
    public void The_server_registers_the_prompts_of_the_mode()
    {
        // A Product Owner gets no developer prompts (pull requests, builds, code history).
        Assert.Equal(["explain", "label", "refine", "sprint", "story", "test", "verify", "wiki"], PromptNames("po"));
        Assert.Equal(["dev_build", "dev_pr", "dev_start", "dev_why", "explain", "label", "refine", "sprint", "story", "test", "verify", "wiki"], PromptNames("full"));
        Assert.Contains("dev_pr", PromptNames("dev"));
    }

    [Fact]
    public void A_Polish_installation_gets_Polish_skill_descriptions_and_prompt_titles()
    {
        var skills = Prompts.SkillsDir()!;
        foreach (var dir in Directory.GetDirectories(skills).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))))
        {
            var pl = Path.Combine(dir, "description.pl.txt");
            Assert.True(File.Exists(pl), Path.GetFileName(dir));
            // The description stays one plain YAML line: no ": " that would start a mapping.
            Assert.DoesNotContain(": ", File.ReadAllText(pl));
            var text = Skills.Text(dir, polish: true);
            Assert.Contains("description: " + File.ReadAllText(pl).Trim(), text);
            Assert.Equal(File.ReadAllText(Path.Combine(dir, "SKILL.md")), Skills.Text(dir, polish: false));
        }
        // Every prompt has its Polish title and description.
        var names = Prompts.Methods(new Settings { Mode = "full" })
            .Select(m => ((McpServerPromptAttribute)m.GetCustomAttributes(typeof(McpServerPromptAttribute), false)[0]).Name).ToList();
        Assert.All(names, n => Assert.True(Prompts.Polish.ContainsKey(n!), n));
    }

    [Fact]
    public void Claude_Desktop_gets_one_zip_per_skill_in_the_language_of_the_installation()
    {
        var source = Path.Combine(dir, "skills");
        Directory.CreateDirectory(Path.Combine(source, "po-verify"));
        File.WriteAllText(Path.Combine(source, "po-verify", "SKILL.md"), "---\nname: po-verify\ndescription: Checks work.\n---\n# Steps\n");
        File.WriteAllText(Path.Combine(source, "po-verify", "description.pl.txt"), "Sprawdza pracę.");
        var zips = Path.Combine(dir, "zips");
        Assert.Contains("1 skill ZIP(s)", Skills.DesktopZips(polish: true, source, zips));
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(zips, "po-verify.zip"));
        Assert.Equal(["po-verify/SKILL.md"], zip.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(zip.Entries[0].Open());
        Assert.Contains("description: Sprawdza pracę.", reader.ReadToEnd());
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
