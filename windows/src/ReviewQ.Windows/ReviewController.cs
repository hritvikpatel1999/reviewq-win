using System.ComponentModel;
using System.Runtime.CompilerServices;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public sealed class ReviewController : INotifyPropertyChanged, IDisposable
{
    private readonly IReviewLoader loader;
    private readonly Action<DiagnosticRecord> log;
    private CancellationTokenSource generation = new();
    private CancellationTokenSource? polling;
    private long revision;
    private readonly ReviewQueueCache? cache;
    private readonly ArchiveStore? archives;
    private ReviewSnapshot? previousSnapshot;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ReviewSettings Settings { get; private set; }
    public ReviewSnapshot? Snapshot { get; private set; }
    public bool IsRefreshing { get; private set; }
    public string? Error { get; private set; }
    public string? CacheWarning { get; private set; }
    public bool IsCached { get; private set; }
    public ReviewChanges Changes { get; private set; } = new();
    public string? ArchiveError { get; private set; }
    public IReadOnlyList<ReviewItem> ActiveItems
    {
        get { var ids = ArchivedIds; return Snapshot?.Items.Where(x => !ids.Contains(x.Id)).ToArray() ?? []; }
    }
    public IReadOnlyList<ReviewItem> ArchivedItems
    {
        get { var ids = ArchivedIds; return Snapshot?.Items.Where(x => ids.Contains(x.Id))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray() ?? []; }
    }
    public int PendingCount => ActiveItems.Count(x => x.Queue == QueueKind.Assigned);
    private IReadOnlySet<string> ArchivedIds => archives?.Ids(Snapshot?.IdentityScope) ?? new HashSet<string>();
    public ReviewController(ReviewSettings settings, IReviewLoader loader, Action<DiagnosticRecord> log,
        ReviewQueueCache? cache = null, ArchiveStore? archives = null)
    {
        Settings = settings; this.loader = loader; this.log = log; this.cache = cache; this.archives = archives;
        ArchiveError = archives?.Error;
        if (ArchiveError is not null) Log(new("archiveStateInvalidated", "archives", Outcome: "invalidResponse"));
    }
    public void SetArchived(ReviewItem item, bool archived)
    {
        try
        {
            if (archives is null || Snapshot is null) throw new ReviewException("Refresh an authenticated queue before archiving PRs.");
            archives.Set(Snapshot, item, archived);
            ArchiveError = null;
            RecomputeChanges();
            Log(new(archived ? "reviewArchived" : "reviewRestored", "archives", Outcome: "success"));
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        { ArchiveError = UiErrors.Message(ex); Log(DiagnosticRecord.Failure("archiveChangeFailed", "archives", ex)); }
        Changed();
    }
    private void RecomputeChanges() => Changes = new(previousSnapshot, Snapshot, ArchivedIds);
    public void Report(string message) { Error = message; Changed(); }
    public void Start(bool refreshImmediately = true)
    {
        polling?.Cancel();
        polling?.Dispose();
        polling = new();
        _ = PollAsync(polling.Token, refreshImmediately);
    }
    private async Task PollAsync(CancellationToken ct, bool refreshImmediately)
    {
        try
        {
            if (!refreshImmediately) await Task.Delay(TimeSpan.FromMinutes(Settings.RefreshMinutes), ct);
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync();
                await Task.Delay(TimeSpan.FromMinutes(Settings.RefreshMinutes), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task RefreshAsync()
    {
        if (IsRefreshing || !Settings.HasSources) return;
        var current = revision;
        var ct = generation.Token;
        IsRefreshing = true;
        Changed();
        PrivateDiagnostics.Cycle.Value = Guid.NewGuid();
        try
        {
            Log(new("refreshStarted", "reviews"));
            void Authenticated(string scope)
            {
                if (current != revision || ct.IsCancellationRequested) return;
                if (Snapshot is not null && Snapshot.IdentityScope != scope)
                { Snapshot = null; previousSnapshot = null; Changes = new(); IsCached = false; Changed(); }
                if (Snapshot is null && cache is not null)
                {
                    try
                    {
                        Snapshot = cache.Restore(scope, DateTimeOffset.UtcNow);
                        IsCached = Snapshot is not null;
                        Changed();
                    }
                    catch (Exception ex) when (UiErrors.IsExpected(ex))
                    { CacheWarning = "Startup cache could not be restored. " + UiErrors.Message(ex); Changed(); }
                }
            }
            var snapshot = loader is IScopedReviewLoader scoped
                ? await scoped.LoadAsync(Settings with { }, ct, Authenticated)
                : await loader.LoadAsync(Settings with { }, ct);
            if (current != revision || ct.IsCancellationRequested) return;
            previousSnapshot = Snapshot;
            Snapshot = snapshot;
            RecomputeChanges();
            IsCached = false;
            Error = null;
            if (cache is not null)
            {
                try
                {
                    CacheWarning = cache.Save(snapshot) ? null :
                        "This queue is too large to cache. Live refresh still works; no partial queue was saved.";
                }
                catch (Exception ex) when (UiErrors.IsExpected(ex))
                { CacheWarning = "The queue refreshed, but the startup cache could not be saved. " + UiErrors.Message(ex); }
            }
            Log(new("refreshFinished", "reviews", Outcome: "success"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { Log(new("refreshCancelled", "reviews", Outcome: "cancelled")); }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        {
            if (current == revision) Error = UiErrors.Message(ex);
            Log(DiagnosticRecord.Failure("refreshFailed", "reviews", ex));
        }
        catch (Exception)
        {
            if (current == revision) Error = "An unexpected refresh error occurred. Your last successful queue is retained. Restart ReviewQ and retry; report the issue if it persists.";
            Log(new("refreshFailed", "reviews", Outcome: "system"));
        }
        finally
        {
            PrivateDiagnostics.Cycle.Value = null;
            if (current == revision) { IsRefreshing = false; Changed(); }
        }
    }
    public async Task ApplyAsync(ReviewSettings settings, bool clearIdentities = false, ReviewSnapshot? verifiedSnapshot = null)
    {
        ClearStartupCache();
        generation.Cancel();
        generation.Dispose();
        generation = new();
        revision++;
        IsRefreshing = false;
        Settings = settings;
        Snapshot = verifiedSnapshot;
        previousSnapshot = null;
        Error = null;
        IsCached = false;
        Changes = new();
        Changed();
        if (clearIdentities) await loader.ClearIdentitiesAsync(generation.Token);
        if (verifiedSnapshot is not null && cache is not null)
        {
            try
            {
                CacheWarning = cache.Save(verifiedSnapshot) ? null :
                    "This queue is too large to cache. Live refresh still works; no partial queue was saved.";
            }
            catch (Exception ex) when (UiErrors.IsExpected(ex))
            { CacheWarning = "Your queue is ready, but the startup cache could not be saved. " + UiErrors.Message(ex); }
            Changed();
        }
        Start(verifiedSnapshot is null);
    }
    public void ClearStartupCache()
    {
        cache?.Clear();
        CacheWarning = null;
    }
    public void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Log(DiagnosticRecord record)
    {
        try { log(record); }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        { Error = "Diagnostic logging failed. " + UiErrors.Message(ex); Changed(); }
    }
    public void Dispose()
    {
        polling?.Cancel();
        generation.Cancel();
        polling?.Dispose();
        generation.Dispose();
    }
}

public static class UiErrors
{
    public static bool IsExpected(Exception ex) => ex is ReviewException or IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or System.Security.SecurityException or System.Net.Http.HttpRequestException;
    public static string Message(Exception ex) => ex is ReviewException ? ex.Message :
        ex is UnauthorizedAccessException or System.Security.SecurityException ? "Windows denied access to local storage or a system setting. Check your account permissions." :
        ex is IOException ? "Local storage could not be read or written. Check disk space and file permissions." :
        "A Windows operation failed. Check the selected application and your connection, then retry.";
}
