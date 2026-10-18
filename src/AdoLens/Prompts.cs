using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AdoLens;

/// <summary>
/// MCP prompts: ready commands in Claude Desktop ("+" → the server) and Claude Code ("/mcp__ado-lens__…"). Each one is
/// the instructions of the matching skill (read from the skills folder installed next to the program) plus the request,
/// so they work without uploading the skills to Claude Desktop. Prompts are not sent to the model until chosen, so they
/// cost no tokens per session.
/// </summary>
[McpServerPromptType]
public static class Prompts
{
    /// <summary>The prompt classes of a mode: developer prompts (dev_*) only in the dev and full modes.</summary>
    /// IEnumerable on purpose: an array would bind to the generic WithPrompts&lt;T&gt;(target) and register nothing.
    public static IEnumerable<Type> TypesFor(Settings settings) =>
        settings.Mode == "po" ? [typeof(Prompts)] : [typeof(Prompts), typeof(DevPrompts)];

    /// <summary>The installed skills: …\ADO Lens\skills (the program is in …\ADO Lens\bin); in a source tree, skills\ above.</summary>
    internal static string? SkillsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "skills"))) return Path.Combine(dir.FullName, "skills");
        return null;
    }

    /// <summary>A skill's instructions without its front matter, or <paramref name="fallback"/> when it is not installed.</summary>
    internal static string Skill(string name, string fallback)
    {
        if (SkillsDir() is { } dir && Path.Combine(dir, name, "SKILL.md") is var path && File.Exists(path))
        {
            var text = File.ReadAllText(path);
            if (text.StartsWith("---", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0)
                text = text[(end + 4)..];
            return text.Trim();
        }
        return fallback;
    }

    internal static string AskSkill(string skill, string fallback, string request) => Ask(skill, fallback, request);

    static string Ask(string skill, string fallback, string request) =>
        $"{Skill(skill, fallback)}\n\n---\nRequest: {request.Trim()}\nAnswer in the language of the request (Polish request → Polish answer).";

    [McpServerPrompt(Name = "verify", Title = "Check a work item: done as asked, and where is it")]
    [Description("Was a work item done as asked and where is it (DEV / UAT / PROD) - criterion by criterion.")]
    public static string Verify([Description("Work item id, e.g. 37027.")] string id) =>
        Ask("po-verify", "Use ado_trace on the work item, read the changes with ado_changes, judge each acceptance criterion done / to check / missing, say where it is deployed.",
            $"Was work item #{id.TrimStart('#')} done as asked, and where is it?");

    [McpServerPrompt(Name = "explain", Title = "Explain how a feature works")]
    [Description("How a feature works and why, in business language, with sources.")]
    public static string Explain([Description("The feature, in your words.")] string topic) =>
        Ask("po-explain", "Use ado_search, read the strongest sources (wiki, work items, code with ado_file), explain in plain language with sources; separate code from documentation.",
            $"How does this work: {topic}");

    [McpServerPrompt(Name = "story", Title = "Write a work item")]
    [Description("A requirement, bug or task written short and complete, with acceptance criteria, then created after you agree.")]
    public static string Story([Description("What is needed, in your words.")] string need,
        [Description("Parent work item id (optional).")] string? parent = null) =>
        Ask("po-story", "Write the work item with the project's real types and fields (ado_fields): goal, scope, testable acceptance criteria; show it before ado_create.",
            $"Write a work item for: {need}{(string.IsNullOrWhiteSpace(parent) ? "" : $" (parent #{parent.TrimStart('#')})")}");

    [McpServerPrompt(Name = "sprint", Title = "Sprint status or review")]
    [Description("How the sprint is going, what is at risk, what was delivered and where it is.")]
    public static string Sprint([Description("current (default), previous, next or a sprint name.")] string? sprint = null) =>
        Ask("po-sprint", "Use ado_sprint, then ado_release_notes for what was finished; summarize progress, risks and deliveries for stakeholders.",
            $"Summarize the {(string.IsNullOrWhiteSpace(sprint) ? "current" : sprint)} sprint.");

    [McpServerPrompt(Name = "refine", Title = "Backlog refinement")]
    [Description("What in the backlog needs work before planning: missing criteria, estimates, too big items, questions for the team.")]
    public static string Refine([Description("Team (optional).")] string? team = null) =>
        Ask("po-refine", "Use ado_health and ado_backlog; propose concrete fixes and questions, split items that are too big; change nothing without the user's yes.",
            $"Prepare backlog refinement{(string.IsNullOrWhiteSpace(team) ? "" : $" for team {team}")}.");

    [McpServerPrompt(Name = "test", Title = "Test cases from acceptance criteria")]
    [Description("Test cases written from a work item's acceptance criteria, and how its tests stand.")]
    public static string Test([Description("Work item id.")] string id) =>
        Ask("po-test", "Read the item (ado_work_item) and its tests (ado_tests); propose one test case per criterion, show them, create with ado_test_create after the user agrees.",
            $"Test cases for work item #{id.TrimStart('#')}.");

    [McpServerPrompt(Name = "wiki", Title = "Write a documentation page")]
    [Description("A wiki page about a feature or a delivered change, from work items and code.")]
    public static string Wiki([Description("What the page is about.")] string topic) =>
        Ask("po-wiki", "Gather the facts (ado_search, ado_work_item, ado_file), draft the page in markdown, ask where to put it, save with ado_wiki_write after the user agrees.",
            $"Write a wiki page about: {topic}");

    [McpServerPrompt(Name = "label", Title = "Where does this message come from")]
    [Description("A D365 message pasted as seen on screen: its label, translations, and where and in which process it appears.")]
    public static string Label([Description("The message text as seen (values in it are fine).")] string text) =>
        $"Load the code tools if they are not there (ado_tools enable=code), then use ado_label with this text, then read the method that shows it (ado_file part=<method>) and explain in plain language when the user sees this message and what to do about it.\n\nMessage: {text.Trim()}\nAnswer in the language of the message.";
}

/// <summary>Prompts for developers: registered in the dev and full modes only.</summary>
[McpServerPromptType]
public static class DevPrompts
{
    static string Ask(string skill, string fallback, string request) => Prompts.AskSkill(skill, fallback, request);

    [McpServerPrompt(Name = "dev_start", Title = "Start work on a work item")]
    [Description("Developer: what to do, where in the code, a plan from the acceptance criteria.")]
    public static string DevStart([Description("Work item id.")] string id) =>
        Ask("dev-start", "Read the item and its parent (ado_work_item), find the code involved (ado_search, ado_file), propose a plan per criterion; set the state only when asked.",
            $"Start work on #{id.TrimStart('#')}.");

    [McpServerPrompt(Name = "dev_pr", Title = "Prepare or review a pull request")]
    [Description("Developer: a PR description from the work item, or a review of a PR against its work item.")]
    public static string DevPr([Description("Work item id or 'PR <n>'.")] string target) =>
        Ask("dev-pr", "For a work item: write the PR description (what and why, changes by area, how to check). For a PR: ado_pr, ado_changes, ado_review, compare with the work item's criteria.",
            $"Pull request for {target}.");

    [McpServerPrompt(Name = "dev_build", Title = "Why is the build red")]
    [Description("Developer: the failing build's errors and a proposed fix.")]
    public static string DevBuild([Description("Build id (optional: the latest failed).")] string? build = null) =>
        Ask("dev-build", "Use ado_pipeline (with build for details), read the error lines, find the code (ado_file), propose the fix.",
            string.IsNullOrWhiteSpace(build) ? "Why did the latest build fail?" : $"Why did build {build} fail?");

    [McpServerPrompt(Name = "dev_why", Title = "Why is this code like this")]
    [Description("Developer: the history of a file or object - who changed it, when, for which work items, and why.")]
    public static string DevWhy([Description("File path or name, e.g. SalesTable.xml.")] string file) =>
        Ask("dev-why", "Use ado_file history=true, read the work items behind the important changes and the diffs (ado_changes), explain the decisions.",
            $"Why is {file} the way it is?");
}
