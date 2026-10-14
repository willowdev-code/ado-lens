using System.Text;

namespace AdoLens;

/// <summary>Setup and diagnostic commands; query commands print the same text as the MCP tools.</summary>
sealed class Cli(CommandLine cl, string version, bool showTokens = false)
{
    // Exit codes used by install.ps1.
    public const int RegistrationNeeded = 3;
    public const int NotConfigured = 4;
    public const int ConnectionFailed = 5;
    public const int ClaudeRunning = 7;

    public static void PrintUsage() => Console.WriteLine("""
        ADO Lens for Azure DevOps - work items, boards, wiki, Git and TFVC for AI assistants over MCP

        usage: adolens [command] [options]

          mcp (default)   run the MCP server over stdio (this is what Claude starts)
          login           add or update a connection: organization, default project and PAT
                            --org <name|url>  --project <name>  --team <name>  --connection <name>
                            --pat-stdin (read the PAT from standard input)  --default (make it the default connection)
          logout          remove the stored PAT of a connection        --connection <name>
          connections     list the connections
          config          show or change settings
                            --connection <name> --project <name> --team <name>   (defaults of a connection)
                            --default-connection <name>  --mode po|dev|full  --usage-log true|false
                            --show-tokens true|false  --add-toolset <set>  --remove-toolset <set>  --confirm-above <n>
          status          configuration, connection check, metadata and Claude registrations
                            --connection <name>  --offline  --quiet
          register        add / update the 'ado-lens' MCP server in Claude Desktop and Claude Code
                            --desktop | --code  --name <server>  --exe <path>  --check  --force
          unregister      remove it (only when it points to this exe, unless --any)
          refresh         fetch the work item structure of a project now     --connection  --project
          fields          same as the ado_fields tool: [--type <t>] [--field <f>] [--changes 30d] [--all] [--refresh]
          item <ids>      work items                     --include comments,history  --detail summary|normal|full
          query           --wiql "<WIQL>" | --query "<saved query path or id>"  [--top 50]
          create          --type <t> --title <t> [--description <md>] [--criteria <md>] [--assign <name>] [--parent <id>]
                            [--iteration <path|current>] [--area <path>] [--tags "a; b"] [--state <s>] [--field "Name=Value"]…
          update <ids>    or --wiql "<WIQL>"; same options as create, plus --reason, --comment <md>, --confirm <token>
          comment <id> <markdown with @[name] mentions>
          link <id> <targets>  --type parent|child|related|predecessor|successor|duplicate|duplicate-of|tested-by|tests [--remove]
          sprint [current|next|previous|<name>]   --team <t> --detail summary|normal|full
          board [<board>]  --team <t>  --move <id> --column <name>  --detail summary|normal|full
          backlog          --level requirement|portfolio|task|<name>  --top 30  --move <id> --above <id> | --below <id>
          myday            --since 1d|3d|2w|yyyy-MM-dd (default: the previous working day)
          wiki [<page path>]  --search <text>  --depth 2  --detail full  --wiki <name>
          wiki-write <page path>  --file <page.md> (or standard input)  --mode create|append|prepend|replace
                            --version <v> (for replace, from 'wiki <path>')  --comment <text>
          api <path>      raw JSON of a GET request (development)
          stats           MCP calls of the last days: answer sizes, times, empty answers   --days 7 --top 10
                            --live: show every call as Claude makes it (tool, tokens, ms), until Ctrl+C
          version         print the version

        --tokens after any command prints the estimated size of the answer ("[~230 tokens]");
        'config --show-tokens true' adds it to every answer, in the CLI and for Claude.

        PATs are kept in Windows Credential Manager ("ado-lens:<connection>"), never in files or in the Claude
        configuration. Without a stored PAT, ADOLENS_PAT_<CONNECTION> and then AZURE_DEVOPS_PAT are used.
        """);

