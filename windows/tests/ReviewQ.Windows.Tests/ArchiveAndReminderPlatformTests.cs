using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ReviewQ.Core;
using ReviewQ.Windows;
using Xunit;

namespace ReviewQ.Windows.Tests;

public sealed class ArchiveAndReminderPlatformTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReviewQ-v4-platform-" + Guid.NewGuid().ToString("N"));
    private static ReviewSettings Settings => new() { SourceUrls = "https://dev.azure.com/org/project" };
    private const string Scope = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static ReviewItem Item(int n = 1, QueueKind queue = QueueKind.Assigned) => new()
    {
        Number = n, Organization = "org", RepositoryId = "repo", Repository = "repository", Queue = queue,
        Title = "Synthetic PR", ChangeFingerprint = "old", CreatedAt = DateTimeOffset.UtcNow
    };
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task ArchivesKeepRawCacheAndBaselineWhileUpdatingAllCounts()
    {
        var first = Item();
        var loader = new Loader { Snapshot = new([first, Item(2, QueueKind.Followed), Item(3, QueueKind.Authored)]) { IdentityScope = Scope } };
        var cache = new ReviewQueueCache(Path.Combine(directory, "queue-cache.json"));
        var archives = new ArchiveStore(Path.Combine(directory, "archives.json"));
        using var controller = new ReviewController(Settings, loader, _ => { }, cache, archives);
        await controller.RefreshAsync();
        controller.SetArchived(first, true);
        Assert.Equal(0, controller.PendingCount);
        Assert.Equal(2, controller.ActiveItems.Count);
        Assert.Single(controller.ArchivedItems);
        Assert.Equal(3, controller.Snapshot!.Items.Count);
        Assert.Equal(3, cache.Restore(Scope, DateTimeOffset.UtcNow)!.Items.Count);
        Assert.Equal(1, loader.Calls);
        Assert.False(controller.Changes.HasChanges);
        loader.Snapshot = loader.Snapshot with { Items = [first with { ChangeFingerprint = "new" }, .. loader.Snapshot.Items.Skip(1)] };
        await controller.RefreshAsync();
        Assert.False(controller.Changes.HasChanges);
        controller.SetArchived(loader.Snapshot.Items[0], false);
        Assert.Equal(1, controller.PendingCount);
        Assert.True(controller.Changes.HasChanges);
        Assert.Equal(2, loader.Calls);
        Assert.Empty(controller.ArchivedItems);
    }

    [Fact]
    public async Task ArchiveChoicesAreScopedAndAbsentPrsCanReturn()
    {
        var item = Item();
        var original = new ReviewSnapshot([item]) { IdentityScope = Scope };
        var loader = new Loader { Snapshot = original };
        var path = Path.Combine(directory, "archives.json");
        using var controller = new ReviewController(Settings, loader, _ => { }, archives: new ArchiveStore(path));
        await controller.RefreshAsync();
        controller.SetArchived(item, true);
        loader.Snapshot = original with { Items = [] };
        await controller.RefreshAsync();
        Assert.Empty(controller.ArchivedItems);
        loader.Snapshot = original;
        await controller.RefreshAsync();
        Assert.Single(controller.ArchivedItems);
        loader.Snapshot = original with { IdentityScope = new string('b', 64) };
        await controller.RefreshAsync();
        Assert.Empty(controller.ArchivedItems);
        Assert.Single(controller.ActiveItems);
        using var restored = new ReviewController(Settings, new Loader { Snapshot = original }, _ => { }, archives: new ArchiveStore(path));
        await restored.RefreshAsync();
        Assert.Single(restored.ArchivedItems);
        Assert.Empty(restored.ActiveItems);
    }

    [Fact]
    public void ScheduledTaskIsPerUserWeeklyLocalAndContainsNoPrData()
    {
        var config = new ReminderConfiguration
        {
            Enabled = true, Weekdays = [DayOfWeek.Monday, DayOfWeek.Friday], Times = [new(15, 30), new(10, 30)]
        };
        var xml = WindowsReminders.ScheduleXml(config, @"C:\Program Files\ReviewQ & Test\ReviewQ.exe", "S-1-5-21-test");
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var triggers = document.Descendants(ns + "CalendarTrigger").ToArray();
        Assert.Equal(2, triggers.Length);
        Assert.Equal("2000-01-03T10:30:00", triggers[0].Element(ns + "StartBoundary")!.Value);
        Assert.Equal(new[] { "Monday", "Friday" }, triggers[0].Descendants(ns + "DaysOfWeek").Single().Elements().Select(x => x.Name.LocalName));
        Assert.Equal("InteractiveToken", document.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", document.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("--deliver-reminder", document.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\Program Files\ReviewQ & Test\ReviewQ.exe", document.Descendants(ns + "Command").Single().Value);
        Assert.Equal("false", document.Descendants(ns + "WakeToRun").Single().Value);
        Assert.Equal("false", document.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.DoesNotContain("azure", xml, StringComparison.OrdinalIgnoreCase);
        Assert.True(WindowsReminders.IsOwnedSchedule(xml, @"C:\Program Files\ReviewQ & Test\ReviewQ.exe"));
        Assert.False(WindowsReminders.IsOwnedSchedule(xml, @"C:\Different\ReviewQ.exe"));
        Assert.False(WindowsReminders.IsOwnedSchedule(xml.Replace("--deliver-reminder", "--unrelated"), @"C:\Program Files\ReviewQ & Test\ReviewQ.exe"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToastIsGenericAndAlwaysOpensQueueByProtocol(bool sound)
    {
        var xml = WindowsReminders.ToastXml(sound);
        var toast = XElement.Parse(xml);
        Assert.Equal("protocol", toast.Attribute("activationType")!.Value);
        Assert.Equal("reviewq-reminder:queue", toast.Attribute("launch")!.Value);
        Assert.Equal(new[] { "Review reminder", "Take a moment to check your ReviewQ." }, toast.Descendants("text").Select(x => x.Value));
        Assert.Equal(sound ? null : "true", toast.Element("audio")!.Attribute("silent")?.Value);
        Assert.DoesNotContain("pending", xml);
        Assert.DoesNotContain("https", xml);
    }

    [Theory]
    [InlineData("--reminder-open", "reviewq-reminder:queue", true)]
    [InlineData("--reminder-open", "REVIEWQ-REMINDER:queue", true)]
    [InlineData("--reminder-open", "reviewq-reminder:other", false)]
    [InlineData("--reminder-open", "https://example.com", false)]
    [InlineData("--other", "reviewq-reminder:queue", false)]
    public void QueueActivationRequiresTheOwnedProtocolAndAction(string flag, string uri, bool expected)
    {
        Assert.Equal(expected, WindowsReminders.IsQueueActivation([flag, uri]));
        Assert.False(WindowsReminders.IsQueueActivation([]));
        Assert.False(WindowsReminders.IsQueueActivation([flag]));
        Assert.False(WindowsReminders.IsQueueActivation([flag, uri, "--diagnostics"]));
    }

    [Fact]
    public void NativeTaskRegistrationIsValidatedWhenExplicitlyRequested()
    {
        if (Environment.GetEnvironmentVariable("REVIEWQ_VALIDATE_NATIVE_REMINDERS") != "1") return;
        var name = "ReviewQ-Validation-" + Guid.NewGuid().ToString("N");
        var time = DateTime.Now.AddHours(12);
        try
        {
            WindowsReminders.RegisterSchedule(new() { Enabled = true, Weekdays = Enum.GetValues<DayOfWeek>(), Times = [new(time.Hour, time.Minute)] }, name);
            object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            object? folder = null, task = null;
            try
            {
                dynamic scheduler = service; scheduler.Connect();
                folder = scheduler.GetFolder("\\"); dynamic root = folder; task = root.GetTask(name);
                dynamic registered = task;
                var xml = (string)registered.Xml;
                Assert.Contains("--deliver-reminder", xml);
                Assert.Contains("InteractiveToken", xml);
            }
            finally
            {
                if (task is not null) Marshal.FinalReleaseComObject(task);
                if (folder is not null) Marshal.FinalReleaseComObject(folder);
                Marshal.FinalReleaseComObject(service);
            }
        }
        finally { WindowsReminders.RegisterSchedule(new() { Enabled = false }, name); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"invalid\"")]
    [InlineData("{\"Enabled\":true,\"Weekdays\":[]}")]
    [InlineData("{\"Weekdays\":[99]}")]
    [InlineData("{\"Times\":null}")]
    [InlineData("{\"Times\":[{\"Hour\":10,\"Minute\":30},{\"Hour\":10,\"Minute\":30}]}")]
    public void InvalidRemindersDisableOnlyRemindersAndPreserveSavedAccount(string invalid)
    {
        var store = new SettingsStore(directory);
        var original = Settings with { ShowAuthoredPullRequests = true, CopilotAdditionalInstructions = "Keep this preference." };
        store.Save(original);
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        json["Settings"]!["Reminders"] = JsonNode.Parse(invalid);
        var saved = json.ToJsonString();
        File.WriteAllText(store.FilePath, saved);
        Assert.Equal(original, store.Load());
        Assert.Contains("Reminders are off", store.LoadWarning);
        Assert.Equal(saved, File.ReadAllText(store.FilePath));
        store.Save(original);
        Assert.Equal(original, store.Load());
        Assert.Null(store.LoadWarning);
    }

    private sealed class Loader : IReviewLoader
    {
        public ReviewSnapshot Snapshot { get; set; } = new([]);
        public int Calls { get; private set; }
        public Task<ReviewSnapshot> LoadAsync(ReviewSettings settings, CancellationToken cancellationToken) { Calls++; return Task.FromResult(Snapshot); }
        public Task ClearIdentitiesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
