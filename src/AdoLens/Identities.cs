using System.Collections.Concurrent;

namespace AdoLens;

public sealed record Person(string Id, string DisplayName, string? Mail, string? Account, bool Active)
{
    /// <summary>What identity fields (Assigned To) accept: the sign-in name, else the display name.</summary>
    public string FieldValue => Account ?? Mail ?? DisplayName;

    public override string ToString() => Mail is null ? DisplayName : $"{DisplayName} <{Mail}>";
}

/// <summary>
/// Finds people by name or e-mail ("Ania", "kowalska", "a.kowalska@contoso.com") through the identity service of the
/// organization. One match is used; several are an error that lists them, so Claude asks instead of guessing.
/// </summary>
public sealed class Identities(AdoClient client)
{
    readonly ConcurrentDictionary<string, List<Person>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<Person>> SearchAsync(string text, CancellationToken ct = default)
    {
        var q = text.Trim().TrimStart('@').Trim('[', ']');
        if (_cache.TryGetValue(q, out var hit)) return hit;
        var found = await client.GetListAsync(
            client.ServiceUrl("vssps") + $"_apis/identities?searchFilter=General&filterValue={Uri.EscapeDataString(q)}&queryMembership=None", ct);
        var people = found
            .Where(i => !Json.Bool(i, "isContainer"))
            .Select(i => new Person(Json.Str(i, "id") ?? "", Json.Str(i, "providerDisplayName") ?? Json.Str(i, "customDisplayName") ?? q,
                Prop(i, "Mail"), Prop(i, "Account"), !i.TryGetProperty("isActive", out var a) || a.ValueKind != System.Text.Json.JsonValueKind.False))
            .Where(p => p.Id.Length > 0)
            .ToList();
        // Active people first; an exact name or e-mail match wins over partial ones.
        // The identity search misses some (guests, names with a company suffix): the people picker of the web finds them.
        if (people.Count == 0) people = await PickerAsync(q, ct);
        people = people.OrderByDescending(p => p.Active).ToList();
        _cache[q] = people;
        return people;
    }

    async Task<List<Person>> PickerAsync(string q, CancellationToken ct)
    {
        try
        {
            var found = await client.PostAsync("_apis/IdentityPicker/Identities?api-version=7.1-preview.1", new
            {
                query = q,
                identityTypes = new[] { "user" },
                operationScopes = new[] { "ims", "source" },
                options = new { MinResults = 5, MaxResults = 20 },
                properties = new[] { "DisplayName", "Mail", "SignInAddress", "Active" },
            }, ct);
            return Json.Arr(found, "results").SelectMany(r => Json.Arr(r, "identities"))
                .Select(i => new Person(Json.Str(i, "localId") ?? Json.Str(i, "originId") ?? "", Json.Str(i, "displayName") ?? q,
                    Json.Str(i, "mail"), Json.Str(i, "signInAddress"), !i.TryGetProperty("active", out var a) || a.ValueKind != System.Text.Json.JsonValueKind.False))
                .Where(p => p.Id.Length > 0).GroupBy(p => p.Id).Select(g => g.First()).ToList();
        }
        catch (AdoApiException) { return []; }
    }

    /// <exception cref="InvalidOperationException">Nobody or several people match.</exception>
    public async Task<Person> ResolveAsync(string text, CancellationToken ct = default)
    {
        var q = text.Trim().TrimStart('@').Trim('[', ']');
        var people = await SearchAsync(q, ct);
        var exact = people.Where(p => p.DisplayName.Equals(q, StringComparison.OrdinalIgnoreCase)
                                      || Plain(p.DisplayName).Equals(Plain(q), StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(p.Mail, q, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(p.Account, q, StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = exact.Count > 0 ? exact : people.Where(p => p.Active).ToList();
        if (candidates.Count == 0) candidates = people;
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException($"nobody named '{q}' found in the organization"),
            _ => throw new InvalidOperationException(
                $"'{q}' matches {candidates.Count} people: {string.Join("; ", candidates.Take(8))}{(candidates.Count > 8 ? "; …" : "")} - use the full name or e-mail"),
        };
    }

    /// <summary>A display name without a trailing "(Company)": "Anna Nowak (Contoso)" → "Anna Nowak".</summary>
    internal static string Plain(string name) => System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s*\([^)]*\)$", "");

    static string? Prop(System.Text.Json.JsonElement identity, string name)
    {
        var props = Json.Obj(identity, "properties");
        var p = Json.Obj(props, name);
        return Json.Str(p, "$value") is { Length: > 0 } v ? v : null;
    }
}
