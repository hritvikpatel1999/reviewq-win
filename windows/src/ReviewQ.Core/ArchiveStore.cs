using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewQ.Core;

public sealed class ArchiveStore
{
    public const int MaximumItems = 4096;
    public const int MaximumBytes = 512 * 1024;
    private sealed record Saved(int Version, Dictionary<string, HashSet<string>> Scopes);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string path;
    private Dictionary<string, HashSet<string>> scopes = new();
    public string? Error { get; private set; }
    public ArchiveStore(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumBytes) throw Invalid();
            var stored = JsonSerializer.Deserialize<Saved>(stream, Options);
            if (stored is null || stored.Version != 1 || stored.Scopes is null ||
                stored.Scopes.Count > MaximumItems || stored.Scopes.Values.Any(x => x is null) ||
                stored.Scopes.Values.Sum(x => x.Count) > MaximumItems ||
                stored.Scopes.Any(x => !ValidScope(x.Key) || x.Value.Count == 0 || x.Value.Any(id => !ValidId(id))))
                throw Invalid();
            scopes = stored.Scopes;
        }
        catch (Exception ex) when (ex is ReviewException or JsonException or IOException or UnauthorizedAccessException)
        {
            Error = "Saved PR archive choices could not be read and were cleared. Archive the PRs again.";
            try { File.Delete(path); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { Error = "Saved PR archive choices could not be read or cleared. Check local storage permissions."; }
        }
    }
    public IReadOnlySet<string> Ids(string? scope) =>
        scope is not null && scopes.TryGetValue(scope, out var ids) ? new HashSet<string>(ids) : new HashSet<string>();
    public void Set(ReviewSnapshot snapshot, ReviewItem item, bool archived)
    {
        if (!ValidScope(snapshot.IdentityScope) || !ValidId(item.Id) || !snapshot.Items.Any(x => x.Id == item.Id))
            throw new ReviewException("This PR is no longer in the authenticated queue. Refresh before changing its archive choice.");
        var updated = scopes.ToDictionary(x => x.Key, x => new HashSet<string>(x.Value));
        if (archived)
        {
            if (!updated.TryGetValue(snapshot.IdentityScope, out var ids)) updated[snapshot.IdentityScope] = ids = [];
            ids.Add(item.Id);
        }
        else if (updated.TryGetValue(snapshot.IdentityScope, out var ids))
        {
            ids.Remove(item.Id);
            if (ids.Count == 0) updated.Remove(snapshot.IdentityScope);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(1, updated));
        if (updated.Values.Sum(x => x.Count) > MaximumItems || bytes.Length > MaximumBytes)
            throw new ReviewException("The local PR archive is full. Restore some PRs before archiving more.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        scopes = updated;
        Error = null;
    }
    public static bool ValidScope(string? scope) => scope is { Length: 64 } && scope.All(Uri.IsHexDigit);
    private static bool ValidId(string? id)
    {
        if (id is null || id.Length > 2048 || id != id.ToLowerInvariant() || id.Any(char.IsControl)) return false;
        var parts = id.Split('/');
        return parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0 &&
            int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number > 0 && number.ToString(CultureInfo.InvariantCulture) == parts[2];
    }
    private static ReviewException Invalid() => new("Invalid local archive choices.");
}
