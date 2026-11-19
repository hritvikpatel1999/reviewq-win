using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewQ.Core;

public sealed class UpdateStore
{
    public const int MaximumCacheBytes = 32 * 1024;
    private sealed record SavedState
    {
        public int Schema { get; init; } = 1;
        public AvailableUpdate? Release { get; init; }
        public string? EntityTag { get; init; }
        public string? DismissedVersion { get; init; }
        public DateTimeOffset? LastCheckedAt { get; init; }
        public DateTimeOffset? NextCheckAt { get; init; }
        public DateTimeOffset? RetryNotBefore { get; init; }
        public int FailureCount { get; init; }
    }
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string path;
    private readonly IUpdateClient client;
    private readonly Action<DiagnosticRecord> log;
    private readonly Func<DateTimeOffset> clock;
    private readonly ReleaseVersion? installed;
    private SavedState saved = new();
    public event Action? Changed;
    public bool IsChecking { get; private set; }
    public string? Error { get; private set; }
    public string? CurrentVersion => installed?.ToString();
    public DateTimeOffset? LastCheckedAt => saved.LastCheckedAt;
    public AvailableUpdate? AvailableUpdate => installed is { } version && saved.Release is { } release &&
        ReleaseVersion.Parse(release.Version).CompareTo(version) > 0 ? release : null;
    public AvailableUpdate? BannerUpdate => AvailableUpdate is { } release && release.Version != saved.DismissedVersion ? release : null;

    public UpdateStore(string path, string currentVersion, IUpdateClient client, Action<DiagnosticRecord> log,
        Func<DateTimeOffset>? clock = null)
    {
        this.path = path; this.client = client; this.log = log; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        Restore();
        try { installed = ReleaseVersion.Parse(currentVersion); }
        catch (UpdateCheckException ex) { Error = ex.Message; Log("updateCheckFailed", "invalidResponse"); }
    }
    public void DismissUpdate()
    {
        if (AvailableUpdate is not { } release) return;
        saved = saved with { DismissedVersion = release.Version };
        Persist();
        Changed?.Invoke();
    }
    public async Task CheckAsync(bool manually = false, CancellationToken ct = default)
    {
        if (installed is null || IsChecking || ct.IsCancellationRequested) return;
        var now = clock();
        if (saved.RetryNotBefore > now)
        {
            if (manually) { Error = $"Update checks are paused by GitHub. Try again after {saved.RetryNotBefore:g}."; Changed?.Invoke(); }
            return;
        }
        if (!manually && saved.NextCheckAt > now) return;
        IsChecking = true;
        Error = null;
        Log("updateCheckStarted");
        Changed?.Invoke();
        try
        {
            var result = await client.CheckAsync(saved.EntityTag, ct);
            ct.ThrowIfCancellationRequested();
            if (result.NotModified && (saved.LastCheckedAt is null || saved.EntityTag is null))
                throw UpdateCheckException.InvalidResponse();
            if (!result.NotModified && result.EntityTag is not null && !GitHubUpdateClient.ValidEntityTag(result.EntityTag))
                throw UpdateCheckException.InvalidResponse();
            saved = saved with
            {
                Release = result.NotModified ? saved.Release : result.Release,
                EntityTag = result.NotModified ? saved.EntityTag : result.EntityTag,
                LastCheckedAt = clock(), NextCheckAt = clock().AddDays(1), RetryNotBefore = null, FailureCount = 0
            };
            Persist();
            Log("updateCheckFinished", "success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { Log("updateCheckCancelled", "cancelled"); }
        catch (Exception ex) when (ex is UpdateCheckException or HttpRequestException or JsonException or OperationCanceledException)
        {
            var failures = Math.Min(saved.FailureCount + 1, 8);
            var next = clock().AddMinutes(Math.Min(1440, 15 * Math.Pow(2, failures - 1)));
            var retry = (ex as UpdateCheckException)?.RetryAt;
            saved = saved with { FailureCount = failures, NextCheckAt = retry > next ? retry : next, RetryNotBefore = retry };
            Error = ex is UpdateCheckException ? ex.Message : ex is JsonException ?
                UpdateCheckException.InvalidResponse().Message : "Could not check for ReviewQ updates. Check your connection and try again.";
            Persist();
            Log("updateCheckFailed", ex is HttpRequestException or OperationCanceledException ? "network" : "invalidResponse");
        }
        finally { IsChecking = false; Changed?.Invoke(); }
    }
    private void Restore()
    {
        try
        {
            if (!File.Exists(path)) return;
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length > MaximumCacheBytes) throw UpdateCheckException.InvalidResponse();
                var state = JsonSerializer.Deserialize<SavedState>(stream, Options);
                if (state is null || state.Schema != 1 || state.FailureCount is < 0 or > 8 || state.LastCheckedAt > clock() ||
                    ((state.Release is not null || state.EntityTag is not null) && state.LastCheckedAt is null) ||
                    (state.EntityTag is not null && !GitHubUpdateClient.ValidEntityTag(state.EntityTag)))
                    throw UpdateCheckException.InvalidResponse();
                if (state.DismissedVersion is not null) _ = ReleaseVersion.Parse(state.DismissedVersion);
                saved = state;
            }
        }
        catch (Exception ex) when (ex is JsonException or UpdateCheckException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Error = "Saved update information could not be restored. Check for updates again.";
            Log("updateCacheInvalidated", "system");
            try { File.Delete(path); }
            catch (Exception cleanup) when (StorageFailure(cleanup))
            { Error += " Windows could not clear the update cache. Check local storage permissions."; }
        }
    }
    private void Persist()
    {
        var temp = path + ".tmp";
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(saved);
            if (data.Length > MaximumCacheBytes) throw new IOException("Update cache exceeds its size limit.");
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (StorageFailure(ex))
        { Error = (Error is null ? "" : Error + " ") + "Update information could not be saved. Check local storage permissions."; Log("updateCacheFailed", "system"); }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (StorageFailure(ex))
            { Error = (Error is null ? "" : Error + " ") + "Temporary update cache cleanup failed."; Log("updateCacheFailed", "system"); }
        }
    }
    private static bool StorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private void Log(string name, string? outcome = null)
    {
        try { log(new(name, "appUpdates", Outcome: outcome)); }
        catch (Exception ex) when (StorageFailure(ex))
        { Error = (Error is null ? "" : Error + " ") + "Update diagnostic logging failed."; }
    }
}
