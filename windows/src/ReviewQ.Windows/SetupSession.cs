using System.Text.Json;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public enum SetupStep { Welcome, Sources, Authentication, Companions, Preferences, Ready }

public sealed class SetupProgressStore(string directory)
{
    public string FilePath => Path.Combine(directory, "setup-progress.json");
    private sealed record Progress(int Version, SetupStep Step, ReviewSettings Draft);
    public (SetupStep Step, ReviewSettings Draft)? Load()
    {
        if (!File.Exists(FilePath)) return null;
        if (new FileInfo(FilePath).Length > 256 * 1024) throw Corrupt();
        try
        {
            var stored = JsonSerializer.Deserialize<Progress>(File.ReadAllText(FilePath));
            if (stored is null || stored.Version != 1 || !Enum.IsDefined(stored.Step) || stored.Draft is null ||
                stored.Draft.SourceUrls is null || stored.Draft.TenantId is null || stored.Draft.FollowedAuthorEmails is null ||
                stored.Draft.GlobalShortcut is null || stored.Draft.CopilotAdditionalInstructions is null ||
                stored.Draft.ScoutAdditionalInstructions is null || stored.Draft.Reminders is null ||
                stored.Draft.Reminders.Times is null || stored.Draft.Reminders.Weekdays is null ||
                stored.Draft.Reminders.Times.Length > 8 || stored.Draft.Reminders.Times.Any(x => x is null) ||
                !Enum.IsDefined(stored.Draft.Authentication))
                throw Corrupt();
            return (stored.Step > SetupStep.Authentication ? SetupStep.Authentication : stored.Step, stored.Draft);
        }
        catch (JsonException) { throw Corrupt(); }
    }
    public void Save(SetupStep step, ReviewSettings draft)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Progress(1, step, draft));
        if (bytes.Length > 256 * 1024) throw new ReviewException("Setup choices are too large to save. Shorten your source list or preferences.");
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, FilePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Clear() => File.Delete(FilePath);
    private static ReviewException Corrupt() =>
        new("Saved setup progress could not be read. Check your choices before continuing. Saved Settings were not changed.");
}

