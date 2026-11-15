using System.IO;
using System.IO.Compression;
using ReviewQ.Core;
using ReviewQ.Windows;
using Xunit;

namespace ReviewQ.Windows.Tests;

public sealed class SetupAndCachedStartupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReviewQ-SetupTests-" + Guid.NewGuid().ToString("N"));
    private static ReviewSettings Settings => new() { SourceUrls = "https://dev.azure.com/org/project" };
    private static ReviewSnapshot Snapshot(string title = "Synthetic title") => new(new[] { new ReviewItem
    { Number = 1, Title = title, Author = "Synthetic", RepositoryId = "repo", Repository = "Repository", Project = "project",
        Organization = "org", Url = new("https://dev.azure.com/org/project/_git/repo/pullrequest/1") } })
    { IdentityScope = ReviewQueueCache.Scope(Settings, "account"), FetchedAt = DateTimeOffset.UtcNow.AddHours(-1) };
    private SetupProgressStore Progress => new(directory);
    private SetupSession Session(
        Func<ReviewSettings, string, CancellationToken, Task<ReviewSnapshot>>? verify = null,
        Func<ReviewSettings, string, ReviewSnapshot, Task>? save = null) =>
        new(Settings, Progress, verify ?? ((_, _, _) => Task.FromResult(Snapshot())),
            save ?? ((_, _, _) => Task.CompletedTask), _ => { });

    [Fact]
    public async Task SixStepsRequireVerificationAndOnlyFinishSaves()
    {
        var checks = 0; var saves = 0;
        using var session = Session((settings, _, _) =>
        { checks++; Assert.False(settings.FollowPeople); return Task.FromResult(Snapshot()); },
            (_, _, snapshot) => { saves++; Assert.Single(snapshot.Items); return Task.CompletedTask; });
        session.Begin(Settings);
        Assert.Equal(SetupStep.Welcome, session.Step);
        await session.AdvanceAsync(); Assert.Equal(SetupStep.Sources, session.Step);
        await session.AdvanceAsync(); Assert.Equal(SetupStep.Authentication, session.Step);
        await session.AdvanceAsync(); Assert.NotNull(session.Error); Assert.Equal(0, saves);
        await session.TestAsync(); Assert.True(session.HasVerifiedAccount);
        await session.AdvanceAsync(); Assert.Equal(SetupStep.Companions, session.Step);
        await session.AdvanceAsync(); Assert.Equal(SetupStep.Preferences, session.Step);
        await session.AdvanceAsync(); Assert.Equal(SetupStep.Ready, session.Step);
        Assert.Equal(1, checks); Assert.Equal(0, saves);
        Assert.True(await session.CompleteAsync());
        Assert.Equal(1, saves); Assert.False(session.IsActive); Assert.False(File.Exists(Progress.FilePath));
    }
    [Fact]
    public async Task QueuePreferencesRecheckWithoutInvalidatingAccount()
    {
        var checks = new List<ReviewSettings>();
        using var session = Session((settings, _, _) => { checks.Add(settings); return Task.FromResult(Snapshot()); });
        session.Begin(Settings); await session.AdvanceAsync(); await session.AdvanceAsync(); await session.TestAsync();
        await session.AdvanceAsync(); await session.AdvanceAsync();
        session.Edit(session.Draft with { IncludeDrafts = true, ShowAuthoredPullRequests = true });
        Assert.True(session.HasVerifiedAccount); Assert.False(session.HasVerifiedQueue);
        await session.AdvanceAsync();
        Assert.Equal(2, checks.Count); Assert.True(checks[1].IncludeDrafts); Assert.True(checks[1].ShowAuthoredPullRequests);
        Assert.Equal(SetupStep.Ready, session.Step);
    }
    [Theory]
    [InlineData("sources")]
    [InlineData("tenant")]
    [InlineData("auth")]
    [InlineData("pat")]
    public async Task AccountOrTokenEditsInvalidateVerification(string field)
    {
        using var session = Session(); session.Begin(Settings);
        await session.TestAsync(); Assert.True(session.HasVerifiedAccount);
        if (field == "pat") session.Token = "new-pat";
        else session.Edit(field switch
        {
            "sources" => session.Draft with { SourceUrls = "https://dev.azure.com/org/another" },
            "tenant" => session.Draft with { TenantId = "tenant.example" },
            _ => session.Draft with { Authentication = AuthenticationMethod.PersonalAccessToken }
        });
        Assert.False(session.HasVerifiedAccount); Assert.False(session.HasVerifiedQueue); Assert.Null(session.Snapshot);
    }
    [Fact]
    public async Task ClosingAndResumingRetainsChoicesButNeverTokenOrVerification()
    {
        using var session = Session(); session.Begin(Settings);
        session.Token = "synthetic-pat-do-not-persist";
        await session.AdvanceAsync(); await session.AdvanceAsync(); await session.TestAsync();
        await session.AdvanceAsync(); await session.AdvanceAsync();
        session.Edit(session.Draft with { RefreshMinutes = 5, ShowCount = false });
        session.Pause();
        Assert.Equal("", session.Token); Assert.False(session.HasVerifiedAccount);
        var json = File.ReadAllText(Progress.FilePath);
        Assert.DoesNotContain("synthetic-pat", json); Assert.DoesNotContain("Synthetic title", json);
        using var resumed = Session();
        Assert.True(resumed.IsActive); Assert.Equal(SetupStep.Authentication, resumed.Step);
        Assert.Equal(5, resumed.Draft.RefreshMinutes); Assert.False(resumed.Draft.ShowCount);
        Assert.False(resumed.HasVerifiedQueue); Assert.NotNull(resumed.Info);
    }
    [Fact]
    public async Task LateVerificationCannotOverwriteEditsOrPausedSession()
    {
        var pending = new TaskCompletionSource<ReviewSnapshot>();
        using var session = Session((_, _, _) => pending.Task);
        session.Begin(Settings);
        var task = session.TestAsync(); Assert.True(session.IsChecking);
        session.Edit(session.Draft with { SourceUrls = "https://dev.azure.com/org/new" });
        pending.SetResult(Snapshot());
        await task;
        Assert.False(session.HasVerifiedAccount); Assert.Null(session.Snapshot); Assert.False(session.IsChecking);
    }
    [Fact]
    public async Task FailedCheckCannotFinishAndSavedSettingsStayUnchanged()
    {
        var saves = 0;
        using var session = Session((_, _, _) => throw new ReviewException("Synthetic access denied"),
            (_, _, _) => { saves++; return Task.CompletedTask; });
        session.Begin(Settings); await session.TestAsync();
        Assert.Contains("access denied", session.Error); Assert.False(await session.CompleteAsync());
        Assert.Equal(0, saves); Assert.Null(session.Snapshot); Assert.False(session.HasVerifiedAccount);
    }
    [Fact]
    public async Task InvalidSourcesOrPreferencesCannotAdvance()
    {
        using var session = Session(); session.Begin(Settings);
        await session.AdvanceAsync(); session.Edit(session.Draft with { SourceUrls = "" });
        await session.AdvanceAsync(); Assert.Equal(SetupStep.Sources, session.Step); Assert.NotNull(session.Error);
        session.Edit(Settings); await session.AdvanceAsync(); await session.TestAsync();
        await session.AdvanceAsync(); await session.AdvanceAsync();
        session.Edit(session.Draft with { RefreshMinutes = 2 });
        await session.AdvanceAsync(); Assert.Equal(SetupStep.Preferences, session.Step); Assert.NotNull(session.Error);
    }
    [Fact]
    public void ProgressCorruptionReportsErrorWithoutChangingSavedSettings()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Progress.FilePath, "{broken");
        using var session = Session();
        Assert.NotNull(session.Error); Assert.Equal(Settings, session.Draft);
        session.Begin(Settings); Assert.Equal(SetupStep.Welcome, session.Step);
    }
    [Fact]
    public void TemporaryPatOverlayCannotMutateCredentialManager()
    {
        var saved = new Pat();
        var overlay = new SetupPatStore("temporary", saved);
        Assert.Equal("temporary", overlay.Load()); Assert.Equal("saved", saved.Load());
        Assert.Throws<ReviewException>(() => overlay.Save("bad"));
        Assert.Throws<ReviewException>(() => overlay.Remove());
        Assert.Equal("saved", new SetupPatStore("", saved).Load());
    }
    [Fact]
    public void SetupAndQueueCacheAreExcludedFromDiagnostics()
    {
        Directory.CreateDirectory(directory);
        new ReviewQueueCache(Path.Combine(directory, "queue-cache.json")).Save(Snapshot());
        Progress.Save(SetupStep.Sources, Settings);
        new PrivateDiagnostics(directory).Export(Path.Combine(directory, "export.zip"));
        using var zip = ZipFile.OpenRead(Path.Combine(directory, "export.zip"));
        Assert.Single(zip.Entries); Assert.Equal("environment.txt", zip.Entries[0].FullName);
    }
    [Fact]
    public async Task CacheRestoresOnlyAfterAuthAndKeepsAgeWhileRefreshLoads()
    {
        var snapshot = Snapshot();
        var cache = new ReviewQueueCache(Path.Combine(directory, "queue-cache.json")); cache.Save(snapshot);
        var loader = new ScopedLoader();
        using var controller = new ReviewController(Settings, loader, _ => { }, cache);
        var refresh = controller.RefreshAsync();
        Assert.Null(controller.Snapshot);
        loader.Authenticated!(snapshot.IdentityScope);
        Assert.True(controller.IsCached); Assert.True(controller.IsRefreshing);
        Assert.Equal(snapshot.FetchedAt, controller.Snapshot!.FetchedAt);
        loader.Result.SetResult(snapshot with { FetchedAt = DateTimeOffset.UtcNow });
        await refresh;
        Assert.False(controller.IsCached); Assert.False(controller.Changes.HasChanges);
        Assert.Null(controller.Error); Assert.Null(controller.CacheWarning);
    }
    [Fact]
    public async Task AuthFailureNeverDisplaysPersistedPrivateQueue()
    {
        var cache = new ReviewQueueCache(Path.Combine(directory, "queue-cache.json")); cache.Save(Snapshot());
        var loader = new ScopedLoader();
        using var controller = new ReviewController(Settings, loader, _ => { }, cache);
        var refresh = controller.RefreshAsync();
        loader.Result.SetException(new ReviewException("Synthetic authentication failed"));
        await refresh;
        Assert.Null(controller.Snapshot); Assert.NotNull(controller.Error);
    }
    [Fact]
    public async Task FailedRefreshPreservesRestoredQueueAndDoesNotReplaceDots()
    {
        var snapshot = Snapshot(); var cache = new ReviewQueueCache(Path.Combine(directory, "queue-cache.json")); cache.Save(snapshot);
        var loader = new ScopedLoader();
        using var controller = new ReviewController(Settings, loader, _ => { }, cache);
        var refresh = controller.RefreshAsync(); loader.Authenticated!(snapshot.IdentityScope);
        loader.Result.SetException(new ReviewException("Synthetic offline"));
        await refresh;
        Assert.Equal(snapshot.Items, controller.Snapshot!.Items);
        Assert.NotNull(controller.Error); Assert.False(controller.Changes.HasChanges);
        Assert.True(controller.IsCached);
    }
    [Fact]
    public async Task SettingsGenerationRejectsOldRefreshAndSeedsVerifiedQueueWithoutExtraRequest()
    {
        var cache = new ReviewQueueCache(Path.Combine(directory, "queue-cache.json")); cache.Save(Snapshot());
        var loader = new ScopedLoader();
        using var controller = new ReviewController(Settings, loader, _ => { }, cache);
        var old = controller.RefreshAsync();
        var replacement = Snapshot("Verified new");
        await controller.ApplyAsync(Settings, verifiedSnapshot: replacement);
        loader.Authenticated!(Snapshot().IdentityScope);
        loader.Result.SetResult(Snapshot("Old"));
        await old;
        Assert.Same(replacement, controller.Snapshot); Assert.Equal(1, loader.Calls);
        Assert.Equal("Verified new", cache.Restore(replacement.IdentityScope, DateTimeOffset.UtcNow)!.Items[0].Title);
    }
    [Fact]
    public async Task SaveFailureKeepsSetupOpenWithActionableErrorAndDraft()
    {
        using var session = Session(save: (_, _, _) => throw new ReviewException("Synthetic disk full"));
        session.Begin(Settings);
        await session.AdvanceAsync(); await session.AdvanceAsync(); await session.TestAsync();
        await session.AdvanceAsync(); await session.AdvanceAsync(); await session.AdvanceAsync();
        Assert.False(await session.CompleteAsync());
        Assert.True(session.IsActive); Assert.True(session.HasVerifiedQueue);
        Assert.Contains("disk full", session.Error); Assert.True(File.Exists(Progress.FilePath));
    }
    [Fact]
    public async Task ClosingDuringConnectionTestCancelsAndClearsToken()
    {
        var started = new TaskCompletionSource();
        using var session = Session(async (_, _, ct) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Snapshot(); });
        session.Begin(Settings); session.Token = "synthetic-token";
        var running = session.TestAsync(); await started.Task;
        session.Pause(); await running;
        Assert.Equal("", session.Token); Assert.Null(session.Snapshot); Assert.False(session.IsChecking);
    }
    [Fact]
    public void ReplayCanKeepExistingPreferencesWithoutSavingDraft()
    {
        var store = new SettingsStore(directory); store.Save(Settings);
        using var session = Session(); session.Begin(Settings);
        session.Edit(session.Draft with { SourceUrls = "https://dev.azure.com/org/changed" });
        session.KeepExisting();
        Assert.Equal(Settings, store.Load()); Assert.False(session.IsActive);
        Assert.False(File.Exists(Progress.FilePath));
    }
    [Fact]
    public async Task CorruptCacheWarnsButDoesNotPreventSuccessfulNetworkQueue()
    {
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "queue-cache.json"), "{broken");
        var loader = new ScopedLoader();
        using var controller = new ReviewController(Settings, loader, _ => { }, new(Path.Combine(directory, "queue-cache.json")));
        var refresh = controller.RefreshAsync(); loader.Authenticated!(Snapshot().IdentityScope);
        Assert.NotNull(controller.CacheWarning);
        loader.Result.SetResult(Snapshot());
        await refresh;
        Assert.NotNull(controller.Snapshot); Assert.Null(controller.CacheWarning);
    }
    private sealed class ScopedLoader : IScopedReviewLoader
    {
        public TaskCompletionSource<ReviewSnapshot> Result { get; } = new();
        public Action<string>? Authenticated { get; private set; }
        public int Calls { get; private set; }
        public Task<ReviewSnapshot> LoadAsync(ReviewSettings settings, CancellationToken ct, Action<string> authenticated)
        { Calls++; Authenticated = authenticated; return Result.Task; }
        public Task<ReviewSnapshot> LoadAsync(ReviewSettings settings, CancellationToken ct) => LoadAsync(settings, ct, _ => { });
        public Task ClearIdentitiesAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Pat : IPatStore
    {
        public string? Load() => "saved";
        public void Save(string token) => throw new InvalidOperationException();
        public void Remove() => throw new InvalidOperationException();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
