using System.Text.Json;
using ReviewQ.Core;
using Xunit;

namespace ReviewQ.Core.Tests;

public sealed class QueueCacheAndChangesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReviewQ-CacheTests-" + Guid.NewGuid().ToString("N"));
    private static ReviewSettings Settings => new() { SourceUrls = "https://dev.azure.com/org/project" };
    private string PathName => Path.Combine(directory, "queue-cache.json");
    private static ReviewItem Item(int number = 1, QueueKind queue = QueueKind.Assigned) => new()
    {
        Number = number, Title = "Synthetic title", Author = "Synthetic author", Organization = "org", Project = "project",
        Repository = "repo", RepositoryId = "repository-id", Queue = queue,
        Url = new($"https://dev.azure.com/org/project/_git/repository-id/pullrequest/{number}"),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), ChangeFingerprint = "fingerprint"
    };
    private static ReviewSnapshot Snapshot(params ReviewItem[] items) =>
        new(items) { IdentityScope = ReviewQueueCache.Scope(Settings, "synthetic-account"), FetchedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };

    [Fact]
    public void RoundTripRetainsTimestampAndDisplayOnlyPayload()
    {
        var cache = new ReviewQueueCache(PathName);
        var snapshot = Snapshot(Item());
        Assert.True(cache.Save(snapshot));
        var restored = cache.Restore(snapshot.IdentityScope, DateTimeOffset.UtcNow)!;
        Assert.Equal(snapshot.FetchedAt, restored.FetchedAt);
        Assert.Equal(snapshot.Items, restored.Items);
        Assert.Equal(snapshot.IdentityScope, restored.IdentityScope);
        var json = File.ReadAllText(PathName);
        Assert.DoesNotContain("synthetic-account", json);
        Assert.DoesNotContain("CopilotAdditionalInstructions", json);
        Assert.DoesNotContain("accessToken", json);
        Assert.DoesNotContain("description", json);
        using var document = JsonDocument.Parse(json);
        var storedItem = document.RootElement.GetProperty("Snapshot").GetProperty("Items")[0];
        Assert.False(storedItem.TryGetProperty("Comments", out _));
        Assert.False(storedItem.TryGetProperty("Description", out _));
        Assert.False(File.Exists(PathName + ".tmp"));
    }
    [Theory]
    [InlineData(-7)]
    [InlineData(-8)]
    [InlineData(1)]
    public void ExpiredOrFutureSnapshotIsNotRestored(int days)
    {
        var cache = new ReviewQueueCache(PathName);
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(Item()) with { FetchedAt = now.AddDays(days) };
        cache.Save(snapshot);
        Assert.Null(cache.Restore(snapshot.IdentityScope, now));
        Assert.False(File.Exists(PathName));
    }
    [Fact]
    public void AccountMismatchClearsCacheWithoutDisplayingIt()
    {
        var cache = new ReviewQueueCache(PathName);
        cache.Save(Snapshot(Item()));
        Assert.Null(cache.Restore(ReviewQueueCache.Scope(Settings, "another-account"), DateTimeOffset.UtcNow));
        Assert.False(File.Exists(PathName));
    }
    [Fact]
    public void QueueScopeIncludesEveryQueueInputButNotPresentationSettings()
    {
        var scope = ReviewQueueCache.Scope(Settings, "account");
        var changes = new[]
        {
            Settings with { SourceUrls = "https://dev.azure.com/org/other" },
            Settings with { Authentication = AuthenticationMethod.PersonalAccessToken },
            Settings with { TenantId = "tenant.example" },
            Settings with { IncludeDrafts = true },
            Settings with { FollowPeople = true, FollowedAuthorEmails = "one@example.com" },
            Settings with { ShowAuthoredPullRequests = true }
        };
        Assert.All(changes, changed => Assert.NotEqual(scope, ReviewQueueCache.Scope(changed, "account")));
        Assert.Equal(scope, ReviewQueueCache.Scope(Settings with
        { ShowCount = false, RefreshMinutes = 5, CopilotAdditionalInstructions = "never cache this prompt" }, "account"));
        Assert.NotEqual(scope, ReviewQueueCache.Scope(Settings, "other-account"));
    }
    [Fact]
    public void FullMaximumQueueCanBeCachedButOversizeQueueIsNeverPartiallySaved()
    {
        var cache = new ReviewQueueCache(PathName);
        var snapshot = Snapshot(Enumerable.Range(1, ReviewQueueCache.MaximumItems).Select(x => Item(x)).ToArray());
        Assert.True(cache.Save(snapshot));
        Assert.Equal(4096, cache.Restore(snapshot.IdentityScope, DateTimeOffset.UtcNow)!.Items.Count);
        Assert.False(cache.Save(Snapshot(Enumerable.Range(1, 4097).Select(x => Item(x)).ToArray())));
        Assert.False(File.Exists(PathName));
    }
    [Fact]
    public void EncodedByteLimitIsEnforcedNotJustItemCount()
    {
        var cache = new ReviewQueueCache(PathName);
        Assert.True(cache.Save(Snapshot(Item())));
        Assert.False(cache.Save(Snapshot(Item() with { Title = new string('x', ReviewQueueCache.MaximumBytes) })));
        Assert.False(File.Exists(PathName));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(PathName, new byte[ReviewQueueCache.MaximumBytes + 1]);
        Assert.Null(cache.Restore(Snapshot().IdentityScope, DateTimeOffset.UtcNow));
        Assert.False(File.Exists(PathName));
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":9,\"Scope\":\"scope\",\"Snapshot\":null}")]
    public void CorruptCacheReportsActionableError(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathName, json);
        Assert.Throws<ReviewException>(() => new ReviewQueueCache(PathName).Restore("scope", DateTimeOffset.UtcNow));
    }
    [Fact]
    public void InvalidLinksCannotBeRestoredFromCache()
    {
        var cache = new ReviewQueueCache(PathName);
        var snapshot = Snapshot(Item() with { Url = new("https://example.com/private") });
        cache.Save(snapshot);
        Assert.Throws<ReviewException>(() => cache.Restore(snapshot.IdentityScope, DateTimeOffset.UtcNow));
    }
    [Fact]
    public void ChangesUseSuccessfulBaselineAndIgnoreFetchedAt()
    {
        var first = Snapshot(Item());
        Assert.False(new ReviewChanges(null, first).HasChanges);
        Assert.False(new ReviewChanges(first, first with { FetchedAt = DateTimeOffset.UtcNow }).HasChanges);
        var changed = first with { Items = new[] { Item() with { Title = "Changed", CreatedAt = first.Items[0].CreatedAt } } };
        var changes = new ReviewChanges(first, changed);
        Assert.Single(changes.ItemIds);
        Assert.Contains(first.Items[0].GroupKey, changes.RepositoryKeys);
        Assert.False(new ReviewChanges(changed, changed).HasChanges);
        Assert.False(new ReviewChanges(first, changed with { IdentityScope = "other" }).HasChanges);
    }
    [Fact]
    public void RemovalsMarkSectionAndTrayButNotAnAbsentItem()
    {
        var before = Snapshot(Item(), Item(2, QueueKind.Followed), Item(3, QueueKind.Authored));
        var changes = new ReviewChanges(before, Snapshot());
        Assert.Empty(changes.ItemIds); Assert.Single(changes.RepositoryKeys);
        Assert.True(changes.Followed); Assert.True(changes.Authored); Assert.True(changes.HasChanges);
    }
    [Fact]
    public void MovingBetweenQueuesMarksBothOldAndNewSections()
    {
        var item = Item();
        var changes = new ReviewChanges(Snapshot(item), Snapshot(item with { Queue = QueueKind.Followed }));
        Assert.Contains(item.Id, changes.ItemIds); Assert.Contains(item.GroupKey, changes.RepositoryKeys);
        Assert.True(changes.Followed);
    }
    [Theory]
    [InlineData("fingerprint")]
    [InlineData("title")]
    [InlineData("required")]
    [InlineData("stage")]
    public void DisplayAndCompactFingerprintChangesMarkItems(string field)
    {
        var item = Item();
        var updated = field switch
        {
            "fingerprint" => item with { ChangeFingerprint = "new-commit-or-thread" },
            "title" => item with { Title = "New title" },
            "required" => item with { IsRequired = true },
            _ => item with { StageLabel = "You commented" }
        };
        Assert.Contains(item.Id, new ReviewChanges(Snapshot(item), Snapshot(updated)).ItemIds);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
