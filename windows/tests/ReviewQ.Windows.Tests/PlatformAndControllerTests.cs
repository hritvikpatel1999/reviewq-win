using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ReviewQ.Core;
using ReviewQ.Windows;
using Xunit;

namespace ReviewQ.Windows.Tests;

public sealed class PlatformAndControllerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ReviewQ-Tests-" + Guid.NewGuid().ToString("N"));
    private static ReviewSettings Settings => new() { SourceUrls = "https://dev.azure.com/org/project" };

    [Fact]
    public void SettingsRoundTripAndVersionValidation()
    {
        var store = new SettingsStore(directory);
        Assert.False(store.Load().HasSources);
        var settings = Settings with { CopilotAdditionalInstructions = "Review carefully.", ShowAuthoredPullRequests = true };
        store.Save(settings);
        Assert.Equal(settings, store.Load());
        Assert.False(File.Exists(store.FilePath + ".tmp"));
        File.WriteAllText(store.FilePath, "{\"Version\":99,\"Settings\":{}}");
        Assert.Throws<ReviewException>(() => store.Load());
        File.WriteAllText(store.FilePath, "{broken");
        Assert.Throws<ReviewException>(() => store.Load());
    }

    [Fact]
    public void InvalidSettingsDoNotOverwritePreviousSettings()
    {
        var store = new SettingsStore(directory);
        store.Save(Settings);
        Assert.Throws<ReviewException>(() => store.Save(Settings with { RefreshMinutes = 2 }));
        Assert.Equal(Settings, store.Load());
    }

    [Fact]
    public void DiagnosticExportOnlyContainsAllowlistedFieldsAndFiles()
    {
        var logs = new PrivateDiagnostics(directory) { Verbose = true };
        logs.Write(new("secret-token", "sensitive-title", 987, -1, "private-prompt"));
        logs.Write(new("requestFinished", "reviews", 200, 15, "success"));
        logs.Write(new("actionFinished", "scout", Outcome: "success"));
        logs.Write(new("updateCheckFinished", "appUpdates", Outcome: "success"));
        logs.Write(DiagnosticRecord.Failure("reminderTestFailed", "reminders",
            new ReviewException("secret private payload", systemCode: -2147024891, cliExitCode: 7), 12));
        File.WriteAllText(Path.Combine(directory, "settings.json"), "secret-settings");
        File.WriteAllText(Path.Combine(directory, "app-updates.json"), "secret-update-state");
        File.WriteAllText(Path.Combine(directory, "reviewq.log.extra"), "secret-extra");
        var destination = Path.Combine(directory, "export.zip");
        logs.Export(destination);
        using var zip = ZipFile.OpenRead(destination);
        Assert.Equal(new[] { "environment.txt", "reviewq.log" }, zip.Entries.Select(x => x.FullName).Order().ToArray());
        using var reader = new StreamReader(zip.GetEntry("reviewq.log")!.Open());
        var content = reader.ReadToEnd();
        Assert.DoesNotContain("secret", content);
        Assert.DoesNotContain("sensitive", content);
        Assert.DoesNotContain("private", content);
        Assert.Contains("\"operation\":\"scout\"", content);
        Assert.Contains("\"operation\":\"appUpdates\"", content);
        Assert.Contains("\"event\":\"updateCheckFinished\"", content);
        Assert.Contains("\"systemCode\":-2147024891", content);
        Assert.Contains("\"cliExitCode\":7", content);
        Assert.Contains("\"durationMs\":12", content);
        using var first = JsonDocument.Parse(content.Split(Environment.NewLine)[0]);
        Assert.Equal("unknown", first.RootElement.GetProperty("event").GetString());
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("durationMs").ValueKind);
    }

    [Fact]
    public void DiagnosticsRotationIsBounded()
    {
        Directory.CreateDirectory(directory);
        var logs = new PrivateDiagnostics(directory);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(directory, "reviewq.log"), new string('x', 1_048_576));
            logs.Write(new("startup", "application"));
        }
        Assert.Equal(4, Directory.GetFiles(directory).Length);
        Assert.True(new FileInfo(Path.Combine(directory, "reviewq.log")).Length < 1024);
    }

    [Fact]
    public void ClearingLogsPreservesUnrelatedFilesAndLoggingCanResume()
    {
        var logs = new PrivateDiagnostics(directory);
        Assert.Equal(0, logs.StorageBytes);
        logs.Clear();
        Directory.CreateDirectory(directory);
        var names = new[] { "reviewq.log", "reviewq.log.1", "reviewq.log.2", "reviewq.log.3" };
        foreach (var name in names) File.WriteAllText(Path.Combine(directory, name), "log");
        foreach (var name in new[] { "settings.json", "identity-cache.json", "reviewq.log.extra", "export.zip" })
            File.WriteAllText(Path.Combine(directory, name), "preserve");
        var nested = Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(nested.FullName, "reviewq.log"), "preserve");
        Assert.Equal(12, logs.StorageBytes);
        logs.Clear();
        Assert.Equal(0, logs.StorageBytes);
        Assert.All(names, name => Assert.False(File.Exists(Path.Combine(directory, name))));
        Assert.Equal(4, Directory.GetFiles(directory).Length);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(nested.FullName, "reviewq.log")));
        logs.Write(new("startup", "application"));
        Assert.True(logs.StorageBytes > 0);
    }

    [Fact]
    public void RotationHappensBeforeARecordWouldExceedTheFileCap()
    {
        Directory.CreateDirectory(directory);
        var logs = new PrivateDiagnostics(directory);
        var path = Path.Combine(directory, "reviewq.log");
        File.WriteAllText(path, new string('x', (int)PrivateDiagnostics.MaxLogFileBytes - 1));
        logs.Write(new("startup", "application"));
        Assert.True(new FileInfo(path).Length < 1024);
        Assert.True(new FileInfo(path + ".1").Length <= PrivateDiagnostics.MaxLogFileBytes);
    }

    [Fact]
    public void VerboseLoggingOnlyAddsOptionalRequestStarts()
    {
        var logs = new PrivateDiagnostics(directory);
        logs.Write(new("requestStarted", "reviews"));
        Assert.Equal(0, logs.StorageBytes);
        logs.Verbose = true;
        logs.Write(new("requestStarted", "reviews"));
        var previousSize = logs.StorageBytes;
        logs.Verbose = false;
        logs.Write(new("requestStarted", "reviews"));
        Assert.Equal(previousSize, logs.StorageBytes);
        logs.Write(new("requestFinished", "reviews", 200, 1, "success"));
        Assert.True(logs.StorageBytes > previousSize);
        Assert.False(new PrivateDiagnostics(directory).Verbose);
    }

    [Fact]
    public void ClearingAnInUseLogReportsTheStorageFailure()
    {
        var logs = new PrivateDiagnostics(directory);
        logs.Write(new("startup", "application"));
        using (var locked = new FileStream(Path.Combine(directory, "reviewq.log"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(logs.Clear);
        logs.Clear();
        Assert.Equal(0, logs.StorageBytes);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Q", 0x4003, 0x51)]
    [InlineData("Ctrl+Shift+F8", 0x4006, 0x77)]
    [InlineData("", 0, 0)]
    public void ShortcutParser(string text, int modifiers, int key)
    {
        var parsed = ShellIntegration.Parse(text);
        Assert.Equal((uint)modifiers, parsed.Modifiers);
        Assert.Equal((uint)key, parsed.Key);
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("Ctrl+Ctrl+Q")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Cmd+Q")]
    [InlineData("Ctrl+Left")]
    [InlineData("Ctrl+44")]
    public void ShortcutParserRejectsAmbiguousValues(string text) => Assert.Throws<ReviewException>(() => ShellIntegration.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc def")]
    [InlineData("abc\n")]
    public void PatRejectsWhitespace(string token) => Assert.Throws<ReviewException>(() => WindowsPatStore.Validate(token));

    [Fact]
    public async Task MissingCommandReturnsActionableError()
    {
        var exception = await Assert.ThrowsAsync<ReviewException>(() =>
            CommandRunner.RunAsync(Path.Combine(directory, "missing.exe"), [], TimeSpan.FromSeconds(2), default));
        Assert.Contains("installation", exception.Message);
    }

    [Theory]
    [InlineData("ERROR: Please run 'az login' to setup account.", "not signed in")]
    [InlineData("AADSTS50076 interaction_required", "requires you to sign in again")]
    [InlineData("certificate verify failed", "proxy/certificate")]
    [InlineData("No module named azure.cli", "Repair or update")]
    [InlineData("unrecognized failure", "requesting an Azure DevOps access token")]
    public void AzureCliErrorsExplainFailureAndRecoveryWithoutLeakingOutput(string stderr, string expected)
    {
        var error = WindowsAuthorization.AzureCliFailure(1, stderr + "\nsecret-token user@example.com", "");
        Assert.Contains(expected, error.Message);
        Assert.Contains("az login --allow-no-subscriptions", error.Message);
        Assert.Contains("then click Refresh", error.Message);
        Assert.Contains("--query expiresOn", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
        Assert.DoesNotContain("user@example.com", error.Message);
    }

    [Fact]
    public void TenantRecoveryCommandDoesNotExposeConfiguredTenant()
    {
        var error = WindowsAuthorization.AzureCliFailure(1, "Please run az login", "private-tenant.example");
        Assert.Contains("--tenant <tenant-from-ReviewQ-Settings>", error.Message);
        Assert.DoesNotContain("private-tenant.example", error.Message);
    }

    [Theory]
    [InlineData("AADSTS53003: Please run az login")]
    [InlineData("Conditional Access policies do not allow token issuance")]
    [InlineData("Your access policy does not allow access")]
    public void AccessPolicyDenialsAreNotMisreportedAsMissingLogin(string stderr)
    {
        var error = WindowsAuthorization.AzureCliFailure(1, stderr, "");
        Assert.Contains("blocked by your organization's access policy", error.Message);
        Assert.Contains("IT administrator", error.Message);
        Assert.DoesNotContain("not signed in", error.Message);
    }

    [Fact]
    public void WindowsUseSystemThemeAndRenderAccessibleQueueAndDiagnostics()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new ThemeTestApp();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("/ReviewQ;component/UiStyles.xaml", UriKind.Relative)
                });
                VerifyScoutAccessibilityHandoff();
                using var controller = new ReviewController(Settings, new Loader
                {
                    Load = (_, _) => Task.FromResult(new ReviewSnapshot(Enum.GetValues<QueueKind>().SelectMany(kind =>
                        Enumerable.Range(1, 3).Select(number => new ReviewItem
                        {
                            Number = number, Title = $"Review {kind} {number}", Author = "Author", Organization = "org",
                            Project = "project", Repository = number == 2 ? "another" : "repository",
                            RepositoryId = $"{kind}-{number}",
                            Queue = kind, CreatedAt = DateTimeOffset.UtcNow
                        })).ToArray()) { IdentityScope = new string('a', 64) })
                }, _ => { }, archives: new ArchiveStore(Path.Combine(directory, "archives.json")));
                controller.RefreshAsync().GetAwaiter().GetResult();
                var updateClient = new UpdateClient();
                var updates = new UpdateStore(Path.Combine(directory, "app-updates.json"), "3.2.0", updateClient, _ => { });
                var queue = new MainWindow(app, controller, updates);
                var settingsConfiguration = new ReviewSettings
                { SourceUrls = "https://dev.azure.com/contoso/Platform\nhttps://dev.azure.com/contoso/Client" };
                var settings = new SettingsWindow(app, settingsConfiguration, updates);
                var logs = new PrivateDiagnostics(directory);
                var diagnostics = new DiagnosticsWindow(logs);
                queue.Show();
                settings.Show();
                diagnostics.Show();
                settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
#pragma warning disable WPF0001
                Assert.Equal(System.Windows.ThemeMode.System, settings.ThemeMode);
                Assert.Equal(System.Windows.ThemeMode.System, diagnostics.ThemeMode);
#pragma warning restore WPF0001
                Assert.True(settings.IsLoaded);
                Assert.True(diagnostics.IsLoaded);
                var sourceInput = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.TextBox>(),
                    box => System.Windows.Automation.AutomationProperties.GetName(box) == "Organization/project URLs (one per line)");
                var firstCharacter = sourceInput.GetRectFromCharacterIndex(0);
                Assert.InRange(firstCharacter.Left, sourceInput.Padding.Left, sourceInput.Padding.Left + 4);
                var reminderEditor = Assert.Single(Descendants(settings).OfType<ReminderEditor>());
                var enableReminders = Assert.Single(Descendants(reminderEditor).OfType<System.Windows.Controls.CheckBox>(),
                    box => System.Windows.Automation.AutomationProperties.GetName(box) == "Enable scheduled review reminders");
                var reminderOptions = Assert.Single(Descendants(reminderEditor).OfType<System.Windows.Controls.StackPanel>(),
                    panel => System.Windows.Automation.AutomationProperties.GetName(panel) == "Reminder settings");
                Assert.True(enableReminders.IsEnabled);
                Assert.False(reminderOptions.IsEnabled);
                Assert.All(Descendants(reminderOptions).OfType<System.Windows.Controls.Control>(), control => Assert.False(control.IsEnabled));
                Assert.Equal(System.Windows.Visibility.Visible, reminderOptions.Visibility);
                Assert.Equal(System.Windows.SystemParameters.HighContrast ? 1 : 0.45, reminderOptions.Opacity);
                enableReminders.IsChecked = true;
                Assert.True(reminderEditor.Draft.Enabled);
                Assert.True(reminderOptions.IsEnabled);
                Assert.Equal(1, reminderOptions.Opacity);
                Assert.All(Descendants(reminderOptions).OfType<System.Windows.Controls.ComboBox>(), control => Assert.True(control.IsEnabled));
                Assert.False(settingsConfiguration.Reminders.Enabled);
                enableReminders.IsChecked = false;
                Assert.Equal(settingsConfiguration.Reminders, reminderEditor.Draft);
                enableReminders.IsChecked = true;
                var addTime = Assert.Single(Descendants(reminderEditor).OfType<System.Windows.Controls.Button>(),
                    button => System.Windows.Automation.AutomationProperties.GetName(button) == "Add reminder time");
                for (var index = 2; index < 8; index++)
                    addTime.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(8, reminderEditor.Draft.Times.Length);
                Assert.False(addTime.IsEnabled);
                Assert.Equal(2, settingsConfiguration.Reminders.Times.Length);
                foreach (var removeTime in Descendants(reminderEditor).OfType<System.Windows.Controls.Button>()
                    .Where(button => System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("Remove reminder", StringComparison.Ordinal))
                    .Skip(2).Reverse().ToArray())
                    removeTime.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(2, reminderEditor.Draft.Times.Length);
                var sections = Descendants(settings).OfType<System.Windows.Controls.Border>()
                    .Where(border => border.Tag is string).Select(border => (string)border.Tag).ToArray();
                Assert.Equal(new[] { "Review sources", "Authentication", "People you follow", "I'm the Author",
                    "Review instructions", "Review queue", "Review reminders", "Startup", "Identity cache",
                    "Startup queue cache", "App updates" }, sections);
                var settingsMethods = Descendants(settings).OfType<System.Windows.Controls.RadioButton>().ToArray();
                var settingsPat = Assert.Single(settingsMethods, method => Equals(method.Content, "Personal access token"));
                var settingsPassword = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.PasswordBox>());
                Assert.False(settingsPassword.IsVisible);
                settingsPat.IsChecked = true;
                Assert.True(settingsPassword.IsVisible);
                settingsPassword.Password = "synthetic-settings-draft";
                settingsMethods.Single(method => !ReferenceEquals(method, settingsPat)).IsChecked = true;
                Assert.False(settingsPassword.IsVisible);
                settingsPat.IsChecked = true;
                Assert.Equal("synthetic-settings-draft", settingsPassword.Password);
                settingsPassword.Clear();
                settingsMethods.Single(method => !ReferenceEquals(method, settingsPat)).IsChecked = true;
                var weekdays = Descendants(reminderEditor).OfType<System.Windows.Controls.CheckBox>()
                    .Where(box => box.Style == settings.FindResource("SettingsDay")).ToArray();
                Assert.Equal(7, weekdays.Length);
                weekdays[5].IsChecked = true;
                Assert.Contains(DayOfWeek.Saturday, reminderEditor.Draft.Weekdays);
                Assert.DoesNotContain(DayOfWeek.Saturday, settingsConfiguration.Reminders.Weekdays);
                weekdays[5].IsChecked = false;
                var reminderSound = Assert.Single(Descendants(reminderEditor).OfType<System.Windows.Controls.CheckBox>(),
                    box => Equals(box.Content, "Play reminder sound"));
                reminderSound.IsChecked = true;
                var retainedDraft = reminderEditor.Draft;
                enableReminders.IsChecked = false;
                Assert.False(reminderSound.IsEnabled);
                Assert.All(weekdays, box => Assert.False(box.IsEnabled));
                Assert.All(Descendants(reminderOptions).OfType<System.Windows.Controls.ComboBox>(), control => Assert.False(control.IsEnabled));
                enableReminders.IsChecked = true;
                Assert.True(reminderSound.IsEnabled);
                Assert.All(weekdays, box => Assert.True(box.IsEnabled));
                Assert.Equal(retainedDraft, reminderEditor.Draft);
                reminderSound.IsChecked = false;
                enableReminders.IsChecked = false;
                var copilotPrompt = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.TextBox>(),
                    box => System.Windows.Automation.AutomationProperties.GetName(box) == "GitHub Copilot additional instructions");
                var savePrompt = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.Button>(),
                    button => System.Windows.Automation.AutomationProperties.GetName(button) == "Save and Refresh");
                copilotPrompt.Text = new string('x', 1001);
                Assert.False(savePrompt.IsEnabled);
                Assert.Contains(Descendants(settings).OfType<System.Windows.Controls.TextBlock>(),
                    block => block.Text == "1,001 of 1,000 characters");
                copilotPrompt.SelectAll();
                Assert.Equal(copilotPrompt.Text, copilotPrompt.SelectedText);
                copilotPrompt.SelectedText = "";
                Assert.True(savePrompt.IsEnabled);
                Assert.Empty(settingsConfiguration.CopilotAdditionalInstructions);
                var banner = Assert.IsType<System.Windows.Controls.Border>(queue.FindName("UpdateBanner"));
                Assert.Equal(System.Windows.Visibility.Collapsed, banner.Visibility);
                Assert.Contains(Descendants(queue).OfType<System.Windows.Controls.TextBlock>(), text => text.Text == "3 pending reviews");
                updates.CheckAsync().GetAwaiter().GetResult();
                Assert.Equal(System.Windows.Visibility.Visible, banner.Visibility);
                Assert.Contains(Descendants(queue).OfType<System.Windows.Controls.TextBlock>(), text => text.Text == "ReviewQ v3.3.0 is out");
                var dismiss = Assert.Single(Descendants(queue).OfType<System.Windows.Controls.Button>(),
                    button => System.Windows.Automation.AutomationProperties.GetName(button) == "Dismiss update for this version");
                dismiss.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(System.Windows.Visibility.Collapsed, banner.Visibility);
                Assert.Contains(Descendants(settings).OfType<System.Windows.Controls.Button>(),
                    button => Equals(button.Content, "View Updates") && button.Visibility == System.Windows.Visibility.Visible);
                var check = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.Button>(),
                    button => Equals(button.Content, "Check for Updates"));
                check.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(2, updateClient.Calls);
                Assert.NotNull(updates.AvailableUpdate);
                Assert.Equal(9, controller.Snapshot!.Items.Count);
                var failSetupVerification = false;
                using var setupSession = new SetupSession(Settings, new SetupProgressStore(Path.Combine(directory, "Setup")),
                    (_, _, _) => failSetupVerification
                        ? Task.FromException<ReviewSnapshot>(new ReviewException("Synthetic connection failure"))
                        : Task.FromResult(new ReviewSnapshot([])), (_, _, _) => Task.CompletedTask, _ => { });
                setupSession.Begin(Settings);
                var setupWindow = new SetupWindow(app, setupSession, true);
                setupWindow.Show();
