using System.Text;

namespace AdoLens;

/// <summary>Setup and diagnostic commands; query commands print the same text as the MCP tools.</summary>
sealed class Cli(CommandLine cl, string version, bool showTokens = false)
{
    // Exit codes used by install.ps1.
    public const int RegistrationNeeded = 3;
    public const int NotConfigured = 4;
    public const int ConnectionFailed = 5;
    /// <summary>login without a project when the organization has several (unattended: the installer asks).</summary>
    public const int ProjectNeeded = 4;
    public const int ClaudeRunning = 7;

    public static void PrintUsage() => Console.WriteLine("""
        ADO Lens for Azure DevOps - work items, boards, wiki, Git and TFVC for AI assistants over MCP

        usage: adolens [command] [options]

          mcp (default)   run the MCP server over stdio (this is what Claude starts)
          login           add or update a connection: organization, default project and PAT
                            --org <name|url>  --project <name>  --team <name>  --connection <name>
                            --pat-stdin (read the PAT from standard input)  --default (make it the default connection)
          logout          remove the stored PAT of a connection        --connection <name> | --all
                            --remove: also the connection itself and its metadata cache
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
          trace <id>       was it done as asked, and where is it (DEV / UAT / PROD)   --detail summary|full
          changes <523 | C523 | "PR 482" | sha --repo <r>>   files, X++ methods; --file <name> for a diff
          pr [<id>]        PRs waiting for my review and mine (--list active: all); with id: reviewers, checks, threads
          pipeline [<build id>]   recent builds (--definition <name> --branch <b> --top 10); with id: why it failed
          wiki [<page path>]  --search <text>  --depth 2  --detail full  --wiki <name>
          wiki-write <page path>  --file <page.md> (or standard input)  --mode create|append|prepend|replace
                            --version <v> (for replace, from 'wiki <path>')  --comment <text>
          search <text>    work items, wiki and code in one search   --scope work;wiki;code  --top 10
          file <path>      a file ($/… TFVC, Git path with --repo, or a file name)  --version <v>  --part <X++ method>
                            --history (who changed it, when, for which work items)  --detail full
          review <change>  checks before testing: work item linked, labels in every language, X++ messages from labels,
                            no TODO;  --item <id>: every change of a work item and its tasks
          attachments <id> files of a work item; --file <name|number> reads one (Word, Excel, text); --add <path> attaches
          label "<text>"   a D365 message as seen (values for %1 are fine) or @File:Id: label, languages, where used
          health           backlog hygiene of the team    --team <t>
          release-notes [current|previous|<sprint>]   or --since 14d [--until yyyy-MM-dd]   markdown for the wiki
          undo             revert the last change made by ADO Lens (preview, then --confirm <token>)   --list
          recipe           list | show <name> | save <name> --file <r.json> | delete <name>
                            run <name> [--param k=v]… [--confirm <token> | --yes (no preview: Task Scheduler)]
          api <path>      raw JSON of a GET request (development)
          stats           MCP calls of the last days: answer sizes, times, empty answers   --days 7 --top 10
                            --live: show every call as Claude makes it (tool, tokens, ms), until Ctrl+C
          setup           settings menu: mode, environments (change or add, for you or the team wiki), add or remove a
                            connection, status;  --wizard: the first-run questions in a row (used by the installer)
          upgrade         is there a newer ADO Lens   --install (download, check SHA-256, run its installer)  --pre
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

        // --pat-env NAME: the installer passes the PAT in an environment variable (never on a command line) and nobody
        // can answer questions, so nothing is asked.
        var patEnv = cl.Opt("pat-env");
        var unattended = patEnv is not null;
        var pat = patEnv is not null ? Environment.GetEnvironmentVariable(patEnv)
            : cl.Flag("pat-stdin") || Console.IsInputRedirected
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

        var project = NullIfEmpty(cl.Opt("project")) ?? urlProject ?? conn.Project;
        if (project is null && projects.Count == 1) project = projects[0];
        else if (project is null && projects.Count > 1 && unattended)
        {
            Console.Error.WriteLine($"which project? {string.Join(", ", projects.Take(30))}{(projects.Count > 30 ? ", …" : "")}");
            Console.Error.WriteLine("nothing was saved");
            return ProjectNeeded;
        }
        else if (project is null && projects.Count > 0 && !Console.IsInputRedirected)
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
        if (cl.Flag("all"))
        {
            // Uninstall: every stored PAT of the connections in the settings.
            foreach (var conn in settings.Connections.Keys)
                Console.WriteLine(CredentialStore.Delete(conn) ? $"removed {CredentialStore.TargetFor(conn)}" : $"no stored PAT for '{conn}'");
            return 0;
        }
        var name = cl.Opt("connection") ?? settings.DefaultConnection;
        if (string.IsNullOrWhiteSpace(name)) { Console.WriteLine("no connection configured - nothing to remove"); return 0; }
        Console.WriteLine(CredentialStore.Delete(name)
            ? $"removed {CredentialStore.TargetFor(name)} from Windows Credential Manager"
            : $"no stored PAT for connection '{name}'");
        if (cl.Flag("remove")) RemoveConnection(name, patRemoved: true);
        return 0;
    }

    /// <summary>The whole connection: PAT, settings entry and its metadata cache.</summary>
    void RemoveConnection(string name, bool patRemoved = false)
    {
        if (!patRemoved && CredentialStore.Delete(name)) Console.WriteLine($"removed {CredentialStore.TargetFor(name)} from Windows Credential Manager");
        var settings = Settings.Load(SettingsPath);
        var key = settings.Connections.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key is null) { Console.WriteLine($"no connection '{name}' in the settings"); return; }
        settings.Connections.Remove(key);
        if (string.Equals(settings.DefaultConnection, key, StringComparison.OrdinalIgnoreCase))
            settings.DefaultConnection = settings.Connections.Count == 1 ? settings.Connections.Keys.First() : null;
        settings.Save(SettingsPath);
        Console.WriteLine($"removed connection '{key}' from {SettingsPath}" +
                          (settings.DefaultConnection is { } d ? $"; default connection: {d}" : settings.Connections.Count > 1 ? "; set a default with 'adolens config --default-connection <name>'" : ""));
        var meta = Path.Combine(Settings.DataDir, "meta");
        foreach (var f in Directory.Exists(meta) ? Directory.GetFiles(meta, key + ".db*") : [])
        {
            try { File.Delete(f); }
            catch (IOException) { Console.WriteLine($"note: {f} is in use (an open Claude session) - delete it later"); }
        }
    }

    public int Connections()
    {
        var settings = Settings.Load(SettingsPath);
        if (settings.Connections.Count == 0) { Console.WriteLine("no connections - add one with 'adolens login'"); return 0; }
        foreach (var (name, c) in settings.Connections.OrderBy(kv => kv.Key))
        {
            var pat = CredentialStore.Read(name) is not null ? "PAT stored" : "no stored PAT";
            Console.WriteLine($"{(name.Equals(settings.DefaultConnection, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {name,-16} " +
                              $"https://dev.azure.com/{c.Organization}  project {c.Project ?? "-"}  team {c.Team ?? "-"}  ({pat})" +
                              (c.Environments.Count > 0 ? $"\n  {"",-16} environments: {string.Join(", ", c.Environments.Select(e => $"{e.Key} = {e.Value}"))}" : ""));
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
                while (cl.Opt("env") is { } e)
                {
                    var eq = e.IndexOf('=');
                    if (eq <= 0 || eq == e.Length - 1) throw new ArgumentException($"--env takes branch=label, e.g. staging=UAT, not '{e}'");
                    conn.Environments[e[..eq].Trim()] = e[(eq + 1)..].Trim();
                    changed = true;
                }
                while (cl.Opt("remove-env") is { } r) { conn.Environments.Remove(r.Trim()); changed = true; }
            }
            else if (cl.Peek("project") is not null || cl.Peek("team") is not null || cl.Peek("env") is not null || cl.Peek("remove-env") is not null)
                throw new ArgumentException("--project / --team / --env need --connection <name>");
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
            Tags = cl.Opt("tags"), State = cl.Opt("state"), Fields = FieldOptions(), Template = cl.Opt("template"),
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
        var wiql = cl.Peek("wiql");
        if (ids is null && wiql is null && r.IsEmpty && cl.Peek("comment") is null)
        {
            // "update" alone is easily taken for updating the program.
            Console.Error.WriteLine("update changes work items: adolens update <ids> --state <s> | --field \"Name=Value\" | --comment <text>…");
            Console.Error.WriteLine("to update ADO Lens itself: adolens upgrade --install");
            return 2;
        }
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

    public async Task<int> UpdateCommandAsync()
    {
        var (text, failed) = await Updater.RunAsync(cl.Flag("install"), cl.Flag("pre"));
        (failed ? Console.Error : Console.Out).WriteLine(text);
        return failed ? 1 : 0;
    }

    /// <summary>
    /// The interactive setup after the connection: who uses it (mode) and where code goes (environments), saved for this
    /// user or for the whole team on the wiki. Every question has a default, Enter accepts it.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetACP();

    /// <summary>
    /// For the installer wizard, on the default (or given) connection and its project:
    /// --detect [--out file]: "existing&lt;TAB&gt;&lt;flow&gt;&lt;TAB&gt;&lt;source&gt;" when a map is in use, else "row&lt;TAB&gt;key&lt;TAB&gt;guess" per
    /// stage or branch found (written in the system ANSI code page, which the wizard reads);
    /// --env "key=name; …" [--scope local|team]: saves the map without questions.
    /// </summary>
    async Task<int> SetupUnattendedAsync()
    {
        var lens = NewLens();
        var session = lens.For(cl.Opt("connection"));
        var project = session.Project(null);
        if (cl.Flag("detect"))
        {
            var lines = new List<string>();
            var (effective, source) = await EnvironmentFlow.EffectiveAsync(session, project, default);
            if (effective.Count > 0) lines.Add($"existing\t{string.Join(" -> ", effective.Values)}\t{source}");
            else lines.AddRange((await EnvironmentFlow.DetectAsync(session, project, default)).Select(d => $"row\t{d.Key}\t{d.Guess}"));
            if (cl.Opt("out") is { } file)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                File.WriteAllLines(file, lines, System.Text.Encoding.GetEncoding((int)GetACP()));
            }
            else lines.ForEach(Console.WriteLine);
            return 0;
        }
        var env = cl.Opt("env")!;
        var scope = cl.Opt("scope") ?? "local";
        Print(await Queries.SettingsAsync(lens, session.Config.Connection, project, env, scope));
        return 0;
    }

    public async Task<int> SetupAsync()
    {
        if (cl.Flag("detect") || cl.Peek("env") is not null) return await SetupUnattendedAsync();
        var path = SettingsPath;
        var settings = Settings.Load(path);
        if (settings.Connections.Count == 0)
        {
            Console.WriteLine(Ui.T("No Azure DevOps connection yet - let's add one.", "Nie ma jeszcze połączenia z Azure DevOps - dodajmy je."));
            var login = await LoginAsync();
            if (login != 0) return login;
            settings = Settings.Load(path);
        }
        var interactive = !Console.IsInputRedirected;
        // Run again later (Start menu "ADO Lens - settings"): a menu to change one thing at a time.
        if (interactive && !cl.Flag("wizard") && cl.Peek("mode") is null) return await SetupMenuAsync();

        // 1. Who uses it.
        if (cl.Opt("mode") is { } m) settings.Mode = Settings.NormalizeMode(m);
        else if (interactive)
        {
            Console.WriteLine();
            Console.WriteLine(Ui.T("Who will use ADO Lens here?", "Kto będzie tu używał ADO Lens?"));
            Console.WriteLine(Ui.T("  1. Product Owner - work items, sprints, board, wiki, checking delivered work",
                                   "  1. Product Owner - work itemy, sprinty, tablica, wiki, sprawdzanie wykonanej pracy"));
            Console.WriteLine(Ui.T("  2. Developer - work items, wiki, code, pull requests, pipelines",
                                   "  2. Programista - work itemy, wiki, kod, pull requesty, pipeline'y"));
            Console.WriteLine(Ui.T("  3. Everything", "  3. Wszystko"));
            var current = settings.Mode switch { "dev" => "2", "full" => "3", _ => "1" };
            var answer = Prompt(Ui.T($"Choice [{current}]: ", $"Wybór [{current}]: "));
            settings.Mode = (string.IsNullOrWhiteSpace(answer) ? current : answer.Trim()) switch { "2" => "dev", "3" => "full", _ => "po" };
        }
        settings.Save(path);

        // 2. Where code goes, per connection.
        var lens = NewLens();
        foreach (var name in settings.Connections.Keys.ToList())
        {
            Session session;
            try { session = lens.For(name); }
            catch (InvalidOperationException ex) { Console.WriteLine($"{name}: {ex.Message}"); continue; }
            if (session.Config.Project is not { } project) continue;
            Console.WriteLine();
            await EnvironmentsAsync(name, session, project, interactive, change: false);
        }
        return 0;
    }

    static bool Yes(string? answer) => (answer ?? "").Trim().ToLowerInvariant() is "y" or "yes" or "t" or "tak";

    /// <summary>
    /// Asks for the environment names of a connection's project. With <paramref name="change"/> an existing map is
    /// edited (its names offered, new stages and branches added at the end); otherwise an existing map is only shown.
    /// </summary>
    async Task EnvironmentsAsync(string name, Session session, string project, bool interactive, bool change)
    {
        var (effective, source) = await EnvironmentFlow.EffectiveAsync(session, project, default);
        if (effective.Count > 0)
            Console.WriteLine(Ui.T($"{name} / {project}: code goes {string.Join(" -> ", effective.Values)} ({source}).",
                                   $"{name} / {project}: kod trafia kolejno na {string.Join(" -> ", effective.Values)} ({source})."));
        if (effective.Count > 0 && !change) return;
        var detected = await EnvironmentFlow.DetectAsync(session, project, default);
        // Rows: the current map in its order, then what was found and is not in it yet.
        var rows = effective.Select(e => (e.Key, Guess: (string?)e.Value))
            .Concat(detected.Where(d => !effective.ContainsKey(d.Key))).ToList();
        if (rows.Count == 0 || !interactive)
        {
            Console.WriteLine(Ui.T($"{name} / {project}: no release stages or long-lived branches found - Claude will ask when needed.",
                                   $"{name} / {project}: nie znaleziono etapów release'ów ani gałęzi - Claude zapyta, gdy będzie trzeba."));
            return;
        }
        Console.WriteLine(Ui.T($"{name} / {project}: where does code go before it reaches production?",
                               $"{name} / {project}: przez jakie środowiska przechodzi kod, zanim trafi na produkcję?"));
        Console.WriteLine(Ui.T("Name each as people say it (e.g. UAT (sbx03)); Enter keeps [the name], '-' leaves it out.",
                               "Nadaj każdemu nazwę, której używacie (np. UAT (sbx03)); Enter zostawia [nazwę], '-' pomija."));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, guess) in rows)
        {
            var label = Prompt($"  {key}{(guess is null ? "" : $" [{guess}]")}: ");
            if (label == "-") continue;
            var value = string.IsNullOrWhiteSpace(label) ? guess : label.Trim();
            if (value is not null) map[key] = value;
        }
        // Something the search did not find (a release stage that has not run yet, a branch in another project).
        while (Prompt(Ui.T("  another one (branch or Release/Stage=name, Enter = done): ", "  kolejne (gałąź albo Release/Etap=nazwa, Enter = koniec): ")) is { Length: > 0 } extra)
        {
            var eq = extra.IndexOf('=');
            if (eq > 0) map[extra[..eq].Trim()] = extra[(eq + 1)..].Trim();
            else Console.WriteLine(Ui.T("    write it as key=name, e.g. release=PROD", "    zapisz jako klucz=nazwa, np. release=PROD"));
        }
        if (map.Count == 0) return;
        var teamNow = source.StartsWith("team", StringComparison.Ordinal);
        var forTeam = Yes(Prompt(Ui.T($"Save for the whole team on the project's wiki page \"ADO Lens\"? [{(teamNow ? "Y/n" : "y/N")}]: ",
                                      $"Zapisać dla całego zespołu na stronie wiki projektu \"ADO Lens\"? [{(teamNow ? "T/n" : "t/N")}]: ")) is { Length: > 0 } a ? a : teamNow ? "y" : "n");
        if (forTeam)
        {
            try
            {
                var url = await session.Team.SaveAsync(project, map, default);
                Console.WriteLine(Ui.T($"saved on the wiki: {url}", $"zapisane na wiki: {url}"));
                // A local map would hide the team's one: drop it.
                if (session.Config.Environments.Count > 0)
                {
                    var s = Settings.Load(SettingsPath);
                    s.Connections[name].Environments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    s.Save(SettingsPath);
                    Console.WriteLine(Ui.T("your own map was removed, the team's is used now", "twoja własna mapa została usunięta, teraz obowiązuje zespołowa"));
                }
                return;
            }
            catch (AdoApiException ex)
            {
                Console.WriteLine(Ui.T($"could not write the wiki page ({ex.Message}) - saving for you only",
                                       $"nie udało się zapisać strony wiki ({ex.Message}) - zapisuję tylko dla ciebie"));
            }
        }
        var settings = Settings.Load(SettingsPath);
        settings.Connections[name].Environments = map;
        settings.Save(SettingsPath);
        Console.WriteLine(Ui.T("saved in your settings", "zapisane w twoich ustawieniach"));
    }

    /// <summary>The settings menu: one thing at a time, until Enter.</summary>
    async Task<int> SetupMenuAsync()
    {
        while (true)
        {
            var settings = Settings.Load(SettingsPath);
            Console.WriteLine();
            Console.WriteLine(Ui.T($"ADO Lens {version} - settings", $"ADO Lens {version} - ustawienia"));
            Console.WriteLine(Ui.T($"  mode: {settings.Mode}; connections: {string.Join(", ", settings.Connections.Keys.Select(k => k == settings.DefaultConnection ? k + " (default)" : k))}",
                                   $"  tryb: {settings.Mode}; połączenia: {string.Join(", ", settings.Connections.Keys.Select(k => k == settings.DefaultConnection ? k + " (domyślne)" : k))}"));
            Console.WriteLine(Ui.T("  1. Who uses it (mode)", "  1. Kto używa (tryb)"));
            Console.WriteLine(Ui.T("  2. Environments (where code goes) - change or add", "  2. Środowiska (gdzie trafia kod) - zmiana lub dodanie"));
            Console.WriteLine(Ui.T("  3. Add a connection (another organization or company)", "  3. Dodaj połączenie (inna organizacja lub firma)"));
            Console.WriteLine(Ui.T("  4. Remove a connection", "  4. Usuń połączenie"));
            Console.WriteLine(Ui.T("  5. Status (is everything working)", "  5. Status (czy wszystko działa)"));
            var choice = Prompt(Ui.T("Choice (Enter = done): ", "Wybór (Enter = koniec): "));
            switch (choice?.Trim())
            {
                case null or "": return 0;
                case "1":
                    var current = settings.Mode switch { "dev" => "2", "full" => "3", _ => "1" };
                    Console.WriteLine(Ui.T("  1. Product Owner  2. Developer  3. Everything", "  1. Product Owner  2. Programista  3. Wszystko"));
                    var m = Prompt(Ui.T($"  mode [{current}]: ", $"  tryb [{current}]: "));
                    settings.Mode = (string.IsNullOrWhiteSpace(m) ? current : m.Trim()) switch { "2" => "dev", "3" => "full", _ => "po" };
                    settings.Save(SettingsPath);
                    Console.WriteLine(Ui.T($"  mode: {settings.Mode} - new Claude sessions use it", $"  tryb: {settings.Mode} - obowiązuje w nowych sesjach Claude'a"));
                    break;
                case "2":
                    var name = PickConnection(settings);
                    if (name is null) break;
                    Session session;
                    try { session = NewLens().For(name); }
                    catch (InvalidOperationException ex) { Console.WriteLine("  " + ex.Message); break; }
                    if (session.Config.Project is not { } project) { Console.WriteLine(Ui.T("  this connection has no default project", "  to połączenie nie ma domyślnego projektu")); break; }
                    await EnvironmentsAsync(name, session, project, interactive: true, change: true);
                    break;
                case "3":
                    await LoginAsync();
                    break;
                case "4":
                    var remove = PickConnection(settings);
                    if (remove is not null && Yes(Prompt(Ui.T($"  remove '{remove}' with its PAT? [y/N]: ", $"  usunąć '{remove}' razem z PAT-em? [t/N]: "))))
                        RemoveConnection(remove);
                    break;
                case "5":
                    await StatusAsync();
                    break;
            }
        }
    }

    string? PickConnection(Settings settings)
    {
        var names = settings.Connections.Keys.ToList();
        if (names.Count == 0) { Console.WriteLine(Ui.T("  no connections yet - choose 3", "  brak połączeń - wybierz 3")); return null; }
        if (names.Count == 1) return names[0];
        for (var i = 0; i < names.Count; i++) Console.WriteLine($"    {i + 1}. {names[i]}");
        var a = Prompt(Ui.T("  connection [1]: ", "  połączenie [1]: "));
        return int.TryParse(string.IsNullOrWhiteSpace(a) ? "1" : a, out var n) && n >= 1 && n <= names.Count ? names[n - 1] : null;
    }

    public async Task<int> TraceAsync()
    {
        if (Int(cl.Positional.FirstOrDefault()) is not { } id) { Console.Error.WriteLine("usage: adolens trace <work item id> [--detail summary|full]"); return 2; }
        Print(await TraceQueries.TraceAsync(NewLens(), cl.Opt("connection"), id, cl.Opt("detail")));
        return 0;
    }

    public async Task<int> ChangesAsync()
    {
        if (cl.Positional.Count == 0) { Console.Error.WriteLine("usage: adolens changes <523 | C523 | \"PR 482\" | sha --repo <name>> [--file <name>]"); return 2; }
        Print(await TraceQueries.ChangesAsync(NewLens(), cl.Opt("connection"), string.Join(' ', cl.Positional), cl.Opt("repo"), cl.Opt("file"), cl.Opt("detail")));
        return 0;
    }

    public async Task<int> PrAsync()
    {
        var lens = NewLens();
        Print(Int(cl.Positional.FirstOrDefault()) is { } id
            ? await PullRequestQueries.ShowAsync(lens, cl.Opt("connection"), id, cl.Opt("repo"), cl.Opt("detail"))
            : await PullRequestQueries.ListAsync(lens, cl.Opt("connection"), cl.Opt("project"), cl.Opt("list")));
        return 0;
    }

    public async Task<int> PipelineAsync()
    {
        var lens = NewLens();
        Print(Int(cl.Positional.FirstOrDefault()) is { } build
            ? await PipelineQueries.BuildAsync(lens, cl.Opt("connection"), cl.Opt("project"), build, cl.Opt("detail"))
            : await PipelineQueries.ListAsync(lens, cl.Opt("connection"), cl.Opt("project"), cl.Opt("definition"), cl.Opt("branch"), Int(cl.Opt("top")) ?? 10));
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

    public async Task<int> SearchAsync()
    {
        if (cl.Positional.Count == 0) { Console.Error.WriteLine("usage: adolens search <text> [--scope work;wiki;code] [--top 10]"); return 2; }
        Print(await Knowledge.SearchAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), string.Join(' ', cl.Positional), cl.Opt("scope"), Int(cl.Opt("top")) ?? 10));
        return 0;
    }

    public async Task<int> AttachmentsAsync()
    {
        if (Int(cl.Positional.FirstOrDefault()) is not { } id) { Console.Error.WriteLine("usage: adolens attachments <work item id> [--file <name or number>] [--add <path> --comment <text>]"); return 2; }
        var lens = NewLens();
        Print(cl.Opt("add") is { } add ? await Attachments.AddAsync(lens, cl.Opt("connection"), id, add, cl.Opt("comment"))
            : await Attachments.ReadAsync(lens, cl.Opt("connection"), id, cl.Opt("file"), cl.Opt("detail")));
        return 0;
    }

    public async Task<int> ReviewAsync()
    {
        var arg = cl.Positional.Count > 0 ? string.Join(' ', cl.Positional) : null;
        var item = Int(cl.Opt("item"));
        if (arg is null && item is null) { Console.Error.WriteLine("usage: adolens review <523 | \"PR 482\" | sha --repo <r>>  or  adolens review --item <work item id>"); return 2; }
        Print(await Review.ReviewAsync(NewLens(), cl.Opt("connection"), item is null ? arg : null, item, cl.Opt("repo")));
        return 0;
    }

    public async Task<int> LabelAsync()
    {
        if (cl.Positional.Count == 0) { Console.Error.WriteLine("usage: adolens label \"<message as seen on screen>\" | @File:Id  [--branch Main]"); return 2; }
        Print(await Labels.FindAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), string.Join(' ', cl.Positional), cl.Opt("branch")));
        return 0;
    }

    public async Task<int> FileAsync()
    {
        if (cl.Positional.Count == 0) { Console.Error.WriteLine("usage: adolens file <path or file name> [--repo <r>] [--version <v>] [--part <method>] [--history]"); return 2; }
        Print(await Knowledge.FileAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), string.Join(' ', cl.Positional), cl.Opt("repo"), cl.Opt("version"),
            cl.Opt("part"), cl.Flag("history"), Int(cl.Opt("top")) ?? 15, cl.Opt("detail")));
        return 0;
    }

    public async Task<int> TestsAsync()
    {
        var ids = cl.Positional.Count > 0 && cl.Positional.All(p => WorkQueries.ParseIds(p).Count > 0) ? string.Join(';', cl.Positional) : null;
        Print(await TestCases.StatusAsync(NewLens(), cl.Opt("connection"), ids, ids is null ? cl.Positional.FirstOrDefault() : null, cl.Opt("team"), cl.Opt("detail")));
        return 0;
    }

    /// <summary>test-create &lt;id&gt; --file cases.md (or the markdown from standard input).</summary>
    public async Task<int> TestCreateAsync()
    {
        if (Int(cl.Positional.FirstOrDefault()) is not { } id) { Console.Error.WriteLine("usage: adolens test-create <work item id> --file <cases.md> [--plan <p> --suite <s>]"); return 2; }
        var cases = cl.Opt("file") is { } file ? await File.ReadAllTextAsync(file) : await Console.In.ReadToEndAsync();
        Print(await TestCases.CreateAsync(NewLens(), cl.Opt("connection"), id, cases, cl.Opt("plan"), cl.Opt("suite")));
        return 0;
    }

    public async Task<int> MetricsAsync()
    {
        Print(await BoardQueries.MetricsAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("team"), Int(cl.Opt("sprints")) ?? 6));
        return 0;
    }

    public async Task<int> HealthAsync()
    {
        Print(await BoardQueries.HealthAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("team")));
        return 0;
    }

    public async Task<int> ReleaseNotesAsync()
    {
        Print(await BoardQueries.ReleaseNotesAsync(NewLens(), cl.Opt("connection"), cl.Opt("project"), cl.Opt("team"),
            cl.Positional.FirstOrDefault(), cl.Opt("since"), cl.Opt("until")));
        return 0;
    }

    public async Task<int> UndoAsync()
    {
        var lens = NewLens();
        Print(cl.Flag("list") ? Audit.List(lens, cl.Opt("connection"), 20) : await Audit.UndoAsync(lens, cl.Opt("connection"), cl.Opt("confirm")));
        return 0;
    }

    /// <summary>recipe list | show &lt;name&gt; | save &lt;name&gt; --file r.json | run &lt;name&gt; --param k=v [--confirm t | --yes] | delete &lt;name&gt;.</summary>
    public async Task<int> RecipeAsync()
    {
        var lens = NewLens();
        var action = cl.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var name = cl.Positional.ElementAtOrDefault(1);
        if (action != "list" && name is null) { Console.Error.WriteLine("usage: adolens recipe list | show <name> | save <name> --file <recipe.json> | run <name> [--param k=v]… [--confirm <token> | --yes] | delete <name>"); return 2; }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (cl.Opt("param") is { } p)
        {
            var eq = p.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"--param {p}: use name=value");
            values[p[..eq].Trim()] = p[(eq + 1)..].Trim();
        }
        Print(action switch
        {
            "list" => Recipes.List(lens),
            "show" => Recipes.Show(lens, name!),
            "save" => Recipes.Save(lens, name!, cl.Opt("file") is { } f ? await File.ReadAllTextAsync(f) : await Console.In.ReadToEndAsync()),
            "delete" => Recipes.Delete(lens, name!),
            "run" => await Recipes.RunAsync(lens, cl.Opt("connection"), name!, values, cl.Opt("confirm"), cl.Flag("yes")),
            _ => throw new ArgumentException("recipe action is list, show, save, run or delete"),
        });
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
