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

    /// <summary>
    /// A skill's SKILL.md in the language of the installation: in Polish, the description (what people see in the list
    /// of skills, and what Claude matches their words against) comes from description.pl.txt. The instructions stay in
    /// English - Claude answers in the language the user writes in.
    /// </summary>
    public static string Text(string skillDir, bool polish)
    {
        var text = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
        var pl = Path.Combine(skillDir, "description.pl.txt");
        if (!polish || !File.Exists(pl)) return text;
        var description = File.ReadAllText(pl).Replace("\r", "").Replace('\n', ' ').Trim();
        return System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^description:[^\r\n]*", _ => "description: " + description, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    /// <summary>Folder of the ZIPs for Claude Desktop next to an installed program, or null when running from the sources.</summary>
    public static string? DesktopDir(string? skillsDir = null)
    {
        var source = skillsDir ?? Prompts.SkillsDir();
        if (source is null || Path.GetDirectoryName(source) is not { } app || !File.Exists(Path.Combine(app, "bin", "adolens.exe"))) return null;
        return Path.Combine(app, "skills-for-claude-desktop");
    }

    /// <summary>One ZIP per skill ("name/SKILL.md"), in the language of the installation, for upload in Claude Desktop.</summary>
    public static string DesktopZips(bool polish, string? skillsDir = null, string? outDir = null)
    {
        var source = skillsDir ?? Prompts.SkillsDir();
        var target = outDir ?? DesktopDir(source);
        if (source is null || target is null) return "";
        Directory.CreateDirectory(target);
        var made = 0;
        foreach (var dir in Directory.GetDirectories(source).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))))
        {
            var name = Path.GetFileName(dir);
            var zip = Path.Combine(target, name + ".zip");
            if (File.Exists(zip)) File.Delete(zip);
            using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("description.", StringComparison.OrdinalIgnoreCase)))
            {
                var entry = archive.CreateEntry(name + "/" + Path.GetRelativePath(dir, file).Replace('\\', '/'));
                using var w = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
                if (Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) w.Write(Text(dir, polish));
                else { w.Flush(); using var r = File.OpenRead(file); r.CopyTo(w.BaseStream); }
            }
            made++;
        }
        return $"Claude Desktop: {made} skill ZIP(s) in {target} - upload the ones you want in Settings > Capabilities > Skills";
    }

    /// <summary>Copies the mode's skills into Claude Code and removes this program's others; returns what it did.</summary>
    public static string Sync(string mode, string? skillsDir = null, string? codeDir = null, bool? polish = null)
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
                File.WriteAllText(Path.Combine(dst, "SKILL.md"), Text(dir, polish ?? Ui.Polish), new System.Text.UTF8Encoding(false));
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