public sealed class SetupSession : IDisposable
{
    private readonly SetupProgressStore progress;
    private readonly Func<ReviewSettings, string, CancellationToken, Task<ReviewSnapshot>> verify;
    private readonly Func<ReviewSettings, string, ReviewSnapshot, Task> save;
    private readonly Action<DiagnosticRecord> log;
    private CancellationTokenSource check = new();
    private long revision;
    private ReviewSettings? verifiedAccount;
    private ReviewSettings? verifiedQueue;
    private string token = "";
    public event Action? Changed;
    public ReviewSettings Draft { get; private set; }
    public SetupStep Step { get; private set; }
    public ReviewSnapshot? Snapshot { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsChecking { get; private set; }
    public bool IsSaving { get; private set; }
    public string? Error { get; private set; }
    public string? Info { get; private set; }
    public bool HasVerifiedAccount => verifiedAccount is not null && SameAccount(verifiedAccount, Draft);
    public bool HasVerifiedQueue => Snapshot is not null && verifiedQueue is not null && SameQueue(verifiedQueue, Draft);
    public string Token
    {
        get => token;
        set { if (token == value) return; token = value; Invalidate(); Error = null; Changed?.Invoke(); }
    }
    public SetupSession(ReviewSettings settings, SetupProgressStore progress,
        Func<ReviewSettings, string, CancellationToken, Task<ReviewSnapshot>> verify,
        Func<ReviewSettings, string, ReviewSnapshot, Task> save, Action<DiagnosticRecord> log)
    {
        Draft = settings with { };
        this.progress = progress; this.verify = verify; this.save = save; this.log = log;
        try
        {
            if (progress.Load() is { } restored)
            {
                Draft = restored.Draft; Step = restored.Step; IsActive = true;
                Info = "Your setup choices were restored. Test the connection again; an unsaved PAT must be entered again.";
            }
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { Report(ex); }
    }
    public void Begin(ReviewSettings settings)
    {
        if (!IsActive) { Draft = settings with { }; Step = SetupStep.Welcome; Info = null; Invalidate(); }
        else if (Step > SetupStep.Authentication && !HasVerifiedAccount) Step = SetupStep.Authentication;
        IsActive = true;
        Persist();
        Changed?.Invoke();
    }
    public void Edit(ReviewSettings draft)
    {
        if (IsSaving || Draft == draft) return;
        var preserveAccount = SameAccount(Draft, draft);
        if (!SameQueue(Draft, draft)) Invalidate(preserveAccount);
        Draft = draft with { };
        Error = null; Persist(); Changed?.Invoke();
    }
    private static bool SameAccount(ReviewSettings a, ReviewSettings b) =>
        a.SourceUrls == b.SourceUrls && a.Authentication == b.Authentication && a.TenantId == b.TenantId;
    private static bool SameQueue(ReviewSettings a, ReviewSettings b) =>
        SameAccount(a, b) && a.IncludeDrafts == b.IncludeDrafts && a.FollowPeople == b.FollowPeople &&
        a.FollowedAuthorEmails == b.FollowedAuthorEmails && a.ShowAuthoredPullRequests == b.ShowAuthoredPullRequests;
    public void Back()
    {
        if (IsChecking || IsSaving || Step == SetupStep.Welcome) return;
        Step--; Error = null; Info = null; Persist(); Changed?.Invoke();
    }
    public async Task AdvanceAsync()
    {
        if (IsChecking || IsSaving) return;
        Error = null;
        try
        {
            switch (Step)
            {
                case SetupStep.Welcome: Step = SetupStep.Sources; break;
                case SetupStep.Sources:
                    if (!Draft.HasSources) throw new ReviewException("Add at least one Azure DevOps organization or project URL.");
                    _ = Draft.Sources(); Step = SetupStep.Authentication; break;
                case SetupStep.Authentication:
                    if (!HasVerifiedAccount) throw new ReviewException("Test your current connection successfully before continuing.");
                    Step = SetupStep.Companions; break;
                case SetupStep.Companions: Step = SetupStep.Preferences; break;
                case SetupStep.Preferences:
                    Draft.Validate(); _ = ShellIntegration.Parse(Draft.GlobalShortcut);
                    if (!HasVerifiedQueue) await TestAsync();
                    if (HasVerifiedQueue) Step = SetupStep.Ready;
                    break;
                case SetupStep.Ready: return;
            }
            Persist();
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { Report(ex); }
        Changed?.Invoke();
    }
    public async Task TestAsync()
    {
        if (IsChecking || IsSaving) return;
        Error = null; Info = null;
        var settings = Draft with { };
        if (Step == SetupStep.Authentication) settings = settings with { FollowPeople = false, ShowAuthoredPullRequests = false };
        try { settings.Validate(); if (Token.Length > 0) WindowsPatStore.Validate(Token); }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { Report(ex); Changed?.Invoke(); return; }
        Invalidate(true);
        var current = revision;
        var ct = check.Token;
        IsChecking = true; Changed?.Invoke();
        PrivateDiagnostics.Cycle.Value = Guid.NewGuid();
        try
        {
            log(new("refreshStarted", "reviews"));
            var result = await verify(settings, Token, ct);
            if (current != revision || ct.IsCancellationRequested) return;
            Snapshot = result; verifiedQueue = settings; verifiedAccount = settings;
            Info = $"Connection verified. {result.Items.Count} pull requests loaded.";
            log(new("refreshFinished", "reviews", Outcome: "success"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { log(new("refreshCancelled", "reviews", Outcome: "cancelled")); }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        {
            if (current == revision) { Invalidate(); Report(ex); }
            log(DiagnosticRecord.Failure("refreshFailed", "reviews", ex));
        }
        finally
        {
            PrivateDiagnostics.Cycle.Value = null;
            if (current == revision) IsChecking = false;
            Changed?.Invoke();
        }
    }
    public async Task<bool> CompleteAsync()
    {
        if (IsChecking || IsSaving) return false;
        if (Step != SetupStep.Ready || !HasVerifiedQueue || Snapshot is null)
        { Report(new ReviewException("Test your current queue successfully before finishing setup.")); Changed?.Invoke(); return false; }
        IsSaving = true; Changed?.Invoke();
        try
        {
            await save(Draft, Token, Snapshot);
            progress.Clear(); IsActive = false; token = ""; Invalidate(); Error = null;
            return true;
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { Report(ex); return false; }
        finally { IsSaving = false; Changed?.Invoke(); }
    }
    public void Pause()
    {
        Invalidate(); token = "";
        if (Step > SetupStep.Authentication) Step = SetupStep.Authentication;
        Persist(); Changed?.Invoke();
    }
    public void KeepExisting()
    {
        progress.Clear(); Invalidate(); token = ""; IsActive = false;
    }
    private void Invalidate(bool preserveAccount = false)
    {
        revision++; check.Cancel(); check.Dispose(); check = new();
        IsChecking = false; Snapshot = null; verifiedQueue = null;
        if (!preserveAccount) verifiedAccount = null;
    }
    private void Persist()
    {
        if (!IsActive) return;
        try { progress.Save(Step, Draft); }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { Report(ex); }
    }
    private void Report(Exception ex)
    {
        Error = UiErrors.Message(ex);
        log(DiagnosticRecord.Failure("settingsFailed", "settings", ex));
    }
    public void Dispose() { check.Cancel(); check.Dispose(); token = ""; }
}

public sealed class SetupPatStore(string token, IPatStore saved) : IPatStore
{
    public string? Load() => token.Length == 0 ? saved.Load() : token;
    public void Save(string value) => throw new ReviewException("Setup verification cannot save credentials.");
    public void Remove() => throw new ReviewException("Setup verification cannot remove credentials.");
}
