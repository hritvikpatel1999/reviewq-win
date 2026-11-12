using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public sealed class SettingsWindow : Window
{
    private readonly TextBox sources = Multiline(90), tenant = new(), emails = new(), shortcut = new();
    private readonly TextBox copilot = Multiline(90), scout = Multiline(70);
    private readonly PasswordBox pat = new() { Padding = new(6), Margin = new(0, 3, 0, 10) };
    private readonly ComboBox auth = new() { ItemsSource = Enum.GetValues<AuthenticationMethod>(), Margin = new(0, 3, 0, 10) };
    private readonly ComboBox interval = new() { ItemsSource = new[] { 1, 5, 15, 30 }, Margin = new(0, 3, 0, 10) };
    private readonly CheckBox drafts = new() { Content = "Include draft pull requests" };
    private readonly CheckBox count = new() { Content = "Show pending count in window title" };
    private readonly CheckBox follow = new() { Content = "Show People you follow" };
    private readonly CheckBox authored = new() { Content = "Show PRs I've authored" };
    private readonly CheckBox startup = new() { Content = "Start ReviewQ at sign-in (in the background)" };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) };
    private readonly TextBlock counts = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button save = new() { Content = "_Save and Refresh", IsDefault = true };
    private readonly App app;
    private readonly ReviewSettings original;
    public SettingsWindow(App app, ReviewSettings settings)
    {
        this.app = app;
        original = settings;
        Title = "ReviewQ Settings";
#pragma warning disable WPF0001 // Use the same built-in system theme as the queue window.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Width = 580; Height = 780; MinWidth = 440; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(20) };
        var footer = new StackPanel();
        footer.Children.Add(validation); footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var form = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        form.Children.Add(new TextBlock { Text = "Azure DevOps", FontSize = 23, FontWeight = FontWeights.SemiBold });
        Add(form, "Organization/project URLs (one per line)", sources);
        form.Children.Add(Note("Example: https://dev.azure.com/your-organization/your-project"));
        Add(form, "Authentication", auth);
        form.Children.Add(Note("Azure CLI: install it separately, then run az login --allow-no-subscriptions. A PAT does not need Azure CLI."));
        Add(form, "Optional Entra tenant GUID or domain", tenant);
        Add(form, "New PAT (Code: Read; blank keeps the saved token)", pat);
        var remove = new Button { Content = "Remove saved PAT", HorizontalAlignment = HorizontalAlignment.Left };
        remove.Click += async (_, _) => await app.RunActionAsync("settings", () =>
        {
            if (MessageBox.Show(this, "Remove the saved PAT from Windows Credential Manager?", "Remove PAT",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { app.PatStore.Remove(); app.InvalidateAuthentication(); validation.Text = "Saved PAT removed."; }
            return Task.CompletedTask;
        });
        form.Children.Add(remove);
        Add(form, "Refresh interval (minutes)", interval);
        foreach (var control in new[] { drafts, count, follow }) form.Children.Add(control);
        Add(form, "Followed-author emails (semicolon separated, without spaces)", emails);
        form.Children.Add(authored);
        form.Children.Add(Note("Merge Ready checks reviewer approvals and discussion resolution only, not builds or all merge policies."));
        form.Children.Add(startup);
        Add(form, "Global shortcut (blank disables; e.g. Ctrl+Alt+Q)", shortcut);
        var retry = new Button { Content = "Retry saved shortcut", HorizontalAlignment = HorizontalAlignment.Left };
        retry.Click += (_, _) => { try { app.RegisterShortcut(original.GlobalShortcut); validation.Text = "Saved shortcut registered."; } catch (Exception ex) when (UiErrors.IsExpected(ex)) { validation.Text = UiErrors.Message(ex); } };
        form.Children.Add(retry);
        var lookup = new Button { Content = "Re-lookup account and followed identities", HorizontalAlignment = HorizontalAlignment.Left };
        lookup.Click += async (_, _) => await app.RunActionAsync("settings", async () =>
        { await app.Controller.ApplyAsync(app.Controller.Settings, true); validation.Text = "Identity cache cleared; refreshing."; });
        form.Children.Add(lookup);
        form.Children.Add(new TextBlock { Text = "Review instructions", FontSize = 20, Margin = new(0, 15, 0, 8) });
        form.Children.Add(Note("The first line is always Review PR: {PR Link}. Instructions are stored locally, not encrypted. Do not enter secrets."));
        Add(form, "VS Code Copilot additional instructions", copilot);
        Add(form, "Scout additional instructions (saved for future support)", scout);
        form.Children.Add(Note("Scout is disabled because a reliable Windows integration has not been verified. No blind typing or automatic submission is used."));
        form.Children.Add(counts);
        form.Children.Add(Note("Visibility: ReviewQ cannot force its tray icon out of Windows overflow. Drag it beside the clock or change taskbar tray settings. You can manually pin ReviewQ to the taskbar."));
        sources.Text = settings.SourceUrls; tenant.Text = settings.TenantId; emails.Text = settings.FollowedAuthorEmails;
        shortcut.Text = settings.GlobalShortcut; copilot.Text = settings.CopilotAdditionalInstructions; scout.Text = settings.ScoutAdditionalInstructions;
        auth.SelectedItem = settings.Authentication; interval.SelectedItem = settings.RefreshMinutes;
        drafts.IsChecked = settings.IncludeDrafts; count.IsChecked = settings.ShowCount; follow.IsChecked = settings.FollowPeople;
        authored.IsChecked = settings.ShowAuthoredPullRequests; startup.IsChecked = settings.StartAtLogin;
        copilot.TextChanged += (_, _) => ValidatePrompts();
        scout.TextChanged += (_, _) => ValidatePrompts();
        save.Click += SaveClick;
        ValidatePrompts();
    }
    private void ValidatePrompts()
    {
        counts.Text = $"Copilot: {ReviewPrompt.CharacterCount(copilot.Text):N0}/1,000 characters; Scout: {ReviewPrompt.CharacterCount(scout.Text):N0}/1,000 characters";
        try { ReviewPrompt.Validate(copilot.Text); ReviewPrompt.Validate(scout.Text); save.IsEnabled = true; validation.Text = ""; }
        catch (ReviewException ex) { validation.Text = ex.Message; save.IsEnabled = false; }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        save.IsEnabled = false;
        try
        {
            if (auth.SelectedItem is not AuthenticationMethod method || interval.SelectedItem is not int minutes)
                throw new ReviewException("Choose authentication and a refresh interval.");
            var settings = app.Controller.Settings with
            {
                SourceUrls = sources.Text, TenantId = tenant.Text, Authentication = method, RefreshMinutes = minutes,
                IncludeDrafts = drafts.IsChecked == true, ShowCount = count.IsChecked == true, FollowPeople = follow.IsChecked == true,
                FollowedAuthorEmails = emails.Text, ShowAuthoredPullRequests = authored.IsChecked == true,
                StartAtLogin = startup.IsChecked == true, GlobalShortcut = shortcut.Text.Trim(),
                CopilotAdditionalInstructions = copilot.Text, ScoutAdditionalInstructions = scout.Text
            };
            settings.Validate();
            await app.SaveSettingsAsync(settings, pat.Password);
            pat.Clear();
            Close();
        }
        catch (Exception ex) when (UiErrors.IsExpected(ex)) { validation.Text = UiErrors.Message(ex); }
        finally { save.IsEnabled = true; }
    }
    private static TextBox Multiline(int height) => new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 4, 0, 10) };
    private static void Add(Panel parent, string title, Control field)
    {
        parent.Children.Add(new Label { Content = title, Target = field, Padding = new(0, 6, 0, 0) });
        System.Windows.Automation.AutomationProperties.SetName(field, title);
        parent.Children.Add(field);
    }
}