    string SettingsPath => Settings.DefaultPath;
    string ServerName => cl.Opt("name") ?? ClaudeConfigs.DefaultServerName;
    string ExePath => cl.Opt("exe") ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "adolens.exe");

    Lens NewLens() => Lens.Default(version);

    /// <summary>Prints an answer; with --tokens (or 'config --show-tokens true') followed by its estimated size.</summary>
    void Print(string answer)
    {
        Console.WriteLine(answer);
        if (showTokens || Settings.Load(SettingsPath).ShowTokens) Console.WriteLine(Usage.Footer(answer));
    }

    public async Task<int> LoginAsync()
    {
        var settings = Settings.Load(SettingsPath);
        string org;
        string? urlProject = null;
        try
        {
            var orgText = cl.Opt("org");
            var existing = cl.Peek("connection") is { } c0 && settings.Connections.TryGetValue(c0, out var e0) ? e0 : null;
            orgText ??= existing?.Organization
                ?? Prompt("Azure DevOps address (paste a link from the browser, e.g. https://dev.azure.com/contoso/Project): ");
            if (string.IsNullOrWhiteSpace(orgText)) { Console.Error.WriteLine("organization is required"); return 2; }
            org = Settings.NormalizeOrganization(orgText);
            urlProject = Settings.ProjectFromUrl(orgText);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        string name;
        try { name = Settings.NormalizeConnectionName(cl.Opt("connection") ?? org); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        var conn = settings.Connections.TryGetValue(name, out var stored) ? stored : new Connection();
        conn.Organization = org;

        var pat = cl.Flag("pat-stdin") || Console.IsInputRedirected
            ? Console.In.ReadLine()
            : ReadSecret($"PAT for {org} (create it at https://dev.azure.com/{org}/_usersSettings/tokens; input hidden): ");
        pat = pat?.Trim();
        if (string.IsNullOrEmpty(pat)) { Console.Error.WriteLine("PAT is required"); return 2; }

        var client = new AdoClient(AdoClient.CreateHttpClient(org, pat, version), org);
        Console.Write($"checking access to https://dev.azure.com/{org} ... ");
        List<string> projects;
        try
        {
            var user = await client.WhoAmIAsync();
            projects = (await client.GetListAsync("_apis/projects?$top=500"))
                .Select(p => Json.Str(p, "name")).Where(p => p is not null).Select(p => p!).Order(StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine($"OK, signed in as {user}");
        }
        catch (Exception ex) when (ex is AdoApiException or HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine("failed");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("nothing was saved");
            return ConnectionFailed;
        }

        var project = cl.Opt("project") ?? urlProject ?? conn.Project;
        if (project is null && projects.Count > 0 && !Console.IsInputRedirected)
            project = PickProject(projects);
        if (project is not null && projects.Count > 0 && !projects.Contains(project, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"note: project '{project}' is not visible with this PAT");
        conn.Project = project is null ? null : projects.FirstOrDefault(p => p.Equals(project, StringComparison.OrdinalIgnoreCase)) ?? project;
        if (cl.Opt("team") is { } team) conn.Team = NullIfEmpty(team);

        CredentialStore.Write(name, org, pat);
        settings.Connections[name] = conn;
        if (cl.Flag("default") || settings.DefaultConnection is null || !settings.Connections.ContainsKey(settings.DefaultConnection))
            settings.DefaultConnection = name;
        settings.Save(SettingsPath);

        Console.WriteLine($"connection '{name}' saved{(settings.DefaultConnection == name ? " (default)" : "")}: {SettingsPath}");
        Console.WriteLine($"PAT saved in Windows Credential Manager: {CredentialStore.TargetFor(name)}");
        if (conn.Project is { } p)
            Console.WriteLine($"default project: {p}");
        return 0;
    }

    static string? PickProject(List<string> projects)
    {
        Console.WriteLine("Projects:");
        for (var i = 0; i < projects.Count; i++) Console.WriteLine($"  [{i + 1}] {projects[i]}");
        var answer = Prompt("Default project (number or name, Enter to skip): ");
        if (string.IsNullOrWhiteSpace(answer)) return null;
        return int.TryParse(answer, out var n) && n >= 1 && n <= projects.Count ? projects[n - 1] : answer;
    }

    public int Logout()
    {
        var settings = Settings.Load(SettingsPath);
        var name = cl.Opt("connection") ?? settings.DefaultConnection;
        if (string.IsNullOrWhiteSpace(name)) { Console.WriteLine("no connection configured - nothing to remove"); return 0; }
        Console.WriteLine(CredentialStore.Delete(name)
            ? $"removed {CredentialStore.TargetFor(name)} from Windows Credential Manager"
            : $"no stored PAT for connection '{name}'");
        return 0;
    }

    public int Connections()
    {
        var settings = Settings.Load(SettingsPath);
        if (settings.Connections.Count == 0) { Console.WriteLine("no connections - add one with 'adolens login'"); return 0; }
        foreach (var (name, c) in settings.Connections.OrderBy(kv => kv.Key))
        {
            var pat = CredentialStore.Read(name) is not null ? "PAT stored" : "no stored PAT";
            Console.WriteLine($"{(name.Equals(settings.DefaultConnection, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {name,-16} " +
                              $"https://dev.azure.com/{c.Organization}  project {c.Project ?? "-"}  team {c.Team ?? "-"}  ({pat})");
        }
        return 0;
    }

    public int Config()
    {
        var settings = Settings.Load(SettingsPath);
        var changed = false;
        try
        {
            if (cl.Opt("connection") is { } name)
            {
                if (!settings.Connections.TryGetValue(name, out var conn))
                    throw new ArgumentException($"no connection named '{name}' - add it with 'adolens login --connection {name}'");
                if (cl.Opt("project") is { } p) { conn.Project = NullIfEmpty(p); changed = true; }
                if (cl.Opt("team") is { } t) { conn.Team = NullIfEmpty(t); changed = true; }
            }
            else if (cl.Peek("project") is not null || cl.Peek("team") is not null)
                throw new ArgumentException("--project / --team need --connection <name>");
            if (cl.Opt("default-connection") is { } d)
            {
                if (!settings.Connections.ContainsKey(d)) throw new ArgumentException($"no connection named '{d}'");
                settings.DefaultConnection = settings.Connections.Keys.First(k => k.Equals(d, StringComparison.OrdinalIgnoreCase));
                changed = true;
            }
            if (cl.Opt("mode") is { } m) { settings.Mode = Settings.NormalizeMode(m); changed = true; }
            if (cl.Opt("usage-log") is { } u)
            {
                settings.UsageLog = bool.TryParse(u, out var b) ? b : throw new ArgumentException("--usage-log takes true or false");
                changed = true;
            }
            while (cl.Opt("add-toolset") is { } add)
            {
                var toolset = Toolsets.Normalize(add);
                if (!settings.ExtraToolsets.Contains(toolset)) settings.ExtraToolsets.Add(toolset);
                changed = true;
            }
            while (cl.Opt("remove-toolset") is { } rem)
            {
                settings.ExtraToolsets.Remove(Toolsets.Normalize(rem));
                changed = true;
            }
            if (cl.Opt("show-tokens") is { } st)
            {
                settings.ShowTokens = bool.TryParse(st, out var b) ? b : throw new ArgumentException("--show-tokens takes true or false");
                changed = true;
            }
            if (cl.Opt("confirm-above") is { } ca)
            {
                settings.ConfirmBulkAbove = int.TryParse(ca, out var n) && n >= 0 ? n : throw new ArgumentException("--confirm-above takes a number (0 previews every update)");
                changed = true;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (changed) settings.Save(SettingsPath);
        Console.WriteLine($"settings: {SettingsPath}{(changed ? " (saved)" : "")}");
        Console.WriteLine($"mode: {settings.Mode} (tool sets: {string.Join(", ", Toolsets.Active(settings).OrderBy(t => Array.IndexOf(Toolsets.All, t)))})");
        Console.WriteLine($"usage log: {(settings.UsageLog ? "on" : "off")}   token footer: {(settings.ShowTokens ? "on" : "off")}   preview updates of more than {settings.ConfirmBulkAbove} items   default connection: {settings.DefaultConnection ?? "-"}");
        return Connections();
    }

    public async Task<int> StatusAsync()
    {
        var quiet = cl.Flag("quiet");
        var connection = cl.Opt("connection");
        var offline = cl.Flag("offline");
        int result = 0;
        try { AdoLens.Config.Resolve(Settings.Load(SettingsPath), connection, Environment.GetEnvironmentVariable, CredentialStore.Read); }
        catch (InvalidOperationException) { result = NotConfigured; }

        var text = await Queries.StatusAsync(NewLens(), connection, offline);
        if (text.Contains("connection FAILED", StringComparison.Ordinal)) result = ConnectionFailed;
        if (quiet) return result;

        Console.WriteLine(text);
        Console.WriteLine($"exe: {ExePath}");
        Console.WriteLine($"settings: {SettingsPath}{(File.Exists(SettingsPath) ? "" : " (not created yet)")}");
        Console.WriteLine("Claude:");
        foreach (var (path, _) in ClaudeConfigs.Targets(false, false))
        {
            var command = ClaudeConfigs.RegisteredCommand(path, ServerName);
            var state = command is null ? "not registered"
                : ClaudeConfigs.IsUpToDate(path, ServerName, ExePath) ? "OK" : $"points to {command} - run 'adolens register'";
            Console.WriteLine($"  '{ServerName}' in {path}: {state}");
            var plain = ClaudeConfigs.EntriesWithPlainTextPat(path);
            if (plain.Count > 0)
                Console.WriteLine($"  warning: {string.Join(", ", plain.Select(n => $"'{n}'"))} keep(s) a PAT in plain text in this file");
        }
        return result;
    }

    public int Register()
    {
        var exe = Path.GetFullPath(ExePath);
        var targets = ClaudeConfigs.Targets(cl.Flag("desktop"), cl.Flag("code"));
        if (targets.Count == 0) { Console.WriteLine("no Claude Desktop / Claude Code configuration found"); return 0; }

        var pending = targets.Where(t => !ClaudeConfigs.IsUpToDate(t.Path, ServerName, exe)).ToList();
        if (cl.Flag("check"))
        {
            foreach (var (path, _) in targets)
                Console.WriteLine($"  '{ServerName}' in {path}: {(pending.Any(p => p.Path == path) ? "needs update" : "up to date")}");
            return pending.Count > 0 ? RegistrationNeeded : 0;
        }

        if (pending.Any(t => t.IsDesktop) && !cl.Flag("force") && ClaudeConfigs.IsClaudeDesktopRunning())
        {
            Console.Error.WriteLine("Claude Desktop is running and would overwrite the change. Quit it (tray icon -> Quit) and run this again,");
            Console.Error.WriteLine("or use install.ps1, which waits for Claude to close and starts it again.");
            return ClaudeRunning;
        }

        foreach (var (path, _) in targets)
        {
            try
            {
                Console.WriteLine(ClaudeConfigs.Register(path, ServerName, exe)
                    ? $"registered '{ServerName}' in {path}"
                    : $"'{ServerName}' already up to date in {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
                return 1;
            }
        }
        if (pending.Count > 0)
            Console.WriteLine("start Claude Desktop again / start a new Claude Code session to pick this up");
        return 0;
    }

    public int Unregister()
    {
        var exe = Path.GetFullPath(ExePath);
        var any = cl.Flag("any");
        var targets = ClaudeConfigs.Targets(cl.Flag("desktop"), cl.Flag("code"));
        var owned = targets.Where(t => ClaudeConfigs.RegisteredCommand(t.Path, ServerName) is { } cmd
            && (any || string.Equals(Path.GetFullPath(cmd), exe, StringComparison.OrdinalIgnoreCase))).ToList();
        if (cl.Flag("check"))
            return owned.Count > 0 ? RegistrationNeeded : 0;
        if (owned.Any(t => t.IsDesktop) && !cl.Flag("force") && ClaudeConfigs.IsClaudeDesktopRunning())
        {
            Console.Error.WriteLine("Claude Desktop is running and would overwrite the change. Quit it (tray icon -> Quit) and run this again.");
            return ClaudeRunning;
        }
        foreach (var (path, _) in targets)
            Console.WriteLine(ClaudeConfigs.Unregister(path, ServerName, exe, any) switch
            {
                ClaudeConfigs.UnregisterResult.Removed => $"removed '{ServerName}' from {path}",
                ClaudeConfigs.UnregisterResult.OtherInstallation => $"left alone in {path}: '{ServerName}' points to another installation; use --any to remove it anyway",
                _ => $"not registered in {path}",
            });
        return 0;
    }

    public async Task<int> RefreshAsync()
    {
        var session = NewLens().For(cl.Opt("connection"));
        var project = session.Project(cl.Opt("project"));
        Console.Write($"fetching the work item structure of {project} ... ");
        var (snap, changes) = await session.Meta.RefreshAsync(project);
        Console.WriteLine($"{snap.Types.Count} types, {snap.FieldsByType.Values.Sum(f => f.Count)} type fields, {changes.Count} change(s) since the last refresh");
        Console.WriteLine($"stored in {session.Meta.Store.Path}");
        return 0;
    }

    public async Task<int> FieldsAsync()
    {
        Print(await Queries.FieldsAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("type") ?? cl.Positional.FirstOrDefault(),
            cl.Opt("field"), cl.Opt("changes"), cl.Flag("all"), cl.Flag("refresh")));
        return 0;
    }

    public async Task<int> ItemAsync()
    {
        Print(await WorkQueries.ItemAsync(NewLens(), cl.Opt("connection"), string.Join(';', cl.Positional), cl.Opt("include"), cl.Opt("detail")));
        return 0;
    }

    public async Task<int> QueryAsync()
    {
        var top = int.TryParse(cl.Opt("top"), out var t) ? t : 50;
        Print(await WorkQueries.QueryAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("wiql"), cl.Opt("query"), top));
        return 0;
    }

    /// <summary>--field "Name=Value", repeatable.</summary>
    Dictionary<string, string>? FieldOptions()
    {
        Dictionary<string, string>? fields = null;
        while (cl.Opt("field") is { } f)
        {
            var eq = f.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"--field takes Name=Value, not '{f}'");
            (fields ??= new(StringComparer.OrdinalIgnoreCase))[f[..eq].Trim()] = f[(eq + 1)..];
        }
        return fields;
    }

    public async Task<int> CreateAsync()
    {
        var type = cl.Opt("type") ?? throw new ArgumentException("--type is required");
        var r = new WriteRequest
        {
            Title = cl.Opt("title"), Description = cl.Opt("description"), AcceptanceCriteria = cl.Opt("criteria"), AssignedTo = cl.Opt("assign"),
            Parent = int.TryParse(cl.Opt("parent"), out var p) ? p : null, Iteration = cl.Opt("iteration"), Area = cl.Opt("area"),
            Tags = cl.Opt("tags"), State = cl.Opt("state"), Fields = FieldOptions(),
        };
        Print(await WorkQueries.CreateAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), type, r));
        return 0;
    }

    public async Task<int> UpdateAsync()
    {
        var lens = NewLens();
        var r = new WriteRequest
        {
            Title = cl.Opt("title"), Description = cl.Opt("description"), AcceptanceCriteria = cl.Opt("criteria"), State = cl.Opt("state"),
            Reason = cl.Opt("reason"), AssignedTo = cl.Opt("assign"), Iteration = cl.Opt("iteration"), Area = cl.Opt("area"),
            Tags = cl.Opt("tags"), Fields = FieldOptions(),
        };
        var ids = cl.Opt("ids") ?? (cl.Positional.Count > 0 ? string.Join(';', cl.Positional) : null);
        Print(await WorkQueries.UpdateAsync(lens, cl.Opt("connection"), ids, cl.Opt("wiql"), r, cl.Opt("comment"), cl.Opt("confirm"), lens.Settings.ConfirmBulkAbove));
        return 0;
    }

    public async Task<int> CommentAsync()
    {
        if (cl.Positional.Count < 2 || !int.TryParse(cl.Positional[0], out var id)) { Console.Error.WriteLine("usage: adolens comment <id> <text>"); return 2; }
        Print(await WorkQueries.CommentAsync(NewLens(), cl.Opt("connection"), id, string.Join(' ', cl.Positional.Skip(1))));
        return 0;
    }

    public async Task<int> LinkAsync()
    {
        if (cl.Positional.Count < 2 || !int.TryParse(cl.Positional[0], out var id)) { Console.Error.WriteLine("usage: adolens link <id> <targets> --type parent|child|related|…"); return 2; }
        Print(await WorkQueries.LinkAsync(NewLens(), cl.Opt("connection"), id, string.Join(';', cl.Positional.Skip(1)),
            cl.Opt("type") ?? "related", cl.Flag("remove")));
        return 0;
    }

    static int? Int(string? s) => int.TryParse(s, out var v) ? v : null;

    public async Task<int> SprintAsync()
    {
        Print(await BoardQueries.SprintAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("team"),
            cl.Positional.Count > 0 ? string.Join(' ', cl.Positional) : null, cl.Opt("detail")));
        return 0;
    }

    public async Task<int> BoardAsync()
    {
        Print(await BoardQueries.BoardAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("team"),
            cl.Positional.Count > 0 ? string.Join(' ', cl.Positional) : null, Int(cl.Opt("move")), cl.Opt("column"), cl.Opt("detail")));
        return 0;
    }

    public async Task<int> BacklogAsync()
    {
        Print(await BoardQueries.BacklogAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("team"), cl.Opt("level"),
            Int(cl.Opt("top")) ?? 30, Int(cl.Opt("move")), Int(cl.Opt("above")), Int(cl.Opt("below"))));
        return 0;
    }

    public async Task<int> MyDayAsync()
    {
        Print(await BoardQueries.MyDayAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("since")));
        return 0;
    }

    public async Task<int> WikiAsync()
    {
        Print(await WikiQueries.ReadAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("wiki"),
            cl.Positional.Count > 0 ? string.Join(' ', cl.Positional) : null, cl.Opt("search"), Int(cl.Opt("depth")) ?? 2, cl.Opt("detail")));
        return 0;
    }

    /// <summary>wiki-write &lt;path&gt; --file page.md (or the markdown from standard input).</summary>
    public async Task<int> WikiWriteAsync()
    {
        if (cl.Positional.Count == 0) { Console.Error.WriteLine("usage: adolens wiki-write <path> --file <page.md> [--mode create|append|prepend|replace] [--version <v>]"); return 2; }
        var content = cl.Opt("file") is { } file ? await File.ReadAllTextAsync(file) : await Console.In.ReadToEndAsync();
        Print(await WikiQueries.WriteAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("wiki"), string.Join(' ', cl.Positional),
            content, cl.Opt("mode"), cl.Opt("version"), cl.Opt("comment")));
        return 0;
    }

    /// <summary>Raw GET for development: prints the JSON answer of an API path (read-only).</summary>
    public async Task<int> ApiAsync()
    {
        if (cl.Positional.FirstOrDefault() is not { } path) { Console.Error.WriteLine("usage: adolens api <path, e.g. _apis/projects>"); return 2; }
        var root = await NewLens().For(cl.Opt("connection")).Client.GetAsync(path.TrimStart('/'));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(root, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public async Task<int> StatsAsync()
    {
        if (cl.Flag("live"))
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            await Usage.LiveAsync(Console.Out, cts.Token);
            return 0;
        }
        int Num(string name, int dflt) => int.TryParse(cl.Opt(name), out var n) && n > 0 ? n : dflt;
        Console.WriteLine(Usage.Report(Num("days", 7), Num("top", 10)));
        return 0;
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static string? Prompt(string text)
    {
        if (Console.IsInputRedirected) return null;
        Console.Write(text);
        return Console.ReadLine()?.Trim();
    }

    /// <summary>Reads a line without echoing it (shows * per character).</summary>
    static string ReadSecret(string text)
    {
        Console.Write(text);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
