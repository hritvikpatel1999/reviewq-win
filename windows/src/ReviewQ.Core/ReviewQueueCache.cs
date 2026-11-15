using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewQ.Core;

public sealed class ReviewQueueCache(string path)
{
    public const int MaximumBytes = 2 * 1_048_576;
    public const int MaximumItems = 4_096;
    private sealed record Envelope(int Version, string Scope, ReviewSnapshot Snapshot);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        IgnoreReadOnlyProperties = true
    };
    public static string ConfigurationScope(ReviewSettings settings) => Hash(JsonSerializer.Serialize(new
    {
        Sources = settings.Sources().Select(x => x.Id).Order(StringComparer.Ordinal).ToArray(),
        settings.Authentication, Tenant = settings.TenantId.Trim().ToLowerInvariant(),
        settings.IncludeDrafts, settings.FollowPeople,
        Emails = settings.FollowedEmails().Order(StringComparer.Ordinal).ToArray(),
        settings.ShowAuthoredPullRequests
    }));
    public static string Scope(ReviewSettings settings, string account) =>
        Hash(JsonSerializer.Serialize(new[] { ConfigurationScope(settings), account }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public ReviewSnapshot? Restore(string scope, DateTimeOffset now)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) { stream.Dispose(); Clear(); return null; }
        Envelope? stored;
        try { stored = JsonSerializer.Deserialize<Envelope>(stream, Options); }
        catch (JsonException) { throw new ReviewException("Saved startup queue is corrupt. Clear the startup cache in Settings, then refresh."); }
        if (stored is null || stored.Version != 1 || stored.Snapshot is null ||
            stored.Snapshot.Items is null || stored.Snapshot.Items.Count > MaximumItems ||
            stored.Snapshot.Items.Any(x => x is null || x.Number <= 0 || !Enum.IsDefined(x.Queue) ||
                x.Title is null || x.Author is null || x.Organization is null || x.Repository is null ||
                x.Project is null || x.RepositoryId is null || x.Url is null || x.ChangeFingerprint is null ||
                x.StageLabel is null || x.Explanation is null) ||
            stored.Snapshot.Items.Select(x => x.Id).Distinct().Count() != stored.Snapshot.Items.Count)
            throw new ReviewException("Saved startup queue is invalid. Clear the startup cache in Settings, then refresh.");
        foreach (var item in stored.Snapshot.Items) ReviewPrompt.ValidateUrl(item.Url);
        var age = now - stored.Snapshot.FetchedAt;
        if (stored.Scope != scope || stored.Snapshot.IdentityScope != scope ||
            age < TimeSpan.Zero || age >= TimeSpan.FromDays(7))
        { stream.Dispose(); Clear(); return null; }
        return stored.Snapshot;
    }
    public bool Save(ReviewSnapshot snapshot)
    {
        if (snapshot.Items.Count > MaximumItems || string.IsNullOrEmpty(snapshot.IdentityScope))
        { Clear(); return false; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, snapshot.IdentityScope, snapshot), Options);
        if (bytes.Length > MaximumBytes) { Clear(); return false; }
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }
    public void Clear() => File.Delete(path);
}
