namespace AdoLens;

/// <summary>
/// The skills of this program in Claude Code (%USERPROFILE%\.claude\skills): only those of the mode, because Claude
/// Code loads every installed skill's description in each session. po-* for a Product Owner, dev-* for a developer,
/// all for "full". Claude Desktop takes skills as uploaded ZIPs, prepared next to the program.
/// </summary>
public static class Skills
{
    public static string CodeDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

    public static bool ForMode(string skill, string mode) => mode switch
    {
        "full" => true,
        "dev" => skill.StartsWith("dev-", StringComparison.OrdinalIgnoreCase),
        _ => skill.StartsWith("po-", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Copies the mode's skills into Claude Code and removes this program's others; returns what it did.</summary>
    public static string Sync(string mode, string? skillsDir = null, string? codeDir = null)
    {
        var source = skillsDir ?? Prompts.SkillsDir();
        var target = codeDir ?? CodeDir;
        if (source is null || !Directory.Exists(source)) return "no skills next to the program";
        if (!Directory.Exists(Path.GetDirectoryName(target)!)) return "Claude Code is not installed for this user - skills for it skipped";
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) continue;
            var dst = Path.Combine(target, name);
            if (ForMode(name, mode))
            {
                Directory.CreateDirectory(dst);
                File.Copy(file, Path.Combine(dst, "SKILL.md"), overwrite: true);
                added.Add(name);
            }
            else if (File.Exists(Path.Combine(dst, "SKILL.md")))
            {
                Directory.Delete(dst, recursive: true);
                removed.Add(name);
            }
        }
        return $"Claude Code skills for mode {mode}: {string.Join(", ", added.DefaultIfEmpty("none"))}" +
               (removed.Count > 0 ? $"; removed: {string.Join(", ", removed)}" : "");
    }
}
