using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AdoLens;

/// <summary>
/// Local SQLite copy of the project structure (types, states, fields, allowed values) of one connection, plus the history
/// of changes between refreshes. One file per connection: %LOCALAPPDATA%\ado-lens\meta\&lt;connection&gt;.db.
/// </summary>
public sealed class MetaStore
{
    public const int SchemaVersion = 1;
    readonly string _cs;

    public string Path { get; }

    public MetaStore(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version != SchemaVersion)
        {
            // Metadata is only a copy of what Azure DevOps has: on a schema change it is simply fetched again.
            Exec(c, """
                DROP TABLE IF EXISTS projects; DROP TABLE IF EXISTS types; DROP TABLE IF EXISTS type_fields;
                DROP TABLE IF EXISTS fields; DROP TABLE IF EXISTS changes;
                """);
            Exec(c, $"PRAGMA user_version={SchemaVersion};");
        }
        Exec(c, """
            CREATE TABLE IF NOT EXISTS projects(
              name TEXT PRIMARY KEY COLLATE NOCASE, id TEXT NOT NULL, process TEXT, git INTEGER, tfvc INTEGER,
              default_team TEXT, refreshed TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS types(
              project TEXT COLLATE NOCASE, name TEXT COLLATE NOCASE, ref TEXT, level TEXT, description TEXT, disabled INTEGER,
              ord INTEGER, states TEXT, transitions TEXT, PRIMARY KEY(project, name));
            CREATE TABLE IF NOT EXISTS type_fields(
              project TEXT COLLATE NOCASE, type TEXT COLLATE NOCASE, ref TEXT COLLATE NOCASE, name TEXT, required INTEGER,
              default_value TEXT, allowed TEXT, help TEXT, ord INTEGER, PRIMARY KEY(project, type, ref));
            CREATE TABLE IF NOT EXISTS fields(
              project TEXT COLLATE NOCASE, ref TEXT COLLATE NOCASE, name TEXT, type TEXT, picklist INTEGER, readonly INTEGER,
              description TEXT, PRIMARY KEY(project, ref));
            CREATE TABLE IF NOT EXISTS changes(
              id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, project TEXT COLLATE NOCASE, type TEXT, kind TEXT,
              ref TEXT, detail TEXT);
            CREATE INDEX IF NOT EXISTS changes_project_at ON changes(project, at);
            """);
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    /// <summary>Replaces the snapshot of the project and appends the changes since the previous one.</summary>
    /// <returns>The changes found (none on the first refresh).</returns>
    public List<ChangeRecord> Save(Snapshot snap)
    {
        var old = Load(snap.Project.Name);
        var changes = Snapshot.Diff(old, snap);
        using var c = Open();
        using var tx = c.BeginTransaction();
        var p = snap.Project;
        foreach (var table in new[] { "types", "type_fields", "fields" })
            Exec(c, $"DELETE FROM {table} WHERE project=$p", tx, ("$p", p.Name));
        Exec(c, "DELETE FROM projects WHERE name=$p", tx, ("$p", p.Name));
        Exec(c, "INSERT INTO projects VALUES($n,$id,$proc,$git,$tfvc,$team,$at)", tx,
            ("$n", p.Name), ("$id", p.Id), ("$proc", p.Process), ("$git", p.Git), ("$tfvc", p.Tfvc), ("$team", p.DefaultTeam),
            ("$at", Iso(p.RefreshedUtc)));

        var ord = 0;
        foreach (var t in snap.Types)
            Exec(c, "INSERT INTO types VALUES($p,$n,$r,$l,$d,$dis,$o,$s,$tr)", tx,
                ("$p", p.Name), ("$n", t.Name), ("$r", t.Ref), ("$l", t.Level), ("$d", t.Description), ("$dis", t.Disabled),
                ("$o", ord++), ("$s", JsonSerializer.Serialize(t.States)), ("$tr", JsonSerializer.Serialize(t.Transitions)));

        foreach (var (type, fields) in snap.FieldsByType)
        {
            ord = 0;
            foreach (var f in fields)
                Exec(c, "INSERT OR REPLACE INTO type_fields VALUES($p,$t,$r,$n,$req,$def,$a,$h,$o)", tx,
                    ("$p", p.Name), ("$t", type), ("$r", f.Ref), ("$n", f.Name), ("$req", f.Required), ("$def", f.Default),
                    ("$a", f.Allowed.Count == 0 ? null : JsonSerializer.Serialize(f.Allowed)), ("$h", f.Help), ("$o", ord++));
        }

        foreach (var f in snap.Fields.Values)
            Exec(c, "INSERT OR REPLACE INTO fields VALUES($p,$r,$n,$t,$pl,$ro,$d)", tx,
                ("$p", p.Name), ("$r", f.Ref), ("$n", f.Name), ("$t", f.Type), ("$pl", f.Picklist), ("$ro", f.ReadOnly), ("$d", f.Description));

        foreach (var ch in changes)
            Exec(c, "INSERT INTO changes(at,project,type,kind,ref,detail) VALUES($at,$p,$t,$k,$r,$d)", tx,
                ("$at", Iso(ch.AtUtc)), ("$p", ch.Project), ("$t", ch.Type), ("$k", ch.Kind), ("$r", ch.Ref), ("$d", ch.Detail));
        tx.Commit();
        return changes;
    }

