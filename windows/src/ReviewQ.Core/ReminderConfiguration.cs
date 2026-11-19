namespace ReviewQ.Core;

public sealed record ReminderTime(int Hour, int Minute)
{
    public int MinuteOfDay => Hour * 60 + Minute;
    public void Validate()
    {
        if (Hour is < 0 or > 23 || Minute is < 0 or > 59) throw new ReviewException("Choose a valid hour and minute for every reminder.");
    }
    public override string ToString() => $"{Hour:00}:{Minute:00}";
}
public sealed record ReminderConfiguration
{
    public bool Enabled { get; init; }
    public DayOfWeek[] Weekdays { get; init; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public ReminderTime[] Times { get; init; } = [new(10, 30), new(15, 30)];
    public bool PlaysSound { get; init; }
    public void Validate()
    {
        if (Weekdays is null || Weekdays.Any(x => !Enum.IsDefined(x)) || Weekdays.Distinct().Count() != Weekdays.Length ||
            (Enabled && Weekdays.Length == 0)) throw new ReviewException("Select at least one valid day for review reminders.");
        if (Times is null || Times.Any(x => x is null) || Times.Length > 8)
            throw new ReviewException("Choose up to eight reminder times per selected day.");
        if (Enabled && Times.Length == 0) throw new ReviewException("Add at least one reminder time.");
        foreach (var time in Times) time.Validate();
        if (Times.Distinct().Count() != Times.Length) throw new ReviewException("Choose different times for each reminder.");
    }
    public bool SameSchedule(ReminderConfiguration other) => Enabled == other.Enabled && PlaysSound == other.PlaysSound &&
        Weekdays.Order().SequenceEqual(other.Weekdays.Order()) && Times.OrderBy(x => x.MinuteOfDay).SequenceEqual(other.Times.OrderBy(x => x.MinuteOfDay));
    public bool Equals(ReminderConfiguration? other) => other is not null && SameSchedule(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled); hash.Add(PlaysSound);
        foreach (var day in Weekdays.Order()) hash.Add(day);
        foreach (var time in Times.OrderBy(x => x.MinuteOfDay)) hash.Add(time);
        return hash.ToHashCode();
    }
}
public enum ReminderPermission { NotConfigured, Allowed, Blocked, Unavailable, NotInitialized }
public interface IReminderBackend
{
    ReminderPermission Permission { get; }
    void Prepare();
    void ReplaceSchedule(ReminderConfiguration configuration);
    void SendTest(bool sound);
}
public sealed class ReminderController(IReminderBackend backend, Action<DiagnosticRecord> log)
{
    public event Action? Changed;
    public string? Error { get; private set; }
    public bool IsScheduled { get; private set; }
    public bool TestRequested { get; private set; }
    public ReminderConfiguration Saved { get; private set; } = new();
    public ReminderPermission Permission { get; private set; } = ReminderPermission.NotConfigured;
    public void Apply(ReminderConfiguration configuration, bool force = false)
    {
        configuration.Validate();
        Saved = configuration;
        if (!force && !configuration.Enabled && Permission == ReminderPermission.NotConfigured) return;
        Run("reminderSchedulingFailed", () =>
        {
            if (configuration.Enabled)
            {
                backend.Prepare();
                Permission = backend.Permission;
                if (!CanNotify)
                {
                    backend.ReplaceSchedule(configuration with { Enabled = false });
                    throw Blocked();
                }
            }
            backend.ReplaceSchedule(configuration);
            IsScheduled = configuration.Enabled;
            log(new("remindersScheduled", "reminders", Outcome: "success"));
        });
    }
    public void CheckPermission() => Run("reminderAuthorizationFailed", () =>
    {
        backend.Prepare();
        Permission = backend.Permission;
        if (!CanNotify) throw Blocked();
    });
    public void SendTest(bool sound) => Run("reminderTestFailed", () =>
    {
        TestRequested = false;
        backend.Prepare();
        Permission = backend.Permission;
        if (!CanNotify) throw Blocked();
        backend.SendTest(sound);
        Permission = backend.Permission;
        TestRequested = true;
        log(new("reminderTestRequested", "reminders", Outcome: "success"));
    });
    private bool CanNotify => Permission is ReminderPermission.Allowed or ReminderPermission.NotInitialized;
    private void Run(string failureEvent, Action action)
    {
        try { Error = null; action(); }
        catch (Exception ex) when (ex is ReviewException or IOException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            Error = ex is ReviewException ? ex.Message : "Windows could not complete the reminder operation. Check notification and Task Scheduler permissions, then retry.";
            if (failureEvent == "reminderSchedulingFailed") IsScheduled = false;
            try { log(DiagnosticRecord.Failure(failureEvent, "reminders", ex)); }
            catch (Exception logging) when (logging is IOException or UnauthorizedAccessException)
            { Error += " Reminder diagnostic logging also failed."; }
        }
        Changed?.Invoke();
    }
    private static ReviewException Blocked() => new("Windows notifications are blocked or unavailable. Allow ReviewQ in Windows Settings > System > Notifications, then retry.");
}
