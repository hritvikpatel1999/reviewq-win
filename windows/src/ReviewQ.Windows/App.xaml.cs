using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using ReviewQ.Core;
using Forms = System.Windows.Forms;

namespace ReviewQ.Windows;

public partial class App : Application
{
    private readonly CancellationTokenSource lifetime = new();
    private Mutex? instance;
    private bool ownsMutex;
    private Task? activationTask;
    private HttpClient? http;
    private Forms.NotifyIcon? tray;
    private Icon? trayIcon;
    private ShellIntegration? shell;
    private SettingsStore? settingsStore;
    private PrivateDiagnostics? diagnostics;
    private WindowsAuthorization? authorization;
    private MainWindow? queue;
    private SettingsWindow? settingsWindow;
    private DiagnosticsWindow? diagnosticsWindow;
    private bool launching;
    public bool IsQuitting { get; private set; }
    public CancellationToken Lifetime => lifetime.Token;
    public IPatStore PatStore { get; } = new WindowsPatStore();
    public ReviewController Controller { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeTest = e.Args.Contains("--smoke-test");
        DispatcherUnhandledException += (_, error) =>
        {
            MessageBox.Show("ReviewQ encountered an unexpected error and must exit. Your saved settings are retained.",
                "ReviewQ", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
            Quit(1);
        };
        try
        {
            var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new ReviewException("The Windows user identity is unavailable.");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
            var pipe = "ReviewQ-" + identity + (smokeTest ? "-smoke" : "");
            instance = new Mutex(true, @"Local\" + pipe, out ownsMutex);
            if (!ownsMutex)
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await client.ConnectAsync(5000, Lifetime);
                    await client.WriteAsync(new byte[] { 1 }, Lifetime);
                    await client.FlushAsync(Lifetime);
                }
                catch (TimeoutException) { throw new ReviewException("ReviewQ is already starting but did not respond. Wait a moment and open it again."); }
                Shutdown();
                return;
            }
            var directory = smokeTest ? Path.Combine(Path.GetTempPath(), "ReviewQ-Smoke-" + Guid.NewGuid().ToString("N")) :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReviewQ");
            if (smokeTest) smokeDirectory = directory;
            settingsStore = new(directory);
            diagnostics = new(Path.Combine(directory, "Logs"));
            diagnostics.Write(new("startup", "application"));
            string? loadError = null;
            ReviewSettings settings;
            try { settings = settingsStore.Load(); }
            catch (Exception ex) when (UiErrors.IsExpected(ex)) { settings = new(); loadError = UiErrors.Message(ex); }
            authorization = new(PatStore);
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { Timeout = TimeSpan.FromSeconds(30) };
            Controller = new(settings, new AzureDevOpsClient(http, authorization, Path.Combine(directory, "identity-cache.json"), diagnostics.Write), diagnostics.Write);
            queue = new MainWindow(this, Controller);
            MainWindow = queue;
            var iconPath = Path.Combine(AppContext.BaseDirectory, "ReviewQ.ico");
            trayIcon = File.Exists(iconPath) ? new Icon(iconPath) : (Icon)SystemIcons.Application.Clone();
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "ReviewQ", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show ReviewQ", null, (_, _) => ShowQueue());
            menu.Items.Add("Refresh", null, async (_, _) => await Controller.RefreshAsync());
            menu.Items.Add("Settings", null, (_, _) => ShowSettings());
            menu.Items.Add("Diagnostics", null, (_, _) => ShowDiagnostics());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit ReviewQ", null, (_, _) => Quit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowQueue(); };
            shell = new(queue, ToggleQueue, () => { if (tray is not null) { tray.Visible = false; tray.Visible = true; } });
            try { if (!smokeTest) RegisterShortcut(settings.GlobalShortcut); }
            catch (ReviewException ex) { loadError = ex.Message; }
            Controller.PropertyChanged += (_, _) => UpdateStatus();
            activationTask = ListenAsync(pipe);
            SystemEvents.PowerModeChanged += PowerChanged;
            if (loadError is not null) Controller.Report(loadError);
            if (!e.Args.Contains("--startup") || !settings.HasSources || loadError is not null) ShowQueue();
            if (!settings.HasSources) ShowSettings();
            if (e.Args.Contains("--diagnostics")) ShowDiagnostics();
            Controller.Start();
            if (smokeTest)
            {
                await Task.Delay(1500, Lifetime);
                Quit(queue.IsLoaded && queue.IsVisible && tray.Visible && settingsWindow?.IsLoaded == true ? 0 : 1);
            }
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex) || ex is TimeoutException)
        {
            MessageBox.Show(UiErrors.Message(ex), "ReviewQ could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit(1);
        }
    }

