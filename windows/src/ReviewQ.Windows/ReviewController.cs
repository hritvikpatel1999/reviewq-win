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
    public event PropertyChangedEventHandler? PropertyChanged;
    public ReviewSettings Settings { get; private set; }
    public ReviewSnapshot? Snapshot { get; private set; }
    public bool IsRefreshing { get; private set; }
    public string? Error { get; private set; }
    public ReviewController(ReviewSettings settings, IReviewLoader loader, Action<DiagnosticRecord> log)
    { Settings = settings; this.loader = loader; this.log = log; }
    public void Report(string message) { Error = message; Changed(); }
    public void Start()
    {
        polling?.Cancel();
        polling?.Dispose();
        polling = new();
        _ = PollAsync(polling.Token);
    }
    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
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
            var snapshot = await loader.LoadAsync(Settings with { }, ct);
            if (current != revision || ct.IsCancellationRequested) return;
            Snapshot = snapshot;
            Error = null;
            Log(new("refreshFinished", "reviews", Outcome: "success"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { Log(new("refreshCancelled", "reviews", Outcome: "cancelled")); }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        {
            if (current == revision) Error = UiErrors.Message(ex);
            Log(new("refreshFailed", "reviews", Outcome: "system"));
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
    public async Task ApplyAsync(ReviewSettings settings, bool clearIdentities = false)
    {
        generation.Cancel();
        generation.Dispose();
        generation = new();
        revision++;
        IsRefreshing = false;
        Settings = settings;
        Snapshot = null;
        Error = null;
        Changed();
        if (clearIdentities) await loader.ClearIdentitiesAsync(generation.Token);
        Start();
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
