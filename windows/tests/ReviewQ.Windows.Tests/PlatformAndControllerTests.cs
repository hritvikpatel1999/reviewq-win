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
        File.WriteAllText(Path.Combine(directory, "settings.json"), "secret-settings");
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
                using var controller = new ReviewController(Settings, new Loader
                {
                    Load = (_, _) => Task.FromResult(new ReviewSnapshot(Enum.GetValues<QueueKind>().SelectMany(kind =>
                        Enumerable.Range(1, 3).Select(number => new ReviewItem
                        {
                            Number = number, Title = $"Review {kind} {number}", Author = "Author", Organization = "org",
                            Project = "project", Repository = number == 2 ? "another" : "repository",
                            Queue = kind, CreatedAt = DateTimeOffset.UtcNow
                        })).ToArray()))
                }, _ => { });
                controller.RefreshAsync().GetAwaiter().GetResult();
                var queue = new MainWindow(app, controller);
                var settings = new SettingsWindow(app, new ReviewSettings());
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
                var refresh = Assert.Single(buttons, button => Equals(button.Content, "_Refresh"));
                var settingsButton = Assert.Single(buttons, button => Equals(button.Content, "_Settings"));
                var refreshRight = refresh.TranslatePoint(new System.Windows.Point(refresh.ActualWidth, 0), queue).X;
                var settingsLeft = settingsButton.TranslatePoint(new System.Windows.Point(0, 0), queue).X;
                Assert.True(settingsLeft - refreshRight >= 8, "Toolbar buttons need an eight-DIP visible gap.");
                Assert.Equal(9, buttons.Count(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Open in Microsoft Edge"));
                Assert.Equal(6, buttons.Count(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Start a new VS Code Copilot agent chat"));
                Assert.Equal(6, buttons.Count(button => !button.IsEnabled && System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("Scout unavailable", StringComparison.Ordinal)));
                foreach (var button in buttons.Where(button => Equals(button.Style, app.Resources["ReviewIconButton"])))
                {
                    var icon = Assert.IsAssignableFrom<System.Windows.FrameworkElement>(button.Content);
                    Assert.Equal(16, icon.Width);
                    Assert.Equal(16, icon.Height);
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
                var refreshedRepositories = Descendants(queue).OfType<System.Windows.Controls.Expander>()
                    .Where(group => System.Windows.Automation.AutomationProperties.GetName(group) == "org/project/repository").ToArray();
                Assert.False(refreshedRepositories[0].IsExpanded);
                Assert.Single(refreshedRepositories);
                var otherRepository = Assert.Single(Descendants(queue).OfType<System.Windows.Controls.Expander>(),
                    group => System.Windows.Automation.AutomationProperties.GetName(group) == "org/project/another");
                Assert.True(otherRepository.IsExpanded);
                settings.Close();
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
