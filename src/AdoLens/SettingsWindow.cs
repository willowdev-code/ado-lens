using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AdoLens;

/// <summary>
/// The settings after installation in a window (Start menu "ADO Lens - settings", <c>adolens setup --window</c>): the
/// same things as the menu of <c>adolens setup</c>, which "Open in a terminal" starts. Settings are changed through the
/// same classes as the menu; signing in and the status run adolens.exe itself, as the installer does.
/// </summary>
public sealed class SettingsWindow : Form
{
    readonly string path;
    readonly string version;
    Settings settings;

    readonly ComboBox mode = Combo(), loading = Combo(), language = Combo();
    readonly TextBox downloads = new() { Dock = DockStyle.Fill };

    readonly ListView connections = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
    readonly TextBox project = Field(), team = Field(), codeProject = Field(), labelLanguages = Field();

    readonly ComboBox envConnection = Combo();
    readonly DataGridView envGrid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToResizeRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = SystemColors.Window,
    };
    readonly Label envInfo = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly TextBox envFilter = new() { Width = 160 };
    readonly ComboBox envRepo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly CheckBox envNamed = new() { AutoSize = true };

    readonly TextBox status = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };

    static readonly string[] Modes = ["po", "dev", "full"];
    static readonly string[] Loadings = ["auto", "demand", "all"];
    static readonly string?[] Languages = ["pl", "en", null];

    public SettingsWindow(string settingsPath, string version)
    {
        path = settingsPath;
        this.version = version;
        settings = Settings.Load(path);
        Text = Ui.T($"ADO Lens {version} - settings", $"ADO Lens {version} - ustawienia");
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch (Exception) { }
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(820, 540);
        MinimumSize = new Size(700, 460);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(GeneralPage());
        tabs.TabPages.Add(ConnectionsPage());
        tabs.TabPages.Add(EnvironmentsPage());
        tabs.TabPages.Add(StatusPage());
        tabs.SelectedIndexChanged += async (_, _) => { if (tabs.SelectedIndex == 2 && envGrid.Rows.Count <= 1) await LoadEnvironmentsAsync(detect: false); };

        var terminal = new Button { Text = Ui.T("Open in a terminal", "Otwórz w terminalu"), AutoSize = true };
        terminal.Click += (_, _) => Start("setup", shell: true);
        var close = new Button { Text = Ui.T("Close", "Zamknij"), AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        bottom.Controls.Add(close);
        bottom.Controls.Add(terminal);

        Controls.Add(tabs);
        Controls.Add(bottom);
        Shown += (_, _) => { WindowState = FormWindowState.Normal; Activate(); };
        LoadGeneral();
        LoadConnections();
    }

    // ---- entry ------------------------------------------------------------------------------------------------------

    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] list, uint count);

    /// <summary>Shows the window on its own UI thread; a console opened only for this program (Start menu) is closed.</summary>
    public static int Run(string settingsPath, string version)
    {
        if (GetConsoleProcessList(new uint[2], 2) == 1) FreeConsole();
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SettingsWindow(settingsPath, version));
            }
            catch (Exception ex) { failed = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failed is not null) { Console.Error.WriteLine(failed.Message); return 1; }
        return 0;
    }

    // ---- general ----------------------------------------------------------------------------------------------------

    TabPage GeneralPage()
    {
        mode.Items.AddRange([Ui.T("Product Owner - work items, sprints, board, wiki, checking delivered work", "Product Owner - work itemy, sprinty, tablica, wiki, sprawdzanie wykonanej pracy"),
                             Ui.T("Developer - work items, wiki, code, pull requests, pipelines", "Programista - work itemy, wiki, kod, pull requesty, pipeline'y"),
                             Ui.T("Everything", "Wszystko")]);
        loading.Items.AddRange([Ui.T("auto - on demand in Claude Code, all at start in Claude Desktop", "auto - na żądanie w Claude Code, wszystkie od startu w Claude Desktop"),
                                Ui.T("demand - core at start, the rest when needed (fewest tokens)", "demand - podstawowe na start, reszta, gdy potrzebna (najmniej tokenów)"),
                                Ui.T("all - every tool of the mode at start", "all - wszystkie narzędzia trybu od startu")]);
        language.Items.AddRange(["polski", "English", Ui.T("as Windows", "jak Windows")]);
        var browse = new Button { Text = "...", Width = 32 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = Downloads.BaseDir(settings) };
            if (dialog.ShowDialog(this) == DialogResult.OK) downloads.Text = dialog.SelectedPath;
        };
        var folder = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folder.Controls.Add(downloads, 0, 0);
        folder.Controls.Add(browse, 1, 0);

        var save = new Button { Text = Ui.T("Save", "Zapisz"), AutoSize = true };
        save.Click += (_, _) => Guard(SaveGeneral);
        var grid = Grid(
            (Ui.T("Who uses it", "Kto używa"), mode),
            (Ui.T("Tools at start", "Narzędzia na start"), loading),
            (Ui.T("Language", "Język"), language),
            (Ui.T("Download folder (empty = Downloads\\ADO Lens)", "Folder pobierania (puste = Pobrane\\ADO Lens)"), folder));
        grid.Controls.Add(Note(Ui.T("Changes apply to new conversations with Claude.", "Zmiany obowiązują w nowych rozmowach z Claude.")), 1, grid.RowCount);
        grid.Controls.Add(save, 1, grid.RowCount + 1);
        return Page(Ui.T("General", "Ogólne"), grid);
    }

    void LoadGeneral()
    {
        mode.SelectedIndex = Math.Max(0, Array.IndexOf(Modes, settings.Mode));
        loading.SelectedIndex = Math.Max(0, Array.IndexOf(Loadings, settings.ToolLoading));
        language.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));
        downloads.Text = settings.DownloadDir ?? "";
    }

    void SaveGeneral()
    {
        settings = Settings.Load(path);
        var newMode = Modes[mode.SelectedIndex];
        var newLanguage = Languages[language.SelectedIndex];
        var skillsChange = newMode != settings.Mode || newLanguage != settings.Language;
        settings.Mode = newMode;
        settings.ToolLoading = Loadings[loading.SelectedIndex];
        settings.Language = newLanguage;
        settings.DownloadDir = string.IsNullOrWhiteSpace(downloads.Text) ? null : downloads.Text.Trim().Trim('"');
        settings.Save(path);
        var said = Ui.T("Saved - new conversations with Claude use it.", "Zapisane - obowiązuje w nowych rozmowach z Claude.");
        if (skillsChange)
        {
            var polish = newLanguage == "pl" || newLanguage is null && Ui.Polish;
            said += "\n\n" + Skills.Sync(newMode, polish: polish);
            if (Skills.DesktopZips(polish) is { Length: > 0 } zips) said += "\n" + zips;
        }
        MessageBox.Show(this, said, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- connections ------------------------------------------------------------------------------------------------

    TabPage ConnectionsPage()
    {
        connections.Columns.Add(Ui.T("Connection", "Połączenie"), 110);
        connections.Columns.Add(Ui.T("Organization", "Organizacja"), 120);
        connections.Columns.Add(Ui.T("Project", "Projekt"), 120);
        connections.Columns.Add(Ui.T("Code in", "Kod w"), 100);
        connections.Columns.Add(Ui.T("Team", "Zespół"), 100);
        connections.Columns.Add("PAT", 90);
        connections.SelectedIndexChanged += (_, _) => ShowConnection();

        var add = Button(Ui.T("Add a connection...", "Dodaj połączenie..."), () => { using var d = new LoginDialog(path, version); if (d.ShowDialog(this) == DialogResult.OK) LoadConnections(); });
        var makeDefault = Button(Ui.T("Make default", "Ustaw jako domyślne"), () =>
        {
            if (Selected() is not { } name) return;
            settings = Settings.Load(path);
            settings.DefaultConnection = name;
            settings.Save(path);
            LoadConnections();
        });
        var remove = Button(Ui.T("Remove...", "Usuń..."), () =>
        {
            if (Selected() is not { } name) return;
            if (MessageBox.Show(this, Ui.T($"Remove the connection '{name}' with its PAT?", $"Usunąć połączenie '{name}' razem z PAT-em?"), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RemoveConnection(name);
            LoadConnections();
        });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([add, makeDefault, remove]);

        var save = Button(Ui.T("Save changes", "Zapisz zmiany"), SaveConnection);
        var details = Grid(
            (Ui.T("Default project (work items)", "Projekt domyślny (work itemy)"), project),
            (Ui.T("Team", "Zespół"), team),
            (Ui.T("Project with the code, PRs and pipelines (empty = the same)", "Projekt z kodem, PR-ami i pipeline'ami (puste = ten sam)"), codeProject),
            (Ui.T("Required label languages (e.g. en-US, pl; empty = all)", "Wymagane języki etykiet (np. en-US, pl; puste = wszystkie)"), labelLanguages));
        details.Controls.Add(save, 1, details.RowCount);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.Controls.Add(connections, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(details, 0, 2);
        return Page(Ui.T("Connections", "Połączenia"), layout);
    }

    void LoadConnections()
    {
        settings = Settings.Load(path);
        connections.Items.Clear();
        foreach (var (name, c) in settings.Connections.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem(name + (name.Equals(settings.DefaultConnection, StringComparison.OrdinalIgnoreCase) ? Ui.T(" (default)", " (domyślne)") : "")) { Tag = name };
            item.SubItems.AddRange([c.Organization, c.Project ?? "-", c.CodeProject ?? "", c.Team ?? "", CredentialStore.Read(name) is null ? Ui.T("missing", "brak") : Ui.T("stored", "zapisany")]);
            connections.Items.Add(item);
        }
        if (connections.Items.Count > 0) connections.Items[0].Selected = true;
        envConnection.Items.Clear();
        envConnection.Items.AddRange(settings.Connections.Keys.Order(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        if (envConnection.Items.Count > 0)
            envConnection.SelectedItem = settings.DefaultConnection is { } d && envConnection.Items.Contains(d) ? d : envConnection.Items[0];
        ShowConnection();
    }

    string? Selected() => connections.SelectedItems.Count > 0 ? connections.SelectedItems[0].Tag as string : null;

    void ShowConnection()
    {
        var c = Selected() is { } name && settings.Connections.TryGetValue(name, out var conn) ? conn : null;
        foreach (var box in new[] { project, team, codeProject, labelLanguages }) box.Enabled = c is not null;
        project.Text = c?.Project ?? "";
        team.Text = c?.Team ?? "";
        codeProject.Text = c?.CodeProject ?? "";
        labelLanguages.Text = c is null ? "" : string.Join(", ", c.LabelLanguages);
    }

    void SaveConnection()
    {
        if (Selected() is not { } name) return;
        settings = Settings.Load(path);
        if (!settings.Connections.TryGetValue(name, out var c)) return;
        c.Project = Empty(project.Text);
        c.Team = Empty(team.Text);
        c.CodeProject = Empty(codeProject.Text);
        c.LabelLanguages = labelLanguages.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        settings.Save(path);
        LoadConnections();
        MessageBox.Show(this, Ui.T("Saved - new conversations with Claude use it.", "Zapisane - obowiązuje w nowych rozmowach z Claude."), Text);
    }

    /// <summary>The PAT, the settings entry and the metadata cache of a connection (as 'adolens logout --remove').</summary>
    void RemoveConnection(string name)
    {
        CredentialStore.Delete(name);
        settings = Settings.Load(path);
        if (settings.Connections.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } key) return;
        settings.Connections.Remove(key);
        if (string.Equals(settings.DefaultConnection, key, StringComparison.OrdinalIgnoreCase))
            settings.DefaultConnection = settings.Connections.Count == 1 ? settings.Connections.Keys.First() : null;
        settings.Save(path);
        var meta = Path.Combine(Settings.DataDir, "meta");
        foreach (var f in Directory.Exists(meta) ? Directory.GetFiles(meta, key + ".db*") : [])
            try { File.Delete(f); } catch (IOException) { } // in use by an open Claude session
    }

    // ---- environments -----------------------------------------------------------------------------------------------

    TabPage EnvironmentsPage()
    {
        envGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "repo", HeaderText = Ui.T("Repository (empty = all)", "Repozytorium (puste = wszystkie)"), FillWeight = 25 });
        envGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", HeaderText = Ui.T("Branch or release stage (Definition/Stage)", "Gałąź albo etap release'u (Definicja/Etap)"), FillWeight = 40 });
        envGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = Ui.T("Environment, as people say it (empty = skip)", "Środowisko, jak o nim mówicie (puste = pomiń)"), FillWeight = 35 });
        envGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "hint", HeaderText = Ui.T("Looks like", "Wygląda na"), FillWeight = 12, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { ForeColor = SystemColors.GrayText } });
        // Where a row comes from: two "Main" or "Release" rows are told apart by their path, repository or release.
        envGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = Ui.T("Where from", "Skąd"), FillWeight = 45, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { ForeColor = SystemColors.GrayText } });
        envGrid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && envGrid.Rows[e.RowIndex].Cells[4].Value is string { Length: > 0 } source) e.ToolTipText = source.Replace("; ", "\n");
        };
        envConnection.SelectedIndexChanged += async (_, _) => await LoadEnvironmentsAsync(detect: false);

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        top.Controls.Add(new Label { Text = Ui.T("Connection:", "Połączenie:"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        envConnection.Width = 160;
        top.Controls.Add(envConnection);
        top.Controls.Add(Button(Ui.T("Find stages and branches", "Wykryj etapy i gałęzie"), async () => await LoadEnvironmentsAsync(detect: true)));
        top.Controls.Add(Button("↑", () => MoveRow(-1)));
        top.Controls.Add(Button("↓", () => MoveRow(+1)));
        top.Controls.Add(Button(Ui.T("Remove row", "Usuń wiersz"), () => { foreach (DataGridViewRow r in envGrid.SelectedRows) if (!r.IsNewRow) envGrid.Rows.Remove(r); }));

        // Many repositories give many rows: narrow them down, then fill the names of the rows left.
        var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        filter.Controls.Add(new Label { Text = Ui.T("Filter:", "Filtr:"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        filter.Controls.Add(envFilter);
        filter.Controls.Add(new Label { Text = Ui.T("Repository:", "Repozytorium:"), AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        filter.Controls.Add(envRepo);
        envNamed.Text = Ui.T("named only", "tylko nazwane");
        envNamed.Padding = new Padding(8, 4, 0, 0);
        filter.Controls.Add(envNamed);
        filter.Controls.Add(Button(Ui.T("Use the hints for the rows shown", "Użyj podpowiedzi dla widocznych"), () =>
        {
            foreach (DataGridViewRow row in envGrid.Rows)
                if (!row.IsNewRow && row.Visible && string.IsNullOrWhiteSpace(row.Cells[2].Value as string) && row.Cells[3].Value is string { Length: > 0 } hint)
                    row.Cells[2].Value = hint;
        }));
        envFilter.TextChanged += (_, _) => ApplyFilter();
        envRepo.SelectedIndexChanged += (_, _) => ApplyFilter();
        envNamed.CheckedChanged += (_, _) => ApplyFilter();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        bottom.Controls.Add(Button(Ui.T("Save for me", "Zapisz dla mnie"), async () => await SaveEnvironmentsAsync(team: false)));
        bottom.Controls.Add(Button(Ui.T("Save for the whole team (wiki page \"ADO Lens\")", "Zapisz dla zespołu (strona wiki \"ADO Lens\")"), async () => await SaveEnvironmentsAsync(team: true)));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(Note(Ui.T("In flow order: DEV first, production last. A repository with rows of its own uses only those (the same branch may mean something else, or nothing, in another repository).",
                                      "W kolejności przepływu: najpierw DEV, na końcu produkcja. Repozytorium z własnymi wierszami używa tylko ich (ta sama gałąź w innym repozytorium może znaczyć co innego albo nic).")), 0, 1);
        layout.Controls.Add(filter, 0, 2);
        layout.Controls.Add(envGrid, 0, 3);
        layout.Controls.Add(envInfo, 0, 4);
        layout.Controls.Add(bottom, 0, 5);
        return Page(Ui.T("Environments", "Środowiska"), layout);
    }

    void MoveRow(int by)
    {
        if (envGrid.CurrentRow is not { IsNewRow: false } row) return;
        var to = row.Index + by;
        if (to < 0 || to >= envGrid.Rows.Count - 1) return;
        envGrid.Rows.Remove(row);
        envGrid.Rows.Insert(to, row);
        envGrid.ClearSelection();
        row.Selected = true;
        envGrid.CurrentCell = row.Cells[0];
    }

    /// <summary>Each load gets a number; only the last one fills the table (two loads at once doubled every row).</summary>
    int envLoad;

    async Task LoadEnvironmentsAsync(bool detect)
    {
        if (envConnection.SelectedItem is not string name) return;
        var load = ++envLoad;
        UseWaitCursor = true;
        envInfo.Text = detect ? Ui.T("Looking for release stages and branches...", "Szukam etapów release'ów i gałęzi...") : "";
        try
        {
            var lens = new Lens(Settings.Load(path), version, Settings.DataDir, Environment.GetEnvironmentVariable, CredentialStore.Read, settingsPath: path);
            var session = lens.For(name);
            var project = session.Project(null);
            var (map, source) = await EnvironmentFlow.EffectiveAsync(session, project, default);
            // Found rows get no name yet, only the hint: with many repositories, saving would otherwise take them all.
            var detected = detect || map.Count == 0 ? await EnvironmentFlow.DetectWithSourcesAsync(session, project, default) : [];
            if (load != envLoad) return;   // a newer load is on its way
            envGrid.Rows.Clear();
            foreach (var (key, label) in map)
                AddRow(key, label, null, detected.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Source);
            var found = 0;
            foreach (var (key, guess, from) in detected)
                if (!map.ContainsKey(key)) { AddRow(key, "", guess, from); found++; }
            FillRepositories();
            var code = session.Config.CodeProject is { } cp && !cp.Equals(project, StringComparison.OrdinalIgnoreCase) ? Ui.T($" (code in {cp})", $" (kod w {cp})") : "";
            envInfo.Text = $"{project}{code}: " + (map.Count > 0 ? Ui.T($"in use: {source}", $"w użyciu: {source}") : Ui.T("no map yet", "brak mapy"))
                           + (found > 0 ? Ui.T($"; {found} found - name the ones that are environments (or filter, then use the hints)",
                                                  $"; znalezione: {found} - nazwij te, które są środowiskami (albo przefiltruj i użyj podpowiedzi)") : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or AdoApiException or HttpRequestException)
        {
            envInfo.Text = ex.Message;
            FillRepositories();
        }
        finally { UseWaitCursor = false; }
    }

    void AddRow(string key, string label, string? hint, string? source = null) =>
        envGrid.Rows.Add(EnvMap.RepoOf(key) ?? "", EnvMap.BranchOf(key), label, hint ?? "", source ?? "");

    const int AllRepositories = 0, NoRepository = 1;

    /// <summary>The repository filter: everything, the rows without a repository (TFVC branches, release stages), each repository.</summary>
    void FillRepositories()
    {
        var keep = envRepo.SelectedItem as string;
        envRepo.Items.Clear();
        envRepo.Items.Add(Ui.T("(all)", "(wszystkie)"));
        envRepo.Items.Add(Ui.T("(none: TFVC branches, release stages)", "(bez: gałęzie TFVC, etapy release'ów)"));
        envRepo.Items.AddRange(envGrid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => r.Cells[0].Value as string)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        envRepo.SelectedIndex = keep is not null && envRepo.Items.IndexOf(keep) is var i and >= 0 ? i : AllRepositories;
        ApplyFilter();
    }

    /// <summary>Hides the rows that do not match; hidden rows keep their names and are saved like the others.</summary>
    void ApplyFilter()
    {
        var text = envFilter.Text.Trim();
        envGrid.CurrentCell = null;
        foreach (DataGridViewRow row in envGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var repo = row.Cells[0].Value as string ?? "";
            var cells = string.Join(" ", row.Cells.Cast<DataGridViewCell>().Select(c => c.Value as string));
            var index = envRepo.SelectedIndex;
            var inRepo = index <= AllRepositories || (index == NoRepository ? repo.Length == 0 : repo.Equals(envRepo.SelectedItem as string, StringComparison.OrdinalIgnoreCase));
            row.Visible = (text.Length == 0 || cells.Contains(text, StringComparison.OrdinalIgnoreCase)) && inRepo
                && (!envNamed.Checked || !string.IsNullOrWhiteSpace(row.Cells[2].Value as string));
        }
    }

    /// <summary>The rows as a map in their order; a row without a name is left out.</summary>
    internal static Dictionary<string, string> MapOf(IEnumerable<(string? Repo, string? Key, string? Name)> rows)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repo, key, name) in rows)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) continue;
            var k = key.Trim();
            if (!string.IsNullOrWhiteSpace(repo) && !EnvMap.IsStage(k)) k = repo.Trim() + ":" + k;
            map[k] = name.Trim().Replace(';', ',');
        }
        return map;
    }

    async Task SaveEnvironmentsAsync(bool team)
    {
        if (envConnection.SelectedItem is not string name) return;
        envGrid.EndEdit();
        var map = MapOf(envGrid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
            .Select(r => (r.Cells[0].Value as string, r.Cells[1].Value as string, r.Cells[2].Value as string)));
        if (map.Count == 0) { MessageBox.Show(this, Ui.T("Name at least one environment.", "Nazwij co najmniej jedno środowisko."), Text); return; }
        UseWaitCursor = true;
        try
        {
            var lens = new Lens(Settings.Load(path), version, Settings.DataDir, Environment.GetEnvironmentVariable, CredentialStore.Read, settingsPath: path);
            var session = lens.For(name);
            var project = session.Project(null);
            string said;
            if (team)
            {
                var url = await session.Team.SaveAsync(project, map, default);
                // A local map would hide the team's one.
                var s = Settings.Load(path);
                if (s.Connections.TryGetValue(name, out var c) && c.Environments.Count > 0)
                {
                    c.Environments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    s.Save(path);
                }
                said = Ui.T($"Saved on the wiki for the whole team:\n{url}", $"Zapisane na wiki dla całego zespołu:\n{url}");
            }
            else
            {
                var s = Settings.Load(path);
                s.Connections[name].Environments = map;
                s.Save(path);
                said = Ui.T("Saved in your settings.", "Zapisane w twoich ustawieniach.");
            }
            MessageBox.Show(this, said + "\n\n" + string.Join(" -> ", map.Values), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AdoApiException or HttpRequestException)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { UseWaitCursor = false; }
    }

    // ---- status -----------------------------------------------------------------------------------------------------

    TabPage StatusPage()
    {
        var check = Button(Ui.T("Check", "Sprawdź"), async () =>
        {
            status.Text = Ui.T("checking...", "sprawdzam...");
            status.Text = (await RunAsync("status")).Replace("\n", "\r\n");
        });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Note(Ui.T($"Settings: {path}", $"Ustawienia: {path}")), 0, 0);
        layout.Controls.Add(check, 0, 1);
        layout.Controls.Add(status, 0, 2);
        return Page("Status", layout);
    }

    /// <summary>Runs adolens.exe with <paramref name="args"/> and returns what it printed.</summary>
    internal static async Task<string> RunAsync(string args, Dictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "adolens.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var (k, v) in env ?? []) info.Environment[k] = v;
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return ((await output) + (await errors)).Trim();
    }

    static void Start(string args, bool shell) =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? "adolens.exe", args) { UseShellExecute = shell });

    // ---- layout helpers ---------------------------------------------------------------------------------------------

    static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    static TextBox Field() => new() { Dock = DockStyle.Fill };
    static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 4) };

    Button Button(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => Guard(click);
        return b;
    }

    Button Button(string text, Func<Task> click)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += async (_, _) =>
        {
            try { await click(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        return b;
    }

    void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    static TableLayoutPanel Grid(params (string Label, Control Control)[] rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        for (var i = 0; i < rows.Length; i++)
        {
            grid.Controls.Add(new Label { Text = rows[i].Label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, 0, i);
            grid.Controls.Add(rows[i].Control, 1, i);
        }
        grid.RowCount = rows.Length;
        return grid;
    }

    static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        page.Controls.Add(content);
        return page;
    }
}

/// <summary>Adding a connection: the address, the PAT (kept in Windows Credential Manager) and the default project.</summary>
sealed class LoginDialog : Form
{
    readonly string path;
    readonly string version;
    readonly TextBox address = new() { Dock = DockStyle.Fill, PlaceholderText = "https://dev.azure.com/contoso/Project" };
    readonly TextBox name = new() { Dock = DockStyle.Fill };
    readonly TextBox pat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly ComboBox project = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    readonly Label info = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    readonly Button connect = new() { AutoSize = true };

    public LoginDialog(string settingsPath, string version)
    {
        path = settingsPath;
        this.version = version;
        Text = Ui.T("Add a connection", "Dodaj połączenie");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(560, 270);

        var tokens = new LinkLabel { Text = Ui.T("Create a PAT (Work Items, Code, Build, Release, Wiki: read & write as needed)", "Utwórz PAT (Work Items, Code, Build, Release, Wiki: odczyt i zapis wg potrzeb)"), AutoSize = true };
        tokens.LinkClicked += (_, _) =>
        {
            var org = Settings.ProjectFromUrl(address.Text) is not null || address.Text.Contains('/') ? TryOrg(address.Text) : address.Text.Trim();
            Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(org) ? "https://dev.azure.com" : $"https://dev.azure.com/{org}/_usersSettings/tokens") { UseShellExecute = true });
        };
        connect.Text = Ui.T("Check and save", "Sprawdź i zapisz");
        connect.Click += async (_, _) => await ConnectAsync();
        var cancel = new Button { Text = Ui.T("Cancel", "Anuluj"), AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = connect;
        CancelButton = cancel;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        void Row(string label, Control c, int r)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, 0, r);
            grid.Controls.Add(c, 1, r);
        }
        Row(Ui.T("Azure DevOps address or link", "Adres Azure DevOps albo link"), address, 0);
        Row(Ui.T("Connection name (optional)", "Nazwa połączenia (opcjonalnie)"), name, 1);
        Row("PAT", pat, 2);
        grid.Controls.Add(tokens, 1, 3);
        Row(Ui.T("Default project", "Projekt domyślny"), project, 4);
        grid.Controls.Add(info, 1, 5);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(connect);
        Controls.Add(grid);
        Controls.Add(buttons);
    }

    static string TryOrg(string text)
    {
        try { return Settings.NormalizeOrganization(text); }
        catch (ArgumentException) { return ""; }
    }

    async Task ConnectAsync()
    {
        string org, connection;
        try
        {
            org = Settings.NormalizeOrganization(address.Text);
            connection = Settings.NormalizeConnectionName(string.IsNullOrWhiteSpace(name.Text) ? org : name.Text);
        }
        catch (ArgumentException ex) { info.Text = ex.Message; return; }
        if (string.IsNullOrWhiteSpace(pat.Text)) { info.Text = Ui.T("The PAT is required.", "PAT jest wymagany."); return; }

        connect.Enabled = false;
        UseWaitCursor = true;
        info.Text = Ui.T($"checking access to https://dev.azure.com/{org} ...", $"sprawdzam dostęp do https://dev.azure.com/{org} ...");
        try
        {
            var client = new AdoClient(AdoClient.CreateHttpClient(org, pat.Text.Trim(), version), org);
            var user = await client.WhoAmIAsync();
            var projects = (await client.GetListAsync("_apis/projects?$top=500")).Select(p => Json.Str(p, "name")).OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            var chosen = Empty(project.Text) ?? Settings.ProjectFromUrl(address.Text) ?? (projects.Count == 1 ? projects[0] : null);
            if (chosen is null && projects.Count > 1)
            {
                project.Items.Clear();
                project.Items.AddRange(projects.Cast<object>().ToArray());
                project.DroppedDown = true;
                info.Text = Ui.T($"Signed in as {user}. Pick the default project, then save.", $"Zalogowano jako {user}. Wybierz projekt domyślny i zapisz.");
                return;
            }
            var settings = Settings.Load(path);
            var conn = settings.Connections.TryGetValue(connection, out var stored) ? stored : new Connection();
            conn.Organization = org;
            conn.Project = chosen is null ? conn.Project : projects.FirstOrDefault(p => p.Equals(chosen, StringComparison.OrdinalIgnoreCase)) ?? chosen;
            CredentialStore.Write(connection, org, pat.Text.Trim());
            settings.Connections[connection] = conn;
            if (settings.DefaultConnection is null || !settings.Connections.ContainsKey(settings.DefaultConnection)) settings.DefaultConnection = connection;
            settings.Save(path);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is AdoApiException or HttpRequestException or TaskCanceledException)
        {
            info.Text = Ui.T($"Failed, nothing was saved: {ex.Message}", $"Nie udało się, nic nie zapisano: {ex.Message}");
        }
        finally
        {
            connect.Enabled = true;
            UseWaitCursor = false;
        }
    }

    static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
