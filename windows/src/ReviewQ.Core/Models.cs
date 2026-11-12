using System.Text.Json;

namespace ReviewQ.Core;

public record AuthorizationSession(string Scheme, string Value, string CacheScope);
public interface IAuthorizationProvider
{
    Task<AuthorizationSession> GetAsync(ReviewSettings settings, CancellationToken cancellationToken);
    void Invalidate();
}
public record DiagnosticRecord(string Event, string Operation, int? Status = null, long? DurationMs = null, string? Outcome = null);
public enum QueueKind { Assigned, Followed, Authored }
public sealed record ReviewItem
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Organization { get; init; } = "";
    public string Project { get; init; } = "";
    public string Repository { get; init; } = "";
    public string RepositoryId { get; init; } = "";
    public Uri Url { get; init; } = new("https://dev.azure.com/");
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsRequired { get; init; }
    public bool IsDraft { get; init; }
    public QueueKind Queue { get; init; }
    public string StageLabel { get; init; } = "First Review";
    public string Explanation { get; init; } = "";
    public int? ActiveComments { get; init; }
    public bool MergeReady { get; init; }
    public string Id => $"{Organization}/{RepositoryId}/{Number}".ToLowerInvariant();
    public string GroupKey => $"{Organization}/{Project}/{Repository}";
}
public sealed record ReviewSnapshot(IReadOnlyList<ReviewItem> Items)
{
    public int PendingCount => Items.Count(x => x.Queue == QueueKind.Assigned);
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
}
public interface IReviewLoader
{
    Task<ReviewSnapshot> LoadAsync(ReviewSettings settings, CancellationToken cancellationToken);
    Task ClearIdentitiesAsync(CancellationToken cancellationToken);
}

internal static class Json
{
    public static JsonElement Required(this JsonElement e, string key) =>
        e.TryGetProperty(key, out var value) ? value : throw Invalid();
    public static string Text(this JsonElement e, string key) =>
        e.Required(key).GetString() ?? throw Invalid();
    public static string? OptionalText(this JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    public static bool Flag(this JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null && v.GetBoolean();
    public static int Integer(this JsonElement e, string key) => e.Required(key).GetInt32();
    public static Guid Identity(this JsonElement e, string key)
    {
        if (!Guid.TryParse(e.Text(key), out var id) || id == Guid.Empty) throw Invalid();
        return id;
    }
    public static ReviewException Invalid() => new("Azure DevOps returned an unexpected response. Check your account and project access.");
}

public sealed record Discussion(int? Id, DateTimeOffset? PublishedAt, Guid? Voter, int? Vote,
    string Status, bool Deleted, bool System, IReadOnlyList<DiscussionComment> Comments);
public sealed record DiscussionComment(Guid Author, bool Deleted, bool IsReview);
public sealed record HistoryResult(string Label, int? Resolved = null, int? Open = null);

public static class ReviewHistory
{
    public static HistoryResult Classify(IReadOnlyList<Discussion> threads, Guid reviewer)
    {
        var votes = threads.Where(x => x.Voter == reviewer && x.Vote is not null and not 0).ToList();
        if (votes.Count > 0)
        {
            Discussion last = votes[0];
            if (votes.Select(x => x.Vote).Distinct().Count() > 1)
            {
                if (votes.Any(x => x.PublishedAt is null)) throw Chronology();
                var latest = votes.Where(x => x.PublishedAt == votes.Max(y => y.PublishedAt)).ToList();
                if (latest.Select(x => x.Vote).Distinct().Count() > 1 &&
                    (latest.Any(x => x.Id is null) || latest.Select(x => x.Id).Distinct().Count() != latest.Count))
                    throw Chronology();
                last = latest.OrderByDescending(x => x.Id).First();
            }
            return new(last.Vote switch
            {
                10 => "You Approved", 5 => "You Approved with Suggestions",
                -5 => "You Waited for Author", -10 => "You Rejected",
                _ => throw Json.Invalid()
            });
        }
        var commented = threads.Where(x => x.Comments.Any(c => c.Author == reviewer && c.IsReview)).ToList();
        if (commented.Count == 0) return new("First Review");
        var relevant = commented.Where(x => !x.Deleted && !x.System &&
            x.Comments.Any(c => c.Author == reviewer && c.IsReview && !c.Deleted)).ToList();
        if (relevant.Count == 0 || relevant.Any(x => x.Status is not ("fixed" or "active" or "pending")))
            return new("You commented");
        var resolved = relevant.Count(x => x.Status == "fixed");
        var open = relevant.Count - resolved;
        return new(open == 0 ? "Your comments are resolved" : $"You commented [{resolved} resolved, {open} open]", resolved, open);
    }
    private static ReviewException Chronology() => new("Azure DevOps review-history chronology is ambiguous. The previous queue has not been replaced.");
    public static IReadOnlyList<Discussion> Decode(JsonElement array)
    {
        var result = new List<Discussion>();
        foreach (var t in array.EnumerateArray())
        {
            bool system = false;
            Guid? voter = null;
            int? vote = null;
            if (t.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                var type = Property(props, "CodeReviewThreadType");
                system = !string.IsNullOrEmpty(type);
                if (type?.Equals("VoteUpdate", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!int.TryParse(Property(props, "CodeReviewVoteResult"), out var v)) throw Json.Invalid();
                    vote = v;
                    var direct = Property(props, "CodeReviewVotedByTfId");
                    if (direct is not null)
                    {
                        if (!Guid.TryParse(direct, out var id) || id == Guid.Empty) throw Json.Invalid();
                        voter = id;
                    }
                    else if (v != 0 && Property(props, "CodeReviewVotedByIdentity") is { } reference)
                        voter = t.Required("identities").Required(reference).Identity("id");
                    if (v != 0 && voter is null) throw Json.Invalid();
                }
            }
            var comments = t.Required("comments").EnumerateArray().Select(c =>
            {
                var type = c.OptionalText("commentType")?.ToLowerInvariant();
                var deleted = c.Flag("isDeleted");
                var isReview = type == "text" || ((type is null or "unknown") && !system &&
                    (deleted || !string.IsNullOrWhiteSpace(c.OptionalText("content"))));
                return new DiscussionComment(c.Required("author").Identity("id"), deleted, isReview);
            }).ToList();
            string status = "unknown";
            if (t.TryGetProperty("status", out var s) && s.ValueKind != JsonValueKind.Null)
                status = s.ValueKind == JsonValueKind.Number ? s.GetInt32() switch
                { 1 => "active", 2 => "fixed", 3 => "wontfix", 4 => "closed", 5 => "bydesign", 6 => "pending", _ => "unknown" }
                    : s.GetString()?.ToLowerInvariant() ?? "unknown";
            result.Add(new(t.TryGetProperty("id", out var i) && i.ValueKind != JsonValueKind.Null ? i.GetInt32() : null,
                t.TryGetProperty("publishedDate", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetDateTimeOffset() : null,
                voter, vote, status, t.Flag("isDeleted"), system, comments));
        }
        return result;
    }
    private static string? Property(JsonElement p, string key) =>
        p.TryGetProperty(key, out var v) ? v.Required("$value").ToString() : null;
}
