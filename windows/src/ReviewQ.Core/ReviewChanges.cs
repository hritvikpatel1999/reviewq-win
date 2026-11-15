using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReviewQ.Core;

public sealed class ReviewChanges
{
    public IReadOnlySet<string> ItemIds { get; }
    public IReadOnlySet<string> RepositoryKeys { get; }
    public bool Followed { get; private set; }
    public bool Authored { get; private set; }
    public bool HasChanges => ItemIds.Count > 0 || RepositoryKeys.Count > 0 || Followed || Authored;
    public ReviewChanges(ReviewSnapshot? previous = null, ReviewSnapshot? current = null)
    {
        var ids = new HashSet<string>();
        var groups = new HashSet<string>();
        void Mark(ReviewItem item)
        {
            if (item.Queue == QueueKind.Followed) Followed = true;
            else if (item.Queue == QueueKind.Authored) Authored = true;
            else groups.Add(item.GroupKey);
        }
        if (previous is not null && current is not null && previous.IdentityScope == current.IdentityScope)
        {
            var remaining = previous.Items.ToDictionary(x => x.Id);
            foreach (var item in current.Items)
            {
                remaining.Remove(item.Id, out var old);
                if (old == item) continue;
                ids.Add(item.Id); Mark(item);
                if (old is not null) Mark(old);
            }
            foreach (var removed in remaining.Values) Mark(removed);
        }
        ItemIds = ids;
        RepositoryKeys = groups;
    }
    internal static string Fingerprint(JsonElement request, JsonElement threads)
    {
        static string Canonical(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => JsonSerializer.Serialize(x.Name) + ":" + Canonical(x.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical).Order(StringComparer.Ordinal)) + "]",
            _ => element.GetRawText()
        };
        var fields = new[] { "sourceRefName", "targetRefName", "lastMergeSourceCommit", "reviewers" }
            .Select(key => request.TryGetProperty(key, out var value) ? Canonical(value) : "null");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(fields.Append(Canonical(threads)).ToArray()))));
    }
}