public sealed class DiagnosticsWindow : Window
{
    private readonly TextBlock storage = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public DiagnosticsWindow(PrivateDiagnostics diagnostics)
    {
#pragma warning disable WPF0001 // Use the same built-in system theme as the queue window.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = "ReviewQ Diagnostics"; Width = 600; Height = 710; MinWidth = 420; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var heading = new DockPanel();
        var icon = UiIcons.Create("diagnostics", 36, true);
        icon.Margin = new(0, 0, 14, 0);
        DockPanel.SetDock(icon, Dock.Left); heading.Children.Add(icon);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = Title, FontSize = 25, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(Note("Local insight when a refresh does not go to plan."));
        heading.Children.Add(titles); panel.Children.Add(heading);
        var collection = Section(panel, "Collection");
        var verbose = new CheckBox { Content = "Enable verbose logging", IsChecked = diagnostics.Verbose, Margin = new(0, 0, 0, 8) };
        verbose.Checked += (_, _) => diagnostics.Verbose = true;
        verbose.Unchecked += (_, _) => diagnostics.Verbose = false;
        collection.Children.Add(verbose);
        collection.Children.Add(Note("Startup information, refreshes, request outcomes, timing, cancellations, and action failures are saved by default. Verbose logging also records request starts. It is optional and resets when ReviewQ exits."));
        var usage = new DockPanel { Margin = new(0, 15, 0, 0) };
        DockPanel.SetDock(storage, Dock.Right); usage.Children.Add(storage);
        usage.Children.Add(new TextBlock { Text = "Log storage", VerticalAlignment = VerticalAlignment.Center });
        collection.Children.Add(usage);
        var logs = Section(panel, "Local logs");
        var buttons = new WrapPanel { Margin = new(0, 0, 0, 5) };
        var export = ActionButton("export", "Export Logs...");
        export.Click += (_, _) => Execute("Export logs", () =>
        {
            var dialog = new SaveFileDialog { Filter = "ZIP archive|*.zip", FileName = $"ReviewQ-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
            if (dialog.ShowDialog(this) == true)
            {
                if (File.Exists(dialog.FileName))
                    throw new ReviewException("Choose a new diagnostic filename; exports do not overwrite existing files.");
                diagnostics.Export(dialog.FileName);
                status.Text = "Diagnostic ZIP exported. Review its contents before sharing.";
            }
        });
        var show = ActionButton("folder", "Show in Explorer");
        show.Click += (_, _) => Execute("Show logs in Explorer", diagnostics.ShowInExplorer);
        var clear = ActionButton("trash", "Clear Logs...");
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete ReviewQ's current log and rotated backups? This cannot be undone. Settings, credentials, the identity cache, and previously exported ZIPs are not removed.",
                "Clear local logs?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Execute("Clear logs", () =>
            {
                diagnostics.Clear();
                status.Text = "Local logs cleared. New activity may create new records.";
            });
        };
        buttons.Children.Add(export); buttons.Children.Add(show); buttons.Children.Add(clear); logs.Children.Add(buttons);
        logs.Children.Add(Note("Only ReviewQ's current log, up to three rotated backups, and a version summary are exported. Nothing is uploaded."));
        var privacy = Section(panel, "Privacy and retention");
        privacy.Children.Add(Note("Credentials, PR titles and comments, identities, private URLs, prompts, and settings are never recorded. Logs do include usage timestamps; exports also include app/OS versions and architecture."));
        privacy.Children.Add(Note("Each log file is capped at 1 MiB (4 MiB combined). Older records are replaced as the files rotate; Windows logs do not currently expire by age. Turning verbose logging off stops future optional collection; use Clear Logs to remove existing records. Exported ZIPs are separate and must be deleted yourself."));
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(storage, "Log storage");
        panel.Children.Add(status);
        timer.Tick += (_, _) => Execute("Read log storage", () => { });
        IsVisibleChanged += (_, _) => { if (IsVisible) { Execute("Read log storage", () => { }); timer.Start(); } else timer.Stop(); };
        Closed += (_, _) => timer.Stop();

        void Execute(string operation, Action action)
        {
            try
            {
                action();
                var bytes = diagnostics.StorageBytes;
                storage.Text = bytes < 1024 ? $"{bytes} B" : bytes < 1_048_576 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes / 1_048_576d:0.##} MiB";
                storage.ToolTip = $"{bytes:N0} bytes in local log files";
            }
            catch (Exception ex) when (UiErrors.IsExpected(ex))
            {
                status.Text = $"{operation} failed. {UiErrors.Message(ex)}";
            }
        }
    }
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new(0, 5, 0, 5), LineHeight = 20 };
    private static StackPanel Section(Panel parent, string title)
    {
        parent.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, 19, 0, 9) });
        var body = new StackPanel();
        parent.Children.Add(new Border { Child = body, Padding = new(16), CornerRadius = new(10), BorderThickness = new(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128)), Background = new SolidColorBrush(Color.FromArgb(10, 128, 128, 128)) });
        return body;
    }
    private static Button ActionButton(string icon, string label)
    {
        var button = new Button { Content = UiIcons.Label(icon, label), Padding = new(10, 8, 10, 8), Margin = new(0, 0, 8, 8) };
        AutomationProperties.SetName(button, label);
        return button;
    }
}
