using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>
/// The adolens command line. Options are checked against what the command understands, so a typo stops with an
/// error instead of being ignored; "-name" works like "--name", and "--name=value" like "--name value".
/// </summary>
public sealed partial class CommandLine
{
    // "name=" takes a value, "name" is a switch.
    static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mcp"] = [],
        ["login"] = ["connection=", "org=", "project=", "team=", "pat-stdin", "default"],
        ["logout"] = ["connection="],
        ["connections"] = [],
        ["config"] = ["connection=", "default-connection=", "project=", "team=", "mode=", "usage-log=", "add-toolset=", "remove-toolset=", "confirm-above=", "show-tokens="],
        ["item"] = ["connection=", "include=", "detail="],
        ["query"] = ["connection=", "project=", "wiql=", "query=", "top="],
        ["create"] = ["connection=", "project=", "type=", "title=", "description=", "criteria=", "assign=", "parent=", "iteration=", "area=", "tags=", "state=", "field="],
        ["update"] = ["connection=", "ids=", "wiql=", "state=", "reason=", "title=", "description=", "criteria=", "assign=", "iteration=", "area=", "tags=", "field=", "comment=", "confirm="],
        ["comment"] = ["connection="],
        ["sprint"] = ["connection=", "project=", "team=", "detail="],
        ["board"] = ["connection=", "project=", "team=", "detail=", "move=", "column="],
        ["backlog"] = ["connection=", "project=", "team=", "level=", "top=", "move=", "above=", "below="],
        ["myday"] = ["connection=", "project=", "since="],
        ["wiki"] = ["connection=", "project=", "wiki=", "search=", "depth=", "detail="],
        ["wiki-write"] = ["connection=", "project=", "wiki=", "mode=", "version=", "comment=", "file="],
        ["link"] = ["connection=", "type=", "remove"],
        ["status"] = ["connection=", "offline", "quiet"],
        ["register"] = ["name=", "exe=", "desktop", "code", "check", "force"],
        ["unregister"] = ["name=", "exe=", "desktop", "code", "check", "force", "any"],
        ["refresh"] = ["connection=", "project="],
        ["fields"] = ["connection=", "project=", "type=", "field=", "changes=", "all", "refresh"],
        ["stats"] = ["days=", "top=", "live"],
        ["api"] = ["connection="],
    };

    [GeneratedRegex(@"^--?(?<name>[A-Za-z][A-Za-z0-9-]*)(=(?<value>.*))?$", RegexOptions.Singleline)]
    private static partial Regex OptionPattern();

    readonly Dictionary<string, Queue<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _switches = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    /// <summary>Arguments that are not options, in order. After "--" everything is positional.</summary>
    public List<string> Positional { get; } = [];

    CommandLine(string command) => Command = command;

    /// <exception cref="ArgumentException">An option the command does not know, or one without its value.</exception>
    public static CommandLine Parse(string[] args)
    {
        var cl = new CommandLine(args.Length > 0 ? args[0].ToLowerInvariant() : "mcp");
        // help, version and unknown commands: no option checking (an unknown command fails on its own).
        Known.TryGetValue(cl.Command, out var spec);
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                cl.Positional.AddRange(args.Skip(i + 1));
                break;
            }
            var m = OptionPattern().Match(a);
            if (spec == null || !m.Success)
            {
                cl.Positional.Add(a);
                continue;
            }
            var name = m.Groups["name"].Value;
            if (spec.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (m.Groups["value"].Success) throw new ArgumentException($"option --{name} of '{cl.Command}' takes no value");
                cl._switches.Add(name);
            }
            else if (spec.Contains(name + "=", StringComparer.OrdinalIgnoreCase))
            {
                string value;
                if (m.Groups["value"].Success) value = m.Groups["value"].Value;
                else if (i + 1 < args.Length) value = args[++i];
                else throw new ArgumentException($"option --{name} of '{cl.Command}' needs a value");
                if (!cl._values.TryGetValue(name, out var q)) cl._values[name] = q = new Queue<string>();
                q.Enqueue(value);
            }
            else
            {
                var known = spec.Length == 0 ? "none" : string.Join(", ", spec.Select(s => "--" + s.TrimEnd('=')));
                throw new ArgumentException($"unknown option '{a}' for '{cl.Command}' (known: {known}; put '--' before an argument that starts with '-')");
            }
        }
        return cl;
    }

    /// <summary>The value of an option, or null. Repeated options are read one by one.</summary>
    public string? Opt(string name) =>
        _values.TryGetValue(name, out var q) && q.Count > 0 ? q.Dequeue() : null;

    /// <summary>The value of an option without consuming it.</summary>
    public string? Peek(string name) =>
        _values.TryGetValue(name, out var q) && q.Count > 0 ? q.Peek() : null;

    public bool Flag(string name) => _switches.Contains(name);
}
