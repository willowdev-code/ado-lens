using System.Text;

namespace AdoLens;

/// <summary>
/// A question asked in every project of a connection (work items and code in two projects of one organization): the
/// answers one after another under the project's name, or the first project that has what was asked for.
/// </summary>
public static class ProjectFanOut
{
    /// <summary>Each project in turn; one project: its answer as it is.</summary>
    public static async Task<string> EachAsync(IReadOnlyList<string> projects, Func<string, Task<string>> ask)
    {
        if (projects.Count == 1) return await ask(projects[0]);
        var answers = await Task.WhenAll(projects.Select(async p =>
        {
            try { return await ask(p); }
            catch (Exception ex) when (ex is AdoApiException or InvalidOperationException) { return $"({ex.Message})"; }
        }));
        var sb = new StringBuilder();
        for (var i = 0; i < projects.Count; i++)
            sb.Append(i == 0 ? "" : "\n\n").Append($"== project {projects[i]} ==\n").Append(answers[i].TrimEnd());
        return sb.ToString();
    }

    /// <summary>
    /// The first project whose answer is not "nothing here" (an error, or text starting with "no "): a build, a file, a
    /// pipeline found by id or name lives in one project.
    /// </summary>
    public static async Task<string> FirstAsync(IReadOnlyList<string> projects, Func<string, Task<string>> ask)
    {
        if (projects.Count == 1) return await ask(projects[0]);
        var misses = new List<string>();
        foreach (var p in projects)
        {
            try
            {
                var answer = await ask(p);
                if (!answer.StartsWith("no ", StringComparison.OrdinalIgnoreCase)) return answer;
                misses.Add($"{p}: {answer}");
            }
            catch (Exception ex) when (ex is AdoApiException or InvalidOperationException) { misses.Add($"{p}: {ex.Message}"); }
        }
        return "not found in any project of the connection:\n" + string.Join('\n', misses.Select(m => "- " + Formatting.Collapse(m, 300)));
    }
}
