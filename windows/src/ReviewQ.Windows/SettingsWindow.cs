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
    private readonly TextBox sources = Multiline(118), tenant = new(), emails = new(), shortcut = new();
    private readonly TextBox copilot = Multiline(100), scout = Multiline(100);
    private readonly PasswordBox pat = new() { Padding = new(6), Margin = new(0, 3, 0, 10) };
    private AuthenticationMethod authentication;
    private readonly ComboBox interval = new() { ItemsSource = new[] { 1, 5, 15, 30 }, Margin = new(0, 3, 0, 10) };
    private readonly CheckBox drafts = new() { Content = "Include draft pull requests" };
    private readonly CheckBox count = new() { Content = "Show pending count in window title" };
    private readonly CheckBox follow = new() { Content = "Show People you follow" };
    private readonly CheckBox authored = new() { Content = "Show PRs I've authored" };
    private readonly CheckBox startup = new() { Content = "Start ReviewQ at sign-in (in the background)" };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) };
    private readonly TextBlock copilotCount = Note(""), scoutCount = Note("");
    private readonly Button save = new() { Content = "_Save and Refresh", IsDefault = true };
    private readonly App app;
    private readonly ReviewSettings original;
    private readonly UpdateStore? updates;
    private readonly ReminderEditor reminders;
    private readonly TextBlock updateStatus = Note("");
    private readonly Button checkUpdates = new() { Content = "Check for Updates", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button viewUpdates = new() { Content = "View Updates", HorizontalAlignment = HorizontalAlignment.Left };
    public SettingsWindow(App app, ReviewSettings settings, UpdateStore? updates = null)
    {
        this.app = app;
        original = settings;
        this.updates = updates ?? app.Updates;
        Title = "ReviewQ Settings";
        FontSize = 14;
        Resources.Add(typeof(TextBox), FindResource("SettingsInput"));
#pragma warning disable WPF0001 // Use the same built-in system theme as the queue window.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Width = Math.Min(640, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
        MinWidth = 440; MinHeight = 400;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel();
        var footer = new StackPanel();
        validation.Margin = new(0, 0, 0, 6);
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite);
        footer.Children.Add(validation);
        var navigation = new Grid();
        navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        navigation.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var diagnostics = Button("Diagnostics...");
        diagnostics.Click += (_, _) => app.ShowDiagnostics();
        navigation.Children.Add(diagnostics);
        save.SetResourceReference(StyleProperty, "SettingsPrimaryButton");
        AutomationProperties.SetName(save, "Save and Refresh");
        Grid.SetColumn(save, 2); navigation.Children.Add(save);
        footer.Children.Add(navigation);
        var footerSurface = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), BorderThickness = new(0, 1, 0, 0),
            Padding = new(20, 10, 20, 14), Child = footer
        };
        DockPanel.SetDock(footerSurface, Dock.Bottom); root.Children.Add(footerSurface);
        var form = new StackPanel { Margin = new(26, 22, 26, 20) };
        root.Children.Add(new ScrollViewer
        {
            Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        Content = root;
        var heading = new DockPanel { Margin = new(2, 0, 0, 2) };
        var icon = UiIcons.Create("app", 34, true);
        icon.Margin = new(0, 0, 16, 0); DockPanel.SetDock(icon, Dock.Left); heading.Children.Add(icon);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Azure DevOps reviews", FontSize = 23, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(Note("A quiet home for your next pull request."));
        heading.Children.Add(titles); form.Children.Add(heading);

        var sourceSection = Section(form, "sources", "Review sources");
        sourceSection.Children.Add(Note("One organization or project URL per line."));
        sources.FontFamily = new FontFamily("Consolas");
        Add(sourceSection, "Organization/project URLs (one per line)", sources, false);
        sourceSection.Children.Add(Note("Organization-only URLs search all accessible projects. Project URLs are faster for large organizations."));

        var authenticationSection = Section(form, "key", "Authentication");
        var segments = new Grid { Margin = new(0, 0, 0, 8) };
        segments.ColumnDefinitions.Add(new()); segments.ColumnDefinitions.Add(new());
        var cli = new RadioButton { Content = "Azure CLI", GroupName = "SettingsAuthentication", HorizontalContentAlignment = HorizontalAlignment.Center };
        var token = new RadioButton { Content = "Personal access token", GroupName = "SettingsAuthentication", HorizontalContentAlignment = HorizontalAlignment.Center };
        cli.SetResourceReference(StyleProperty, "SetupSegment"); token.SetResourceReference(StyleProperty, "SetupSegment");
        AutomationProperties.SetName(cli, "Azure CLI"); AutomationProperties.SetName(token, "Personal access token");
        segments.Children.Add(cli); Grid.SetColumn(token, 1); segments.Children.Add(token);
        authenticationSection.Children.Add(segments);
        var cliFields = new StackPanel();
        cliFields.Children.Add(Note("Sign in once in Terminal; ReviewQ uses short-lived Entra tokens."));
        cliFields.Children.Add(new TextBlock { Text = "az login --allow-no-subscriptions", FontFamily = new FontFamily("Consolas"), Margin = new(0, 4, 0, 8) });
        Add(cliFields, "Tenant ID or domain (optional)", tenant);
        cliFields.Children.Add(Note("Leave the tenant blank to use your current Azure CLI account."));
        authenticationSection.Children.Add(cliFields);
        var patFields = new StackPanel();
        patFields.Children.Add(Note("Use a PAT with Code (Read) and access to your projects. It is stored in Windows Credential Manager, not settings."));
        Add(patFields, "New PAT (blank keeps the saved token)", pat);
        AutomationProperties.SetLiveSetting(updateStatus, AutomationLiveSetting.Polite);
        var remove = Button("Remove saved PAT");
        remove.Click += async (_, _) => await app.RunActionAsync("settings", async () =>
        {
            if (MessageBox.Show(this, "Remove the saved PAT from Windows Credential Manager?", "Remove PAT",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { await app.RemoveSavedPatAsync(); validation.Text = "Saved PAT and prior startup queue removed."; }
        });
        patFields.Children.Add(remove); authenticationSection.Children.Add(patFields);
        void SelectAuthentication(AuthenticationMethod method)
        {
            authentication = method;
            cliFields.Visibility = method == AuthenticationMethod.AzureCli ? Visibility.Visible : Visibility.Collapsed;
            patFields.Visibility = method == AuthenticationMethod.PersonalAccessToken ? Visibility.Visible : Visibility.Collapsed;
        }
        cli.Checked += (_, _) => SelectAuthentication(AuthenticationMethod.AzureCli);
        token.Checked += (_, _) => SelectAuthentication(AuthenticationMethod.PersonalAccessToken);
        authentication = settings.Authentication;
        cli.IsChecked = authentication == AuthenticationMethod.AzureCli;
        token.IsChecked = authentication == AuthenticationMethod.PersonalAccessToken;

        var people = Section(form, "people", "People you follow");
        people.Children.Add(follow);
        Add(people, "Followed-author emails", emails, false);
        people.Children.Add(Note("Enter email addresses separated by semicolons, with no spaces."));
        people.Children.Add(Note("Off by default. Adds active PRs by these authors across accessible projects in your configured organizations."));
        people.Children.Add(Note("Adds read-only requests only when enabled. PATs also need Identity (Read) and Project and Team (Read). Emails stay local and go only to Azure DevOps to find authors."));
        var authoredSection = Section(form, "author", "I'm the Author");
        authoredSection.Children.Add(authored);
        authoredSection.Children.Add(Note("Off by default. Shows your active PRs across accessible projects. The draft preference also applies."));
        authoredSection.Children.Add(Note("Shows active discussion counts and Merge Ready for required approvals with no active comments, not builds or all merge policies. Browser actions only."));
        authoredSection.Children.Add(Note("Adds read-only PR, discussion, and reviewer-policy requests only when enabled. PATs need Code (Read), Identity (Read), and Project and Team (Read)."));

        var instructions = Section(form, "logs", "Review instructions");
        var firstLine = Note("Always sent first: Review PR: {PR Link}"); firstLine.FontFamily = new FontFamily("Consolas");
        instructions.Children.Add(firstLine);
        Prompt(instructions, "Scout", scout, scoutCount);
        Prompt(instructions, "GitHub Copilot", copilot, copilotCount);
        instructions.Children.Add(Note("Stored locally in preferences, not encrypted. Do not enter secrets."));
        instructions.Children.Add(Note("Scout starts a new chat and verifies the prompt before sending. Existing drafts are never overwritten."));

        var reviewQueue = Section(form, "repository", "Review queue");
        var refresh = new DockPanel();
        var refreshLabel = new Label { Content = "Refresh", Target = interval, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
        DockPanel.SetDock(refreshLabel, Dock.Left); refresh.Children.Add(refreshLabel);
        interval.ItemStringFormat = "Every {0} minutes"; interval.HorizontalAlignment = HorizontalAlignment.Left;
        interval.MinWidth = 160; AutomationProperties.SetName(interval, "Refresh interval (minutes)");
        refresh.Children.Add(interval); reviewQueue.Children.Add(refresh);
        count.Content = "Show pending count in the taskbar and tray";
        reviewQueue.Children.Add(count); reviewQueue.Children.Add(drafts);
        reviewQueue.Children.Add(Note("Repository sections show active, directly assigned PRs with no vote from you. Followed and authored PRs appear in separate optional sections."));
        reviewQueue.Children.Add(new Separator { Margin = new(0, 6, 0, 6) });
        Add(reviewQueue, "Global shortcut (blank disables; e.g. Ctrl+Alt+Q)", shortcut);
        var retry = Button("Retry saved shortcut");
        retry.Click += (_, _) => { try { app.RegisterShortcut(original.GlobalShortcut); validation.Text = "Saved shortcut registered."; } catch (Exception ex) when (UiErrors.IsExpected(ex)) { validation.Text = UiErrors.Message(ex); } };
        reviewQueue.Children.Add(retry);
        reviewQueue.Children.Add(Note("Drag ReviewQ beside the clock to keep its tray icon visible. Windows does not allow apps to force it out of overflow."));

        reminders = new(app, settings.Reminders, _ => { });
        form.Children.Add(reminders);
        var startupSection = Section(form, "power", "Startup");
        var startupText = (string)startup.Content;
        startup.Content = new TextBlock { Text = startupText, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(startup, startupText);
        startupSection.Children.Add(startup);
        startupSection.Children.Add(Note("Starts quietly in the notification area when you sign in to Windows. Closing the queue keeps ReviewQ running; Quit ends it."));
        var identity = Section(form, "people", "Identity cache");
        identity.Children.Add(Note("Your identity and resolved followed authors are cached locally for seven days, including across restarts. No-match results expire after 15 minutes."));
        identity.Children.Add(Note("PRs, votes, and comments are checked on every refresh. Ordinary refreshes reuse valid identities."));
        var lookup = Button("Refresh identities");
        lookup.Click += async (_, _) => await app.RunActionAsync("settings", async () =>
        { await app.Controller.ApplyAsync(app.Controller.Settings, true); validation.Text = "Identity cache cleared; refreshing."; });
        identity.Children.Add(lookup);
        var cache = Section(form, "folder", "Startup queue cache");
        cache.Children.Add(Note("Local, unencrypted PR display data keeps the last queue visible while fresh data loads. It expires after seven days and holds at most 4,096 PRs / 2 MiB."));
        cache.Children.Add(Note("Excluded from diagnostic exports. Saving Settings clears the old cache."));
        var clearCache = Button("Clear startup queue cache");
        clearCache.Click += async (_, _) => await app.RunActionAsync("settings", () =>
        { app.Controller.ClearStartupCache(); validation.Text = "Startup queue cache cleared. The live queue is unchanged."; return Task.CompletedTask; });
        cache.Children.Add(clearCache);
        var appUpdates = Section(form, "export", "App updates");
        appUpdates.Children.Add(Note("Checks public Windows releases about once a day, independently of Azure DevOps. Updates are installed manually; nothing is downloaded or installed automatically."));
        appUpdates.Children.Add(updateStatus);
        var updateActions = new WrapPanel();
        foreach (var button in new[] { checkUpdates, viewUpdates })
        { button.SetResourceReference(StyleProperty, "SettingsButton"); button.Margin = new(0, 4, 8, 0); updateActions.Children.Add(button); }
        checkUpdates.Click += async (_, _) =>
        { if (this.updates is not null) await this.updates.CheckAsync(manually: true, ct: app.Lifetime); };
        viewUpdates.Click += async (_, _) =>
        {
            if (this.updates?.AvailableUpdate is { } release)
                await app.RunActionAsync("browser", () => { ReviewLauncher.OpenWeb(release.ReleaseUrl); return Task.CompletedTask; });
        };
        appUpdates.Children.Add(updateActions);
        if (this.updates is not null) this.updates.Changed += RenderUpdates;
        Closed += (_, _) => { if (this.updates is not null) this.updates.Changed -= RenderUpdates; };
        RenderUpdates();
        sources.Text = settings.SourceUrls; tenant.Text = settings.TenantId; emails.Text = settings.FollowedAuthorEmails;
        shortcut.Text = settings.GlobalShortcut; copilot.Text = settings.CopilotAdditionalInstructions; scout.Text = settings.ScoutAdditionalInstructions;
        interval.SelectedItem = settings.RefreshMinutes;
        drafts.IsChecked = settings.IncludeDrafts; count.IsChecked = settings.ShowCount; follow.IsChecked = settings.FollowPeople;
        authored.IsChecked = settings.ShowAuthoredPullRequests; startup.IsChecked = settings.StartAtLogin;
        copilot.TextChanged += (_, _) => ValidatePrompts();
        scout.TextChanged += (_, _) => ValidatePrompts();
        save.Click += SaveClick;
        ValidatePrompts();
    }
    private void RenderUpdates()
    {
        var version = updates?.CurrentVersion ?? "development build";
        var result = updates?.AvailableUpdate is { } release ? $"ReviewQ v{release.Version} is available." :
            updates?.LastCheckedAt is not null && updates.Error is null ? "You're up to date." : "";
        updateStatus.Text = $"Installed: ReviewQ {version}\n" +
            (updates?.IsChecking == true ? "Checking for ReviewQ updates...\n" : result + "\n") +
            (updates?.LastCheckedAt is { } checkedAt ? $"Last successful check: {checkedAt.LocalDateTime:g}" : "Not checked yet.") +
            (updates?.Error is { } error ? "\n" + error : "");
        checkUpdates.IsEnabled = updates?.CurrentVersion is not null && !updates.IsChecking;
        viewUpdates.Visibility = updates?.AvailableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ValidatePrompts()
    {
        copilotCount.Text = $"{ReviewPrompt.CharacterCount(copilot.Text):N0} of 1,000 characters";
        scoutCount.Text = $"{ReviewPrompt.CharacterCount(scout.Text):N0} of 1,000 characters";
        try { ReviewPrompt.Validate(copilot.Text); ReviewPrompt.Validate(scout.Text); save.IsEnabled = true; validation.Text = ""; }
        catch (ReviewException ex) { validation.Text = ex.Message; save.IsEnabled = false; }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        save.IsEnabled = false;
        try
        {
            if (interval.SelectedItem is not int minutes)
                throw new ReviewException("Choose a refresh interval.");
            var settings = app.Controller.Settings with
            {
                SourceUrls = sources.Text, TenantId = tenant.Text, Authentication = authentication, RefreshMinutes = minutes,
                IncludeDrafts = drafts.IsChecked == true, ShowCount = count.IsChecked == true, FollowPeople = follow.IsChecked == true,
                FollowedAuthorEmails = emails.Text, ShowAuthoredPullRequests = authored.IsChecked == true,
                StartAtLogin = startup.IsChecked == true, GlobalShortcut = shortcut.Text.Trim(),
                CopilotAdditionalInstructions = copilot.Text, ScoutAdditionalInstructions = scout.Text, Reminders = reminders.Draft
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
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 4, 0, 10) };
    private static void Add(Panel parent, string title, Control field, bool showLabel = true)
    {
        if (showLabel) parent.Children.Add(new Label { Content = title, Target = field, Padding = new(0, 6, 0, 4) });
        System.Windows.Automation.AutomationProperties.SetName(field, title);
        parent.Children.Add(field);
    }
    private static Button Button(string title)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(StyleProperty, "SettingsButton");
        AutomationProperties.SetName(button, title);
        return button;
    }
    private static StackPanel Section(Panel parent, string icon, string title)
    {
        var section = new StackPanel { Margin = new(0, 20, 0, 0) };
        var header = UiIcons.Label(icon, title); header.Margin = new(4, 0, 0, 7); header.Opacity = 0.85;
        ((TextBlock)header.Children[1]).FontWeight = FontWeights.SemiBold;
        section.Children.Add(header);
        var content = new StackPanel();
        var card = new Border { Child = content, Tag = title };
        card.SetResourceReference(StyleProperty, "SettingsCard");
        section.Children.Add(card); parent.Children.Add(section);
        return content;
    }
    private static void Prompt(Panel parent, string title, TextBox field, TextBlock counter)
    {
        var header = new DockPanel { Margin = new(0, 8, 0, 4) };
        counter.FontSize = 11; counter.Margin = new(8, 0, 0, 0);
        counter.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(counter, Dock.Right); header.Children.Add(counter);
        header.Children.Add(new Label { Content = title, Target = field, FontWeight = FontWeights.SemiBold, Padding = new(0) });
        parent.Children.Add(header);
        field.FontFamily = new FontFamily("Consolas");
        Add(parent, title + " additional instructions", field, false);
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