    private async Task ListenAsync(string pipe)
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(Lifetime);
                var buffer = new byte[1];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    if (await server.ReadAsync(buffer, timeout.Token) > 0) await Dispatcher.InvokeAsync(ShowQueue);
                }
                catch (OperationCanceledException) when (!Lifetime.IsCancellationRequested)
                { await Dispatcher.InvokeAsync(() => Controller.Report("Another ReviewQ launch did not finish its activation request. Open the tray icon to show the queue.")); }
                catch (IOException)
                { await Dispatcher.InvokeAsync(() => Controller.Report("Another ReviewQ launch disconnected before activation. Open the tray icon to show the queue.")); }
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        { await Dispatcher.InvokeAsync(() => Controller.Report("App reactivation is unavailable. " + UiErrors.Message(ex))); }
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Dispatcher.InvokeAsync(async () => await Controller.RefreshAsync());
    }
    public void ShowQueue()
    {
        if (queue is null) return;
        queue.Show();
        if (queue.WindowState == WindowState.Minimized) queue.WindowState = WindowState.Normal;
        queue.Activate();
    }
    private void ToggleQueue()
    {
        if (queue?.IsVisible == true && queue.IsActive) queue.Hide(); else ShowQueue();
    }
    public void ShowSettings()
    {
        if (queue is null) return;
        if (settingsWindow is null)
        {
            settingsWindow = new(this, Controller.Settings) { Owner = queue };
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        ShowQueue();
        settingsWindow.Show(); settingsWindow.Activate();
    }
    public void ShowDiagnostics()
    {
        if (queue is null || diagnostics is null) return;
        if (diagnosticsWindow is null)
        {
            diagnosticsWindow = new(diagnostics) { Owner = queue };
            diagnosticsWindow.Closed += (_, _) => diagnosticsWindow = null;
        }
        ShowQueue(); diagnosticsWindow.Show(); diagnosticsWindow.Activate();
    }
    public void RegisterShortcut(string text) => shell?.Register(text);
    public void InvalidateAuthentication() => authorization?.Invalidate();
    public async Task SaveSettingsAsync(ReviewSettings settings, string newPat)
    {
        settings.Validate();
        _ = ShellIntegration.Parse(settings.GlobalShortcut);
        if (newPat.Length > 0) WindowsPatStore.Validate(newPat);
        if (settings.Authentication == AuthenticationMethod.PersonalAccessToken && newPat.Length == 0 && PatStore.Load() is null)
            throw new ReviewException("Save a Code (Read) PAT before choosing PAT authentication.");
        var previous = Controller.Settings;
        var previousPat = newPat.Length > 0 ? PatStore.Load() : null;
        try
        {
            RegisterShortcut(settings.GlobalShortcut);
            StartupRegistration.Set(settings.StartAtLogin);
            if (newPat.Length > 0) PatStore.Save(newPat);
            settingsStore!.Save(settings);
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        {
            var recoveryErrors = new List<string>();
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception recovery) when (UiErrors.IsExpected(recovery))
                { recoveryErrors.Add(UiErrors.Message(recovery)); }
            }
            Restore(() => RegisterShortcut(previous.GlobalShortcut));
            Restore(() => StartupRegistration.Set(previous.StartAtLogin));
            if (newPat.Length > 0) Restore(() => { if (previousPat is null) PatStore.Remove(); else PatStore.Save(previousPat); });
            if (recoveryErrors.Count > 0)
                throw new ReviewException("Settings were not saved, and Windows could not restore every previous setting. Reopen Settings and save again. " + UiErrors.Message(ex));
            throw;
        }
        authorization!.Invalidate();
        await Controller.ApplyAsync(settings);
        try { diagnostics!.Write(new("settingsSaved", "settings")); }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { Controller.Report("Settings were saved, but diagnostic logging failed. " + UiErrors.Message(ex)); }
    }
    public void MarkFirstClose()
    {
        try
        {
            var settings = Controller.Settings with { FirstCloseShown = true };
            settingsStore!.Save(settings);
            Controller.Settings.FirstCloseShown = true;
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { Controller.Report(UiErrors.Message(ex)); }
    }
    public async Task RunActionAsync(string operation, Func<Task> action)
    {
        if (launching && operation == "copilot") { Controller.Report("A review launch is already in progress. Wait for it to finish."); return; }
        if (operation == "copilot") launching = true;
        try
        {
            await action();
            diagnostics!.Write(new("actionFinished", operation, Outcome: "success"));
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (UiErrors.IsExpected(ex))
        {
            Controller.Report(UiErrors.Message(ex));
            diagnostics!.Write(new("actionFailed", operation, Outcome: "system"));
            ShowQueue();
        }
        finally { if (operation == "copilot") launching = false; }
    }
    private void UpdateStatus()
    {
        if (tray is null || queue is null) return;
        var text = Controller.Error is not null ? "ReviewQ - error; open queue" : Controller.Snapshot is { } snapshot ?
            $"ReviewQ - {snapshot.PendingCount} pending reviews" : "ReviewQ - waiting for refresh";
        tray.Text = text[..Math.Min(text.Length, 63)];
        queue.TaskbarItemInfo ??= new TaskbarItemInfo();
        queue.TaskbarItemInfo.ProgressState = Controller.Error is not null ? TaskbarItemProgressState.Error :
            Controller.IsRefreshing ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.None;
        queue.TaskbarItemInfo.ProgressValue = Controller.Error is not null ? 1 : 0;
        var count = Controller.Snapshot?.PendingCount ?? 0;
        var label = Controller.Error is not null ? "!" : count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        if (!Controller.Settings.ShowCount && Controller.Error is null) { queue.TaskbarItemInfo.Overlay = null; return; }
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawEllipse(System.Windows.Media.Brushes.DarkBlue, null, new System.Windows.Point(12, 12), 12, 12);
            var textImage = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), label.Length > 2 ? 10 : 14, System.Windows.Media.Brushes.White, 1);
            context.DrawText(textImage, new System.Windows.Point((24 - textImage.Width) / 2, (24 - textImage.Height) / 2));
        }
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze();
        queue.TaskbarItemInfo.Overlay = image;
    }
    public void Quit(int exitCode = 0)
    {
        IsQuitting = true;
        lifetime.Cancel();
        Controller?.Dispose();
        shell?.Dispose(); shell = null;
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); tray = null; }
        trayIcon?.Dispose(); trayIcon = null;
        SystemEvents.PowerModeChanged -= PowerChanged;
        Shutdown(exitCode);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        http?.Dispose();
        if (ownsMutex) { instance?.ReleaseMutex(); ownsMutex = false; }
        instance?.Dispose();
        lifetime.Dispose();
        if (smokeDirectory is not null)
        {
            try { Directory.Delete(smokeDirectory, true); }
            catch (Exception ex) when (UiErrors.IsExpected(ex))
            { e.ApplicationExitCode = 1; }
        }
        base.OnExit(e);
    }
    private string? smokeDirectory;
}