    public ProjectInfo? Project(string name)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id,name,process,git,tfvc,default_team,refreshed FROM projects WHERE name=$p", null, ("$p", name));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadProject(r) : null;
    }

    public List<ProjectInfo> Projects()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id,name,process,git,tfvc,default_team,refreshed FROM projects ORDER BY name");
        using var r = cmd.ExecuteReader();
        var list = new List<ProjectInfo>();
        while (r.Read()) list.Add(ReadProject(r));
        return list;
    }

    static ProjectInfo ReadProject(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) != 0, r.GetInt64(4) != 0,
            r.IsDBNull(5) ? null : r.GetString(5), ParseIso(r.GetString(6)));

    public Snapshot? Load(string project)
    {
        var info = Project(project);
        if (info is null) return null;
        using var c = Open();

        var types = new List<TypeInfo>();
        using (var cmd = Cmd(c, "SELECT name,ref,level,description,disabled,states,transitions FROM types WHERE project=$p ORDER BY ord", null, ("$p", info.Name)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                types.Add(new TypeInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) != 0,
                    JsonSerializer.Deserialize<List<StateInfo>>(r.GetString(5)) ?? [],
                    JsonSerializer.Deserialize<Dictionary<string, List<string>>>(r.GetString(6)) ?? []));

        var byType = new Dictionary<string, List<TypeField>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Cmd(c, "SELECT type,ref,name,required,default_value,allowed,help FROM type_fields WHERE project=$p ORDER BY type, ord", null, ("$p", info.Name)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var type = r.GetString(0);
                if (!byType.TryGetValue(type, out var list)) byType[type] = list = [];
                list.Add(new TypeField(r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? [] : JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [], r.IsDBNull(6) ? null : r.GetString(6)));
            }

        var fields = new Dictionary<string, FieldDef>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Cmd(c, "SELECT ref,name,type,picklist,readonly,description FROM fields WHERE project=$p", null, ("$p", info.Name)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                fields[r.GetString(0)] = new FieldDef(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetInt64(4) != 0,
                    r.IsDBNull(5) ? null : r.GetString(5));

        return new Snapshot(info, types, byType, fields);
    }

    public List<ChangeRecord> Changes(string project, DateTime sinceUtc, int limit = 200)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT at,project,type,kind,ref,detail FROM changes WHERE project=$p AND at>=$s ORDER BY at DESC, id LIMIT $l", null,
            ("$p", project), ("$s", Iso(sinceUtc)), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<ChangeRecord>();
        while (r.Read())
            list.Add(new ChangeRecord(ParseIso(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5)));
        return list;
    }

    static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    static DateTime ParseIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    static SqliteCommand Cmd(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in ps)
            cmd.Parameters.AddWithValue(name, value switch { null => DBNull.Value, bool b => b ? 1 : 0, _ => value });
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, tx, ps);
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = Cmd(c, sql);
        return cmd.ExecuteScalar();
    }
}