#pragma warning disable WPF0001
                Assert.Equal(System.Windows.ThemeMode.System, setupWindow.ThemeMode);
                var captureTheme = Environment.GetEnvironmentVariable("REVIEWQ_UI_THEME");
                if (captureTheme == "Light") setupWindow.ThemeMode = System.Windows.ThemeMode.Light;
                if (captureTheme == "Dark") setupWindow.ThemeMode = System.Windows.ThemeMode.Dark;
#pragma warning restore WPF0001
                setupWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                setupWindow.UpdateLayout();
                Assert.True(setupWindow.IsLoaded);
                for (var step = 0; step < 6; step++)
                {
                    Assert.Contains(Descendants(setupWindow).OfType<System.Windows.Controls.TextBlock>(),
                        text => text.Text == $"Step {step + 1} of 6");
                    if (setupSession.Step == SetupStep.Authentication)
                    {
                        System.Windows.FrameworkElement SuccessIcon() => Assert.Single(
                            Descendants(setupWindow).OfType<System.Windows.FrameworkElement>(),
                            element => System.Windows.Automation.AutomationProperties.GetName(element) == "Connection verified");
                        Assert.Equal(System.Windows.Visibility.Collapsed, SuccessIcon().Visibility);
                        void CheckPendingIcon()
                        {
                            if (setupSession.IsChecking)
                                Assert.Equal(System.Windows.Visibility.Collapsed, SuccessIcon().Visibility);
                        }
                        setupSession.Changed += CheckPendingIcon;
                        var methods = Descendants(setupWindow).OfType<System.Windows.Controls.RadioButton>().ToArray();
                        Assert.Equal(2, methods.Length);
                        var pat = Assert.Single(methods, method => Equals(method.Content, "Personal access token"));
                        pat.IsChecked = true;
                        var token = Assert.Single(Descendants(setupWindow).OfType<System.Windows.Controls.PasswordBox>());
                        Assert.True(token.IsVisible);
                        token.Password = "synthetic-ui-test-token";
                        Assert.Equal("synthetic-ui-test-token", setupSession.Token);
                        methods.Single(method => !ReferenceEquals(method, pat)).IsChecked = true;
                        Assert.False(token.IsVisible);
                        Assert.Equal("synthetic-ui-test-token", setupSession.Token);
                        setupSession.TestAsync().GetAwaiter().GetResult();
                        Assert.Equal(System.Windows.Visibility.Visible, SuccessIcon().Visibility);
                        var tick = Assert.IsType<System.Windows.Shapes.Path>(SuccessIcon());
                        if (!System.Windows.SystemParameters.HighContrast)
                            Assert.Equal(System.Windows.Media.Colors.SeaGreen,
                                Assert.IsType<System.Windows.Media.SolidColorBrush>(tick.Stroke).Color);
                        var successText = Assert.Single(Descendants(setupWindow).OfType<System.Windows.Controls.TextBlock>(),
                            text => text.Text.StartsWith("Connected.", StringComparison.Ordinal));
                        setupWindow.UpdateLayout();
                        Assert.True(tick.TranslatePoint(new System.Windows.Point(tick.ActualWidth, 0), setupWindow).X
                            < successText.TranslatePoint(new System.Windows.Point(), setupWindow).X);
                        failSetupVerification = true;
                        setupSession.TestAsync().GetAwaiter().GetResult();
                        Assert.Equal(System.Windows.Visibility.Collapsed, SuccessIcon().Visibility);
                        Assert.Contains(Descendants(setupWindow).OfType<System.Windows.Controls.TextBlock>(),
                            text => text.Text.Contains("Synthetic connection failure", StringComparison.Ordinal));
                        failSetupVerification = false;
                        setupSession.TestAsync().GetAwaiter().GetResult();
                        Assert.Equal(System.Windows.Visibility.Visible, SuccessIcon().Visibility);
                        setupSession.Changed -= CheckPendingIcon;
                        setupSession.AdvanceAsync().GetAwaiter().GetResult();
                        setupSession.Back();
                        Assert.Equal(SetupStep.Authentication, setupSession.Step);
                        Assert.Equal("synthetic-ui-test-token",
                            Assert.Single(Descendants(setupWindow).OfType<System.Windows.Controls.PasswordBox>()).Password);
                        Assert.Contains(Descendants(setupWindow).OfType<System.Windows.Controls.TextBlock>(),
                            text => text.Text.StartsWith("Connected.", StringComparison.Ordinal));
                        Assert.Equal(System.Windows.Visibility.Visible, SuccessIcon().Visibility);
                    }
                    if (setupSession.Step == SetupStep.Companions)
                    {
                        var scoutStatus = Assert.Single(Descendants(setupWindow).OfType<System.Windows.Controls.TextBlock>(),
                            text => System.Windows.Automation.AutomationProperties.GetName(text) == "Scout installation status");
                        Assert.Equal(ReviewLauncher.FindScout() is not null ? "Detected" : "Not detected", scoutStatus.Text);
                        Assert.Contains(Descendants(setupWindow).OfType<System.Windows.Controls.TextBlock>(),
                            text => text.Text.Contains("Starts a new review chat using Windows accessibility controls.", StringComparison.Ordinal));
                        var recheck = Assert.Single(Descendants(setupWindow).OfType<System.Windows.Controls.Button>(),
                            button => Equals(button.Content, "Recheck installed companions"));
                        scoutStatus.Text = "stale";
                        recheck.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Assert.Equal(ReviewLauncher.FindScout() is not null ? "Detected" : "Not detected", scoutStatus.Text);
                    }
                    if (setupSession.Step == SetupStep.Preferences)
                    {
                        Assert.True(setupSession.HasVerifiedQueue);
                        var editor = Assert.Single(Descendants(setupWindow).OfType<ReminderEditor>());
                        var sound = Assert.Single(Descendants(editor).OfType<System.Windows.Controls.CheckBox>(),
                            box => Equals(box.Content, "Play reminder sound"));
                        var enable = Assert.Single(Descendants(editor).OfType<System.Windows.Controls.CheckBox>(),
                            box => System.Windows.Automation.AutomationProperties.GetName(box) == "Enable scheduled review reminders");
                        Assert.False(sound.IsEnabled);
                        enable.IsChecked = true;
                        Assert.True(sound.IsEnabled);
                        sound.IsChecked = true;
                        enable.IsChecked = false;
                        Assert.False(sound.IsEnabled);
                        Assert.True(setupSession.Draft.Reminders.PlaysSound);
                        Assert.True(setupSession.HasVerifiedQueue);
                        Assert.False(Settings.Reminders.PlaysSound);
                        var progress = new SetupProgressStore(Path.Combine(directory, "Setup")).Load() ??
                            throw new InvalidOperationException("Setup draft was not saved.");
                        Assert.True(progress.Draft.Reminders.PlaysSound);
                    }
                    foreach (var size in new[] { (Width: 540d, Height: 440d), (Width: 800d, Height: 780d) })
                    {
                        setupWindow.Width = size.Width; setupWindow.Height = size.Height;
                        setupWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        setupWindow.UpdateLayout();
                        var setupElements = Descendants(setupWindow).ToArray();
                        var continueButton = Assert.Single(setupElements.OfType<System.Windows.Controls.Button>(),
                            button => button.IsDefault);
                        Assert.True(continueButton.IsVisible);
                        var position = continueButton.TranslatePoint(new System.Windows.Point(), setupWindow);
                        Assert.True(position.X >= 0 && position.X + continueButton.ActualWidth <= setupWindow.ActualWidth);
                        Assert.True(position.Y >= 0 && position.Y + continueButton.ActualHeight <= setupWindow.ActualHeight);
                        var navigation = Assert.IsType<System.Windows.Controls.Grid>(continueButton.Parent);
                        var visibleButtons = navigation.Children.OfType<System.Windows.Controls.Button>()
                            .Where(button => button.IsVisible).OrderBy(button => button.TranslatePoint(new System.Windows.Point(), navigation).X).ToArray();
                        for (var index = 1; index < visibleButtons.Length; index++)
                            Assert.True(visibleButtons[index - 1].TranslatePoint(new System.Windows.Point(visibleButtons[index - 1].ActualWidth, 0), navigation).X
                                <= visibleButtons[index].TranslatePoint(new System.Windows.Point(), navigation).X,
                                "Setup navigation buttons must not overlap at the minimum window width.");
                        var captureDirectory = Environment.GetEnvironmentVariable("REVIEWQ_UI_CAPTURES");
                        if (!string.IsNullOrEmpty(captureDirectory))
                        {
                            Directory.CreateDirectory(captureDirectory);
                            var content = setupWindow;
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                (int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                            var drawing = new System.Windows.Media.DrawingVisual();
                            using (var context = drawing.RenderOpen())
                            {
                                var bounds = new System.Windows.Rect(0, 0, content.ActualWidth, content.ActualHeight);
                                context.DrawRectangle(captureTheme == "Light" ? System.Windows.Media.Brushes.White :
                                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)), null, bounds);
                                context.DrawRectangle(new System.Windows.Media.VisualBrush(content)
                                {
                                    ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
                                    Viewbox = bounds, Stretch = System.Windows.Media.Stretch.Fill
                                }, null, bounds);
                            }
                            bitmap.Render(drawing);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            using var output = File.Create(Path.Combine(captureDirectory, $"setup-{step + 1}-{size.Width}.png"));
                            encoder.Save(output);
                        }
                    }
                    setupSession.AdvanceAsync().GetAwaiter().GetResult();
                }
                setupWindow.Close();
                Assert.Equal("", setupSession.Token);
                Assert.False(setupSession.HasVerifiedQueue);
                var diagnosticElements = Descendants(diagnostics).ToArray();
                var diagnosticButtons = diagnosticElements.OfType<System.Windows.Controls.Button>()
                    .Select(System.Windows.Automation.AutomationProperties.GetName).ToArray();
                Assert.Contains("Export Logs...", diagnosticButtons);
                Assert.Contains("Show in Explorer", diagnosticButtons);
                Assert.Contains("Clear Logs...", diagnosticButtons);
                Assert.Contains(diagnosticElements, element => element is System.Windows.Controls.ScrollViewer);
                var verbose = Assert.Single(diagnosticElements.OfType<System.Windows.Controls.CheckBox>());
                verbose.IsChecked = true;
                Assert.True(logs.Verbose);
                verbose.IsChecked = false;
                Assert.False(logs.Verbose);
                var buttons = Descendants(queue).OfType<System.Windows.Controls.Button>().ToArray();
                var refresh = Assert.Single(buttons, button => System.Windows.Automation.AutomationProperties.GetName(button) == "Refresh reviews");
                var setupButton = Assert.Single(buttons, button => System.Windows.Automation.AutomationProperties.GetName(button) == "Run Setup");
                var creator = Assert.Single(buttons, button => Equals(button.Content, "By: Hritvik Patel"));
                var footer = Assert.IsType<System.Windows.Controls.Grid>(creator.Parent);
                var updatedText = Assert.IsType<System.Windows.Controls.TextBlock>(queue.FindName("UpdatedText"));
                foreach (var width in new[] { queue.MinWidth, 560, 900 })
                {
                    queue.Width = width;
                    queue.UpdateLayout();
                    var creatorLeft = creator.TranslatePoint(new System.Windows.Point(), footer).X;
                    Assert.Equal(footer.ActualWidth - creator.Margin.Right,
                        creatorLeft + creator.ActualWidth, precision: 1);
                    Assert.True(updatedText.ActualWidth + updatedText.Margin.Right <= creatorLeft,
                        "The update age must not overlap the creator credit.");
                    var toolbar = new[] { setupButton, refresh, (System.Windows.Controls.Button)queue.FindName("SettingsButton"),
                        (System.Windows.Controls.Button)queue.FindName("QuitButton") };
                    for (var index = 1; index < toolbar.Length; index++)
                        Assert.True(toolbar[index - 1].TranslatePoint(new System.Windows.Point(toolbar[index - 1].ActualWidth, 0), queue).X <=
                            toolbar[index].TranslatePoint(new System.Windows.Point(), queue).X, "Header actions must remain ordered and not overlap.");
                    Assert.True(toolbar[^1].TranslatePoint(new System.Windows.Point(toolbar[^1].ActualWidth, 0), queue).X <= queue.ActualWidth);
                }
                queue.Width = 560;
                queue.UpdateLayout();
                Assert.DoesNotContain(Descendants(queue).OfType<System.Windows.Controls.TextBlock>(),
                    text => text.Text.StartsWith("Closing this window", StringComparison.Ordinal));
                Assert.Contains(Descendants(queue).OfType<System.Windows.Controls.TextBlock>(),
                    text => text.Text.StartsWith("Updated ", StringComparison.Ordinal));
                var settingsButton = Assert.Single(buttons, button => System.Windows.Automation.AutomationProperties.GetName(button) == "Settings");
                var refreshRight = refresh.TranslatePoint(new System.Windows.Point(refresh.ActualWidth, 0), queue).X;
                var settingsLeft = settingsButton.TranslatePoint(new System.Windows.Point(0, 0), queue).X;
                Assert.True(settingsLeft - refreshRight >= 8, "Toolbar buttons need an eight-DIP visible gap.");
                Assert.Equal(9, buttons.Count(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Open in Microsoft Edge"));
                Assert.Equal(6, buttons.Count(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Start a new VS Code Copilot agent chat"));
                Assert.Equal(6, buttons.Count(button => button.IsEnabled && System.Windows.Automation.AutomationProperties.GetName(button) == "Start a new Scout review chat"));
                foreach (var button in buttons.Where(button => Equals(button.Style, app.Resources["ReviewIconButton"]) &&
                    button.Content is System.Windows.FrameworkElement { Width: 16 } &&
                    System.Windows.Automation.AutomationProperties.GetName(button) != "Archive pull request"))
                {
                    var icon = Assert.IsAssignableFrom<System.Windows.FrameworkElement>(button.Content);
                    Assert.Equal(16, icon.Width);
                    Assert.Equal(16, icon.Height);
                    if (!System.Windows.SystemParameters.HighContrast)
                    {
                        var image = Assert.IsType<System.Windows.Controls.Image>(icon);
                        var artwork = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(image.Source);
                        Assert.True(artwork.IsFrozen);
                        Assert.InRange(artwork.PixelWidth, 32, 64);
                        Assert.InRange(artwork.PixelHeight, 32, 64);
                    }
                    Assert.Equal(34, button.ActualWidth);
                    var hover = Assert.Single(button.Template.Triggers.OfType<System.Windows.Trigger>(),
                        trigger => trigger.Property == System.Windows.UIElement.IsMouseOverProperty);
                    var scaleSetter = Assert.Single(hover.Setters.OfType<System.Windows.Setter>(), setter => setter.TargetName == "ActionIcon");
                    var scale = Assert.IsType<System.Windows.Media.ScaleTransform>(scaleSetter.Value);
                    Assert.Equal(18, icon.Width * scale.ScaleX);
                    Assert.Equal(18, icon.Height * scale.ScaleY);
                }
                var groups = Descendants(queue).OfType<System.Windows.Controls.Expander>().ToArray();
                Assert.Equal(4, groups.Length);
                foreach (var name in new[] { "People you follow", "I'm the Author" })
                {
                    var section = Assert.Single(groups, group => System.Windows.Automation.AutomationProperties.GetName(group) == name);
                    Assert.Empty(Descendants(section).OfType<System.Windows.Controls.Expander>());
                    var text = Descendants(section).OfType<System.Windows.Controls.TextBlock>().Select(block => block.Text).ToArray();
                    Assert.Equal(new[] { "Author in repository", "Author in another", "Author in repository" },
                        text.Where(value => value.StartsWith("Author", StringComparison.Ordinal)).ToArray());
                    Assert.DoesNotContain("org / project", text);
                    var titles = Descendants(section).OfType<System.Windows.Controls.Button>()
                        .Select(System.Windows.Automation.AutomationProperties.GetName)
                        .Where(value => value.StartsWith("Open pull request", StringComparison.Ordinal)).ToArray();
                    Assert.Equal(new[] { 1, 2, 3 }, titles.Select(value => int.Parse(value.Split(' ')[3].TrimEnd(':'))).ToArray());
                }
                Assert.All(groups, group => Assert.NotNull(group.Template.FindName("HeaderButton", group)));
                Assert.All(groups, group =>
                {
                    Assert.Equal(queue.Foreground, group.Foreground);
                    var header = Assert.IsType<System.Windows.Controls.Grid>(group.Header);
                    Assert.True(header.ActualWidth >= group.ActualWidth - 60, $"Header width {header.ActualWidth} should fill group width {group.ActualWidth}.");
                });
                Assert.All(buttons.Where(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Open in Microsoft Edge"),
                    button => Assert.Equal(queue.Foreground, button.Foreground));
                var repositories = groups.Where(group => System.Windows.Automation.AutomationProperties.GetName(group) == "org/project/repository").ToArray();
                Assert.Single(repositories);
                repositories[0].IsExpanded = false;
                controller.RefreshAsync().GetAwaiter().GetResult();
                queue.UpdateLayout();
                Assert.Null(controller.Error);
                Assert.True(controller.Changes.HasChanges);
                Assert.Contains(Descendants(queue).OfType<System.Windows.FrameworkElement>(), element =>
                    System.Windows.Automation.AutomationProperties.GetName(element) ==
                    "Pull request is new or changed since the previous successful refresh");
                Assert.Contains(Descendants(queue).OfType<System.Windows.FrameworkElement>(), element =>
                    System.Windows.Automation.AutomationProperties.GetName(element) ==
                    "Section changed since the previous successful refresh");
                var refreshedRepositories = Descendants(queue).OfType<System.Windows.Controls.Expander>()
                    .Where(group => System.Windows.Automation.AutomationProperties.GetName(group) == "org/project/repository").ToArray();
                Assert.False(refreshedRepositories[0].IsExpanded);
                Assert.Single(refreshedRepositories);
                var otherRepository = Assert.Single(Descendants(queue).OfType<System.Windows.Controls.Expander>(),
                    group => System.Windows.Automation.AutomationProperties.GetName(group) == "org/project/another");
                Assert.True(otherRepository.IsExpanded);
                Assert.Equal(9, buttons.Count(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Archive pull request"));
                var archiveAction = Assert.Single(buttons.Where(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Archive pull request").Take(1));
                if (!System.Windows.SystemParameters.HighContrast)
                {
                    var image = Assert.IsType<System.Windows.Controls.Image>(archiveAction.Content);
                    var drawing = Assert.IsType<System.Windows.Media.DrawingImage>(image.Source);
                    Assert.True(drawing.IsFrozen);
                    Assert.Equal(32, drawing.Drawing.Bounds.Width);
                }
                archiveAction.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                queue.UpdateLayout();
                Assert.Single(controller.ArchivedItems);
                Assert.Equal(2, controller.PendingCount);
                var archiveSection = Assert.Single(Descendants(queue).OfType<System.Windows.Controls.Expander>(),
                    group => System.Windows.Automation.AutomationProperties.GetName(group) == "Archives");
                Assert.False(archiveSection.IsExpanded);
                archiveSection.IsExpanded = true;
                queue.UpdateLayout();
                var restore = Assert.Single(Descendants(archiveSection).OfType<System.Windows.Controls.Button>(),
                    button => System.Windows.Automation.AutomationProperties.GetName(button) == "Restore pull request");
                Assert.DoesNotContain(Descendants(archiveSection).OfType<System.Windows.Controls.Button>(),
                    button => System.Windows.Automation.AutomationProperties.GetName(button).Contains("chat", StringComparison.Ordinal));
                restore.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Empty(controller.ArchivedItems);
                Assert.Equal(3, controller.PendingCount);
                foreach (var item in controller.Snapshot!.Items) controller.SetArchived(item, true);
                queue.UpdateLayout();
                Assert.Empty(controller.ActiveItems);
                Assert.Equal(0, controller.PendingCount);
                Assert.Equal(9, controller.ArchivedItems.Count);
                Assert.Single(Descendants(queue).OfType<System.Windows.Controls.Expander>());
                foreach (var item in controller.ArchivedItems.ToArray()) controller.SetArchived(item, false);
                queue.UpdateLayout();
                var queueCaptureDirectory = Environment.GetEnvironmentVariable("REVIEWQ_UI_CAPTURES");
                if (!string.IsNullOrEmpty(queueCaptureDirectory))
                {
#pragma warning disable WPF0001
                    if (captureTheme == "Light") queue.ThemeMode = System.Windows.ThemeMode.Light;
                    if (captureTheme == "Dark") queue.ThemeMode = System.Windows.ThemeMode.Dark;
#pragma warning restore WPF0001
                    queue.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var queuePanel = Assert.IsType<System.Windows.Controls.StackPanel>(queue.FindName("QueuePanel"));
                    foreach (var group in queuePanel.Children.OfType<System.Windows.Controls.Expander>()) group.IsExpanded = false;
                    queue.UpdateLayout();
                    foreach (var group in queuePanel.Children.OfType<System.Windows.Controls.Expander>())
                    {
                        Assert.False(group.IsExpanded);
                        var content = Assert.IsType<System.Windows.Controls.ContentPresenter>(group.Template.FindName("GroupContent", group));
                        Assert.Equal(System.Windows.Visibility.Collapsed, content.Visibility);
                    }
                    void CaptureQueue(string name, System.Windows.Window? target = null)
                    {
                        target ??= queue;
                        target.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight,
                            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        var background = new System.Windows.Media.DrawingVisual();
                        using (var context = background.RenderOpen())
                            context.DrawRectangle(captureTheme == "Dark" ? new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(32, 32, 32)) : System.Windows.Media.Brushes.White,
                                null, new System.Windows.Rect(0, 0, target.ActualWidth, target.ActualHeight));
                        bitmap.Render(background);
                        bitmap.Render(target);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var imageFile = File.Create(Path.Combine(queueCaptureDirectory, $"queue-{captureTheme}-{name}.png"));
                        encoder.Save(imageFile);
                    }
                    foreach (var width in new[] { queue.MinWidth, 560 })
                    {
                        queue.Width = width;
                        CaptureQueue(width.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    updateClient.Version = "3.4.0";
                    updates.CheckAsync(manually: true).GetAwaiter().GetResult();
                    Assert.Equal(System.Windows.Visibility.Visible, banner.Visibility);
                    queue.Width = queue.MinWidth;
                    CaptureQueue("update-banner");
                    Assert.True(banner.ActualHeight >= 32);
                    var viewUpdate = Assert.Single(Descendants(banner).OfType<System.Windows.Controls.Button>(),
                        button => Equals(button.Content, "View Updates"));
                    Assert.True(viewUpdate.IsVisible);
                    Assert.True(viewUpdate.TranslatePoint(new System.Windows.Point(viewUpdate.ActualWidth, 0), banner).X < banner.ActualWidth);
                    controller.SetArchived(controller.Snapshot.Items[0], true);
                    foreach (var group in queuePanel.Children.OfType<System.Windows.Controls.Expander>())
                        group.IsExpanded = System.Windows.Automation.AutomationProperties.GetName(group) == "Archives";
                    CaptureQueue("archives");
                    controller.SetArchived(controller.ArchivedItems[0], false);
                    foreach (var group in queuePanel.Children.OfType<System.Windows.Controls.Expander>())
                        group.IsExpanded = System.Windows.Automation.AutomationProperties.GetName(group) == "org/project/another";
                    CaptureQueue("archive-action");
#pragma warning disable WPF0001
                    if (captureTheme == "Light") settings.ThemeMode = System.Windows.ThemeMode.Light;
                    if (captureTheme == "Dark") settings.ThemeMode = System.Windows.ThemeMode.Dark;
#pragma warning restore WPF0001
                    settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    reminderEditor.BringIntoView();
                    CaptureQueue("reminder-settings", settings);
                    var settingsScroll = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.ScrollViewer>(),
                        viewer => viewer.Content is System.Windows.Controls.StackPanel { Margin.Left: 26 });
                    foreach (var size in new[] { (Width: 440d, Height: 400d), (Width: 640d, Height: 820d) })
                    {
                        settings.Width = size.Width; settings.Height = size.Height;
                        settingsScroll.ScrollToTop();
                        settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        settings.UpdateLayout();
                        var saveSettings = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.Button>(),
                            button => System.Windows.Automation.AutomationProperties.GetName(button) == "Save and Refresh");
                        var diagnosticsSettings = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.Button>(),
                            button => Equals(button.Content, "Diagnostics..."));
                        var saveBounds = saveSettings.TransformToAncestor(settings).TransformBounds(
                            new System.Windows.Rect(0, 0, saveSettings.ActualWidth, saveSettings.ActualHeight));
                        var diagnosticsBounds = diagnosticsSettings.TransformToAncestor(settings).TransformBounds(
                            new System.Windows.Rect(0, 0, diagnosticsSettings.ActualWidth, diagnosticsSettings.ActualHeight));
                        Assert.True(diagnosticsBounds.Right < saveBounds.Left);
                        Assert.True(saveBounds.Right <= settings.ActualWidth && saveBounds.Bottom <= settings.ActualHeight);
                        Assert.True(settingsScroll.TranslatePoint(new System.Windows.Point(0, settingsScroll.ActualHeight), settings).Y <= saveBounds.Top);
                        Assert.Equal(0, settingsScroll.ScrollableWidth);
                        CaptureQueue($"settings-top-{size.Width}", settings);
                        var authenticationCard = Assert.Single(Descendants(settings).OfType<System.Windows.Controls.Border>(),
                            border => Equals(border.Tag, "Authentication"));
                        authenticationCard.BringIntoView();
                        settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        CaptureQueue($"settings-auth-{size.Width}", settings);
                        reminderEditor.BringIntoView();
                        settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        CaptureQueue($"settings-reminders-{size.Width}", settings);
                    }
                    foreach (var title in new[] { "People you follow", "Review instructions", "Review reminders", "App updates" })
                    {
                        Assert.Single(Descendants(settings).OfType<System.Windows.Controls.Border>(),
                            border => Equals(border.Tag, title)).BringIntoView();
                        settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        CaptureQueue("settings-" + title.Replace(' ', '-'), settings);
                    }
                }
                settings.Close();
                Assert.False(settingsConfiguration.Reminders.Enabled);
                diagnostics.Close();
                queue.Hide();
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Theme window test did not complete.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task CommandFailureMapsStderrWithoutExposingIt()
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var error = await Assert.ThrowsAsync<ReviewException>(() => CommandRunner.RunAsync(powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.Write('Please run az login; secret-token'); exit 1"],
            TimeSpan.FromSeconds(10), default,
            commandFailure: (exit, stderr) => WindowsAuthorization.AzureCliFailure(exit, stderr, "")));
        Assert.Contains("not signed in", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
    }

    [Theory]
    [InlineData("resources\\app\\out\\cli.js", false)]
    [InlineData("16507d91be\\resources\\app\\out\\cli.js", true)]
    public void CodeDiscoveryHandlesLegacyAndVersionedResources(string relative, bool wrapper)
    {
        var script = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");
        if (wrapper)
        {
            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            File.WriteAllText(Path.Combine(directory, "bin", "code.cmd"), $"@echo off\n\"%~dp0..\\Code.exe\" \"%~dp0..\\{relative}\" %*");
        }
        Assert.Equal(script, ReviewLauncher.CodeCliScript(Path.Combine(directory, "Code.exe")));
    }

    [Fact]
    public void CodeDiscoveryDoesNotExecuteWrapperOrEscapeInstallDirectory()
    {
        var bin = Path.Combine(directory, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "code.cmd"), "@echo off\n\"%~dp0..\\..\\outside\\cli.js\"");
        Assert.Null(ReviewLauncher.CodeCliScript(Path.Combine(directory, "Code.exe")));
    }

    [Fact]
    public void ScoutDetectionRequiresAnInstalledExecutableButNotARunningProcess()
    {
        Assert.Null(ReviewLauncher.ScoutExecutable(directory));
        var installDirectory = Path.Combine(directory, "Microsoft Scout");
        Directory.CreateDirectory(installDirectory);
        Assert.Null(ReviewLauncher.ScoutExecutable(directory));
        File.WriteAllText(Path.Combine(installDirectory, "unrelated.exe"), "");
        Assert.Null(ReviewLauncher.ScoutExecutable(directory));
        var executable = Path.Combine(installDirectory, "scout.exe");
        File.WriteAllText(executable, "");
        Assert.Equal(executable, ReviewLauncher.ScoutExecutable(directory));
        File.Delete(executable);
        Assert.Null(ReviewLauncher.ScoutExecutable(directory));
    }

    private static void VerifyScoutAccessibilityHandoff()
    {
        var newChat = new System.Windows.Controls.Button { Content = "New chat" };
        var message = new System.Windows.Controls.TextBox { Text = "\n" };
        System.Windows.Automation.AutomationProperties.SetName(message, "Message");
        var send = new System.Windows.Controls.Button { Content = "Send" };
        var stop = new System.Windows.Controls.Button { Content = "Stop", Visibility = System.Windows.Visibility.Collapsed };
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(newChat); panel.Children.Add(message); panel.Children.Add(send); panel.Children.Add(stop);
        var window = new System.Windows.Window { Title = "Synthetic Scout controls", Content = panel, Width = 400, Height = 250 };
        const string prompt = "Review PR: https://dev.azure.com/org/project/_git/repo/pullrequest/42\n\nCheck correctness.";
        var created = 0;
        var submitted = 0;
        newChat.Click += (_, _) => { created++; message.Text = "\n"; };
        send.Click += (_, _) =>
        {
            Assert.Equal(prompt, message.Text);
            submitted++;
            message.Text = "\n";
            stop.Visibility = System.Windows.Visibility.Visible;
        };
        window.Show();
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var task = Task.Run(() =>
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                return ScoutReviewAutomation.StartAsync(new WindowsScoutControls(root), prompt, timeout.Token);
            });
            var frame = new System.Windows.Threading.DispatcherFrame();
            var pump = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            pump.Tick += (_, _) => { if (task.IsCompleted) frame.Continue = false; };
            pump.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { pump.Stop(); }
            task.GetAwaiter().GetResult();
            Assert.Equal(1, created);
            Assert.Equal(1, submitted);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task CommandTimeoutAndCancellationAreDistinct()
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        string[] args = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"];
        var timeout = await Assert.ThrowsAsync<ReviewException>(() => CommandRunner.RunAsync(powershell, args, TimeSpan.FromMilliseconds(300), default));
        Assert.Contains("timed out", timeout.Message);
        using var cancellation = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CommandRunner.RunAsync(powershell, args, TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task CommandArgumentsCannotBecomeShellCommands()
    {
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "must-not-exist.txt");
        var argument = $"text & echo injected > \"{marker}\"";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var script = Path.Combine(directory, "echo.ps1");
        File.WriteAllText(script, "param([string]$Value)\n[Console]::Write($Value)");
        var output = await CommandRunner.RunAsync(powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, argument],
            TimeSpan.FromSeconds(10), default);
        Assert.Equal(argument, output);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task RefreshPreservesLastSnapshotOnFailureAndDoesNotOverlap()
    {
        var completion = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new Loader { Load = (_, _) => completion.Task };
        using var controller = new ReviewController(Settings, loader, _ => { });
        var first = controller.RefreshAsync();
        await controller.RefreshAsync();
        Assert.Equal(1, loader.Calls);
        completion.SetResult(new ReviewSnapshot([]));
        await first;
        var snapshot = controller.Snapshot;
        loader.Load = (_, _) => throw new ReviewException("visible failure");
        await controller.RefreshAsync();
        Assert.Same(snapshot, controller.Snapshot);
        Assert.Equal("visible failure", controller.Error);
        Assert.False(controller.IsRefreshing);
    }

    [Fact]
    public async Task SupersededRefreshCannotOverwriteNewGeneration()
    {
        var stale = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new Loader { Load = (_, _) => stale.Task };
        using var controller = new ReviewController(Settings, loader, _ => { });
        var first = controller.RefreshAsync();
        var replacement = new ReviewSnapshot([]);
        loader.Load = (_, _) => Task.FromResult(replacement);
        await controller.ApplyAsync(Settings with { IncludeDrafts = true }, true);
        stale.SetResult(new ReviewSnapshot([]));
        await first;
        Assert.Same(replacement, controller.Snapshot);
        Assert.Equal(1, loader.Clears);
        Assert.Null(controller.Error);
        Assert.False(controller.IsRefreshing);
    }

    [Fact]
    public async Task UnexpectedBackgroundFailureIsExplicit()
    {
        var loader = new Loader { Load = (_, _) => throw new InvalidOperationException("private details") };
        using var controller = new ReviewController(Settings, loader, _ => { });
        await controller.RefreshAsync();
        Assert.Contains("unexpected refresh error", controller.Error);
        Assert.DoesNotContain("private details", controller.Error);
        Assert.False(controller.IsRefreshing);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class Loader : IReviewLoader
    {
        public int Calls, Clears;
        public Func<ReviewSettings, CancellationToken, Task<ReviewSnapshot>> Load { get; set; } = (_, _) => Task.FromResult(new ReviewSnapshot([]));
        public Task<ReviewSnapshot> LoadAsync(ReviewSettings settings, CancellationToken ct) { Calls++; return Load(settings, ct); }
        public Task ClearIdentitiesAsync(CancellationToken ct) { Clears++; return Task.CompletedTask; }
    }

    private sealed class ThemeTestApp : App
    {
        protected override void OnStartup(System.Windows.StartupEventArgs e) { }
    }
    private sealed class UpdateClient : IUpdateClient
    {
        public int Calls { get; private set; }
        public string Version { get; set; } = "3.3.0";
        public Task<UpdateCheckResult> CheckAsync(string? entityTag, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new UpdateCheckResult(false,
                new AvailableUpdate(Version, new Uri(AvailableUpdate.ReleaseBase + "v" + Version)), "\"test\""));
        }
    }
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
