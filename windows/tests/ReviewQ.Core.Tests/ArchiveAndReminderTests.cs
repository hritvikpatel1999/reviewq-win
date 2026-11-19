using System.Text.Json;
using ReviewQ.Core;
using Xunit;

namespace ReviewQ.Core.Tests;

public sealed class ArchiveAndReminderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReviewQ-v4-" + Guid.NewGuid().ToString("N"));
    private string ArchivePath => Path.Combine(directory, "archives.json");
    private const string Scope = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static ReviewItem Item(int number = 1) => new()
    {
        Number = number, Organization = "org", RepositoryId = "repo", Repository = "Private repo",
        Title = "Private title", Author = "Private author", Queue = QueueKind.Assigned
    };
    private static ReviewSnapshot Snapshot(params ReviewItem[] items) => new(items) { IdentityScope = Scope };
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public void ArchiveChoicesContainOnlyIdsAndSurviveRestartAndAbsence()
    {
        var archive = new ArchiveStore(ArchivePath);
        archive.Set(Snapshot(Item()), Item(), true);
        Assert.Contains(Item().Id, archive.Ids(Scope));
        var json = File.ReadAllText(ArchivePath);
        Assert.DoesNotContain("Private", json);
        Assert.DoesNotContain("Title", json);
        var restored = new ArchiveStore(ArchivePath);
        Assert.Contains(Item().Id, restored.Ids(Scope));
        Assert.Empty(restored.Ids(new string('b', 64)));
        Assert.DoesNotContain(Snapshot().Items, item => restored.Ids(Scope).Contains(item.Id));
        restored.Set(Snapshot(Item()), Item(), false);
        Assert.Empty(new ArchiveStore(ArchivePath).Ids(Scope));
        Assert.False(File.Exists(ArchivePath + ".tmp"));
    }

    [Fact]
    public void StaleAndUnscopedActionsCannotHidePrs()
    {
        var archive = new ArchiveStore(ArchivePath);
        Assert.Throws<ReviewException>(() => archive.Set(new ReviewSnapshot([Item()]), Item(), true));
        Assert.Throws<ReviewException>(() => archive.Set(Snapshot(Item()), Item(2), true));
        Assert.Throws<ReviewException>(() => archive.Set(Snapshot(Item() with { Number = -1 }), Item() with { Number = -1 }, true));
        Assert.Empty(archive.Ids(Scope));
        Assert.False(File.Exists(ArchivePath));
    }

    [Fact]
    public void StorageFailureDoesNotPublishArchiveMutation()
    {
        Directory.CreateDirectory(ArchivePath);
        var archive = new ArchiveStore(ArchivePath);
        Assert.Throws<UnauthorizedAccessException>(() => archive.Set(Snapshot(Item()), Item(), true));
        Assert.Empty(archive.Ids(Scope));
        Assert.False(File.Exists(ArchivePath + ".tmp"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":99,\"Scopes\":{}}")]
    [InlineData("{\"Version\":1,\"Scopes\":{\"bad\":[\"org/repo/1\"]}}")]
    [InlineData("{\"Version\":1,\"Scopes\":null}")]
    public void InvalidArchiveChoicesAreClearedWithAnError(string text)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(ArchivePath, text);
        var archive = new ArchiveStore(ArchivePath);
        Assert.NotNull(archive.Error);
        Assert.Empty(archive.Ids(Scope));
        Assert.False(File.Exists(ArchivePath));
    }

    [Fact]
    public void ArchiveLimitsAreGlobalAndRestoringRecoversCapacity()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(ArchivePath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, Scopes = new Dictionary<string, string[]> { [Scope] = Enumerable.Range(1, 4096).Select(n => Item(n).Id).ToArray() }
        }));
        var archive = new ArchiveStore(ArchivePath);
        Assert.Null(archive.Error);
        Assert.Throws<ReviewException>(() => archive.Set(Snapshot(Item(4097)), Item(4097), true));
        Assert.DoesNotContain(Item(4097).Id, archive.Ids(Scope));
        archive.Set(Snapshot(Item()), Item(), false);
        archive.Set(Snapshot(Item(4097)), Item(4097), true);
        Assert.Equal(4096, archive.Ids(Scope).Count);
        File.WriteAllBytes(ArchivePath, new byte[ArchiveStore.MaximumBytes + 1]);
        Assert.NotNull(new ArchiveStore(ArchivePath).Error);
        Assert.False(File.Exists(ArchivePath));
    }

    [Fact]
    public void ArchivedChangesAreExcludedFromBothNetworkBaselines()
    {
        var old = Item();
        var current = old with { Title = "Server edit" };
        var hidden = new HashSet<string> { old.Id };
        Assert.False(new ReviewChanges(Snapshot(old), Snapshot(current), hidden).HasChanges);
        Assert.False(new ReviewChanges(Snapshot(old), Snapshot(), hidden).HasChanges);
        Assert.True(new ReviewChanges(Snapshot(old), Snapshot(current)).HasChanges);
        Assert.Single(Snapshot(current).Items);
    }

    [Fact]
    public void ReminderDefaultsAndRoundTripPreserveOldSettings()
    {
        var settings = JsonSerializer.Deserialize<ReviewSettings>("{}")!;
        Assert.False(settings.Reminders.Enabled);
        Assert.False(settings.Reminders.PlaysSound);
        Assert.Equal(5, settings.Reminders.Weekdays.Length);
        Assert.Equal(new[] { new ReminderTime(10, 30), new ReminderTime(15, 30) }, settings.Reminders.Times);
        Assert.Equal(settings, JsonSerializer.Deserialize<ReviewSettings>(JsonSerializer.Serialize(settings)));
        settings.Reminders.Validate();
    }

    [Fact]
    public void ReminderConfigurationRejectsInvalidAndDuplicateValues()
    {
        var config = new ReminderConfiguration { Enabled = true };
        Assert.Throws<ReviewException>(() => (config with { Weekdays = [] }).Validate());
        Assert.Throws<ReviewException>(() => (config with { Weekdays = [(DayOfWeek)7] }).Validate());
        Assert.Throws<ReviewException>(() => (config with { Times = [] }).Validate());
        Assert.Throws<ReviewException>(() => (config with { Times = [new(24, 0)] }).Validate());
        Assert.Throws<ReviewException>(() => (config with { Times = [new(10, 60)] }).Validate());
        Assert.Throws<ReviewException>(() => (config with { Times = [new(10, 0), new(10, 0)] }).Validate());
        Assert.Throws<ReviewException>(() => (config with { Times = Enumerable.Range(0, 9).Select(h => new ReminderTime(h, 0)).ToArray() }).Validate());
        (config with { Enabled = false, Times = [], Weekdays = [] }).Validate();
        (config with { Times = Enumerable.Range(0, 8).Select(h => new ReminderTime(h, 0)).ToArray() }).Validate();
    }

    [Fact]
    public void ReminderDraftsDoNotRegisterOrRequestPermission()
    {
        var backend = new Backend();
        var controller = new ReminderController(backend, _ => { });
        controller.Apply(new());
        Assert.Equal(0, backend.Preparations);
        Assert.Empty(backend.Schedules);
        var draft = controller.Saved with { Enabled = true, PlaysSound = true };
        Assert.False(controller.Saved.Enabled);
        controller.Apply(draft, true);
        Assert.Single(backend.Schedules);
        Assert.True(controller.IsScheduled);
        Assert.True(backend.Schedules[0].PlaysSound);
        controller.Apply(draft with { Enabled = false }, true);
        Assert.False(controller.IsScheduled);
        Assert.False(backend.Schedules[^1].Enabled);
    }

    [Fact]
    public void BlockedNotificationsRemoveOwnedScheduleAndRemainExplicit()
    {
        var backend = new Backend { Permission = ReminderPermission.Blocked };
        var logs = new List<DiagnosticRecord>();
        var controller = new ReminderController(backend, logs.Add);
        controller.Apply(new() { Enabled = true }, true);
        Assert.False(controller.IsScheduled);
        Assert.Contains("blocked", controller.Error);
        Assert.False(backend.Schedules.Single().Enabled);
        Assert.Contains(logs, entry => entry.Event == "reminderSchedulingFailed");
        backend.Permission = ReminderPermission.Allowed;
        controller.Apply(controller.Saved, true);
        Assert.True(controller.IsScheduled);
        Assert.Null(controller.Error);
    }

    [Fact]
    public void TestNotificationUsesDraftSoundWithoutSavingSchedule()
    {
        var backend = new Backend();
        var controller = new ReminderController(backend, _ => { });
        controller.SendTest(true);
        Assert.True(controller.TestRequested);
        Assert.True(backend.Sound);
        Assert.Empty(backend.Schedules);
        Assert.False(controller.Saved.Enabled);
        Assert.False(controller.Saved.PlaysSound);
    }

    [Fact]
    public void ReminderFailuresHaveDistinctPrivateNumericDiagnostics()
    {
        var backend = new Backend { Error = new System.Runtime.InteropServices.COMException("private payload", unchecked((int)0x80070005)) };
        var logs = new List<DiagnosticRecord>();
        var controller = new ReminderController(backend, logs.Add);
        controller.CheckPermission();
        controller.SendTest(false);
        controller.Apply(new() { Enabled = true }, true);
        Assert.Equal(new[] { "reminderAuthorizationFailed", "reminderTestFailed", "reminderSchedulingFailed" }, logs.Select(x => x.Event));
        Assert.All(logs, entry => { Assert.Equal(unchecked((int)0x80070005), entry.SystemCode); Assert.Equal("reminders", entry.Operation); });
        Assert.DoesNotContain("private payload", controller.Error);
    }

    [Fact]
    public void FirstToastCanInitializeWindowsWithoutTreatingAnUnknownProfileAsDenied()
    {
        var backend = new Backend { Permission = ReminderPermission.NotInitialized };
        var controller = new ReminderController(backend, _ => { });
        controller.CheckPermission();
        Assert.Equal(ReminderPermission.NotInitialized, controller.Permission);
        Assert.Null(controller.Error);
        Assert.Empty(backend.Schedules);
        controller.SendTest(false);
        Assert.True(controller.TestRequested);
        Assert.False(controller.Saved.Enabled);
        controller.Apply(new() { Enabled = true }, true);
        Assert.True(controller.IsScheduled);
        Assert.True(backend.Schedules.Single().Enabled);
    }

    private sealed class Backend : IReminderBackend
    {
        public ReminderPermission Permission { get; set; } = ReminderPermission.Allowed;
        public Exception? Error { get; set; }
        public int Preparations { get; private set; }
        public List<ReminderConfiguration> Schedules { get; } = [];
        public bool Sound { get; private set; }
        public void Prepare() { Preparations++; if (Error is not null) throw Error; }
        public void ReplaceSchedule(ReminderConfiguration configuration) => Schedules.Add(configuration);
        public void SendTest(bool sound) => Sound = sound;
    }
}
