using System.Text.Json;

namespace AdoLens;

/// <summary>
/// Where code goes in a project, found without asking anyone: release stages deployed in the last half year and the
/// long-lived branches of its Git repositories, ordered DEV → UAT → PROD with a first guess of each name.
/// </summary>
public static class EnvironmentFlow
{
    /// <summary>The effective map of a project: the local one when set, else the team's page in the wiki.</summary>
    public static async Task<(IReadOnlyDictionary<string, string> Map, string Source)> EffectiveAsync(Session session, string project, CancellationToken ct)
    {
        if (session.Config.Environments.Count > 0) return (session.Config.Environments, "local settings");
        if (await session.Team.EnvironmentsAsync(project, ct) is { } team) return (team, $"team page \"{TeamSettings.PagePath}\" in the wiki of {project}");
        return (new Dictionary<string, string>(), "none");
    }

    public static async Task<List<(string Key, string? Guess)>> DetectAsync(Session session, string project, CancellationToken ct)
    {
        var keys = new List<string>();
        foreach (var d in await session.Code.DeploymentsAsync(project, ct))
        {
            if (d.When is not { } when || when < DateTime.UtcNow.AddDays(-183)) continue;
            keys.Add(d.Branch?.StartsWith("$/", StringComparison.Ordinal) == true ? $"{d.Definition}/{d.Stage}" : Code.Short(d.Branch) ?? $"{d.Definition}/{d.Stage}");
        }
        // Long-lived branches of the repositories that changed lately (feature branches are filtered by ProposeFlow).
        try
        {
            var repos = await session.Client.GetListAsync($"{AdoClient.Scope(project)}_apis/git/repositories", ct);
            foreach (var repo in repos.Where(r => !Json.Bool(r, "isDisabled") && r.TryGetProperty("size", out var s) && s.GetInt64() > 0).Take(30))
            {
                var refs = await session.Client.GetListAsync($"_apis/git/repositories/{Json.Str(repo, "id")}/refs?filter=heads/&$top=200", ct);
                keys.AddRange(refs.Select(r => Code.Short(Json.Str(r, "name")) ?? "").Where(n => n.Length > 0 && !n.Contains('/')));
            }
        }
        catch (AdoApiException) { } // no Git, or no Code (Read) scope
        return TraceQueries.ProposeFlow(keys.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
