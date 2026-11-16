using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public sealed class SetupWindow : Window
{
    private readonly SetupSession session;
    private readonly App app;
    private readonly bool canKeepExistingSettings;
    private readonly StackPanel form = new();
    private readonly TextBlock heading = new()
    {
        FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, Margin = new(0, 0, 0, 10)
    };
    private readonly TextBlock subtitle = Note("");
    private readonly TextBlock status = Note("");
    private readonly TextBlock progress = Note("");
    private TextBlock connectionStatus = Note("");
    private FrameworkElement? connectionSuccess;
    private readonly Border[] steps = new Border[6];
    private readonly Button back = new() { Content = "_Back" };
    private readonly Button next = new() { Content = "_Continue", IsDefault = true };
    private readonly Button keep = new() { Content = "Keep existing settings" };
    private readonly Button skip = new() { Content = "Skip for now", Margin = new(0, 0, 16, 0) };
    private readonly ScrollViewer scroll;
    private SetupStep? rendered;
    private PasswordBox? password;
    private StackPanel? cliFields;
    private StackPanel? patFields;
    private Button? test;
    private bool completed;

    public SetupWindow(App app, SetupSession session, bool canKeepExistingSettings)
    {
        this.app = app; this.session = session;
        this.canKeepExistingSettings = canKeepExistingSettings;
        Title = "ReviewQ Setup";
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Width = Math.Min(800, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(780, SystemParameters.WorkArea.Height - 40);
        MinWidth = 540; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        var root = new DockPanel { Margin = new(28, 24, 28, 20) };
        var header = new Grid { Margin = new(0, 0, 0, 18) };
        var stepLabels = new StackPanel();
        var markers = new StackPanel { Orientation = Orientation.Horizontal };
        for (var index = 0; index < steps.Length; index++)
        {
            steps[index] = new Border { Width = 24, Height = 4, CornerRadius = new(2), Margin = new(0, 0, 6, 0) };
            AutomationProperties.SetName(steps[index], $"Setup step {index + 1}");
            markers.Children.Add(steps[index]);
        }
        stepLabels.Children.Add(markers);
        progress.FontSize = 11; progress.Margin = new(0, 7, 0, 0);
        stepLabels.Children.Add(progress); header.Children.Add(stepLabels);
        header.Children.Add(new TextBlock
        {
            Text = "ReviewQ", FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new(0, 14, 0, 0) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(connectionStatus, AutomationLiveSetting.Polite);
        footer.Children.Add(status);
        var navigation = new Grid();
        navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        navigation.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        back.SetResourceReference(StyleProperty, "SetupLinkButton");
        keep.SetResourceReference(StyleProperty, "SetupLinkButton");
        skip.SetResourceReference(StyleProperty, "SetupLinkButton");
        next.SetResourceReference(StyleProperty, "SetupPrimaryButton");
        back.Margin = new(0, 0, 14, 0); keep.FontSize = 11;
        navigation.Children.Add(back);
        Grid.SetColumn(keep, 1); navigation.Children.Add(keep);
        Grid.SetColumn(skip, 3); navigation.Children.Add(skip);
        Grid.SetColumn(next, 4); navigation.Children.Add(next);
        footer.Children.Add(navigation);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 24, 8, 24) };
        subtitle.TextAlignment = TextAlignment.Center; subtitle.FontSize = 15; subtitle.Margin = new(0, 0, 0, 28);
        body.Children.Add(heading); body.Children.Add(subtitle); body.Children.Add(form);
        scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(scroll); Content = root;
        back.Click += (_, _) => session.Back();
        skip.Click += async (_, _) => await session.AdvanceAsync();
        next.Click += async (_, _) =>
        {
            if (session.Step == SetupStep.Ready)
            {
                if (await session.CompleteAsync()) { completed = true; Close(); app.ShowQueue(); }
            }
            else await session.AdvanceAsync();
        };
        keep.Click += (_, _) =>
        {
            try { session.KeepExisting(); completed = true; Close(); }
            catch (Exception ex) when (UiErrors.IsExpected(ex)) { status.Text = UiErrors.Message(ex); status.Visibility = Visibility.Visible; }
        };
        Closing += OnClosing;
        Closed += (_, _) => { session.Changed -= Update; form.Children.Clear(); };
        session.Changed += Update;
        Update();
    }

    private void Update()
    {
        progress.Text = $"Step {(int)session.Step + 1} of 6";
        for (var index = 0; index < steps.Length; index++)
        {
            steps[index].SetResourceReference(Border.BackgroundProperty, index == (int)session.Step
                ? SystemColors.HighlightBrushKey : SystemColors.GrayTextBrushKey);
            steps[index].Opacity = index == (int)session.Step ? 1 : 0.4;
            steps[index].Width = index == (int)session.Step ? 32 : 16;
        }
        status.Text = session.IsChecking ? "Checking your current connection..." : session.Error ?? session.Info ?? "";
        status.Visibility = status.Text.Length > 0 && session.Step != SetupStep.Authentication ? Visibility.Visible : Visibility.Collapsed;
        UpdateConnectionStatus();
        back.Visibility = session.Step == SetupStep.Welcome ? Visibility.Collapsed : Visibility.Visible;
        back.IsEnabled = !session.IsChecking && !session.IsSaving;
        keep.Visibility = canKeepExistingSettings ? Visibility.Visible : Visibility.Collapsed;
        keep.IsEnabled = !session.IsChecking && !session.IsSaving;
        skip.Visibility = session.Step == SetupStep.Companions ? Visibility.Visible : Visibility.Collapsed;
        skip.IsEnabled = !session.IsChecking && !session.IsSaving;
        next.IsEnabled = !session.IsChecking && !session.IsSaving &&
            (session.Step != SetupStep.Authentication || session.HasVerifiedAccount);
        next.Content = session.Step == SetupStep.Welcome ? "_Get started" :
            session.Step == SetupStep.Ready ? "_Open my queue" :
            session.Step == SetupStep.Preferences && !session.HasVerifiedQueue ? "_Check and continue" : "_Continue";
        form.IsEnabled = !session.IsChecking && !session.IsSaving;
        if (test is not null) test.Content = session.IsChecking ? "Checking..." : session.HasVerifiedAccount ? "Test again" : "_Test connection";
        if (cliFields is not null) cliFields.Visibility = session.Draft.Authentication == AuthenticationMethod.AzureCli ? Visibility.Visible : Visibility.Collapsed;
        if (patFields is not null) patFields.Visibility = session.Draft.Authentication == AuthenticationMethod.PersonalAccessToken ? Visibility.Visible : Visibility.Collapsed;
        if (rendered == session.Step) return;
        rendered = session.Step;
        ClearPassword(); cliFields = null; patFields = null; test = null; connectionSuccess = null;
        form.Children.Clear(); scroll.ScrollToTop();
        switch (session.Step)
        {
            case SetupStep.Welcome: Welcome(); break;
            case SetupStep.Sources: Sources(); break;
            case SetupStep.Authentication: Authentication(); break;
            case SetupStep.Companions: Companions(); break;
            case SetupStep.Preferences: Preferences(); break;
            case SetupStep.Ready: Ready(); break;
        }
    }

    private void Welcome()
    {
        heading.Text = "Your reviews. One quiet home.";
        subtitle.Text = "The pull requests that need you, right in your taskbar and tray.";
        var card = Card();
        var label = UiIcons.Label("repository", "Example queue");
        label.Children.Add(new TextBlock { Text = "2 to review", Margin = new(24, 0, 0, 0), Opacity = 0.6 });
        card.Children.Add(label); Separator(card);
        var repository = UiIcons.Label("repository", "Service");
        repository.Margin = new(0, 0, 0, 4); card.Children.Add(repository);
        card.Children.Add(Note("example / Project", 12));
        Preview(card, "First Review", "Improve request validation");
        Preview(card, "First Review", "Handle retries reliably");
        CenteredNote("Grouped by repository. Newest first. One click to review.");
        CenteredNote("Read-only Azure DevOps access. Closing setup saves non-secret choices, never an unsaved PAT or verification.", 11);
        CenteredNote("The local unencrypted startup cache contains PR display data, not credentials, discussion bodies or assistant prompts.", 11);
    }

    private void Sources()
    {
        heading.Text = "Where do you review?";
        subtitle.Text = "Add the organizations or projects you want in your queue.";
        var card = Card();
        card.Children.Add(UiIcons.Label("sources", "Review sources"));
        card.Children.Add(Note("One organization or project URL per line."));
        var sources = Text(card, "Azure DevOps sources", session.Draft.SourceUrls,
            value => session.Edit(session.Draft with { SourceUrls = value }), 150);
        sources.FontFamily = new("Cascadia Mono, Consolas");
        card.Children.Add(Note("Example: https://dev.azure.com/your-organization/your-project", 12));
        card.Children.Add(Note("A project URL keeps discovery focused and is faster for large organizations. An organization-only URL searches all accessible projects.", 11));
    }

    private void Authentication()
    {
        connectionStatus = Note("");
        AutomationProperties.SetLiveSetting(connectionStatus, AutomationLiveSetting.Polite);
        var successIcon = UiIcons.Create("success", 16);
        connectionSuccess = successIcon;
        successIcon.Margin = new(0, 0, 8, 0);
        successIcon.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(successIcon, "Connection verified");
        UpdateConnectionStatus();
        heading.Text = "Connect Azure DevOps";
        subtitle.Text = "Use your Azure CLI sign-in or a read-only personal access token.";
        var card = Card();
        var methods = new Grid { Margin = new(0, 0, 0, 16) };
        methods.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        methods.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        methods.Children.Add(new TextBlock { Text = "Authentication", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) });
        var choices = new WrapPanel();
        foreach (var method in Enum.GetValues<AuthenticationMethod>())
        {
            var choice = new RadioButton
            {
                Content = method == AuthenticationMethod.AzureCli ? "Azure CLI - recommended" : "Personal access token",
                GroupName = "Authentication", IsChecked = session.Draft.Authentication == method
            };
            choice.SetResourceReference(StyleProperty, "SetupSegment");
            AutomationProperties.SetName(choice, choice.Content.ToString());
            choice.Checked += (_, _) => session.Edit(session.Draft with { Authentication = method });
            choices.Children.Add(choice);
        }
        Grid.SetColumn(choices, 1); methods.Children.Add(choices); card.Children.Add(methods);
        cliFields = new StackPanel { Visibility = session.Draft.Authentication == AuthenticationMethod.AzureCli ? Visibility.Visible : Visibility.Collapsed };
        cliFields.Children.Add(Note("Sign in once in PowerShell. ReviewQ uses short-lived Microsoft Entra tokens."));
        var command = new Grid();
        command.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        command.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        command.Children.Add(new TextBlock
        {
            Text = "az login --allow-no-subscriptions", FontFamily = new("Cascadia Mono, Consolas"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0)
        });
        var copy = new Button { Content = "Copy" }; copy.SetResourceReference(StyleProperty, "SetupButton");
        AutomationProperties.SetName(copy, "Copy Azure CLI sign-in command");
        copy.Click += (_, _) =>
        {
            successIcon.Visibility = Visibility.Collapsed;
            try
            {
                var settings = session.Draft with { FollowPeople = false };
                settings.Validate();
                Clipboard.SetText("az login --allow-no-subscriptions" +
                    (settings.TenantId.Trim().Length > 0 ? " --tenant " + settings.TenantId.Trim() : ""));
                connectionStatus.Text = "Sign-in command copied. Run it in PowerShell, then test your connection.";
            }
            catch (Exception ex) when (UiErrors.IsExpected(ex) || ex is System.Runtime.InteropServices.COMException)
            { connectionStatus.Text = ex is System.Runtime.InteropServices.COMException ? "The clipboard is busy. Copy the displayed command manually." : UiErrors.Message(ex); }
        };
        Grid.SetColumn(copy, 1); command.Children.Add(copy);
        cliFields.Children.Add(new Border { Background = Surface(), CornerRadius = new(10), Padding = new(12), Margin = new(0, 0, 0, 12), Child = command });
        Text(cliFields, "Tenant ID or domain (optional)", session.Draft.TenantId, value => session.Edit(session.Draft with { TenantId = value }));
        cliFields.Children.Add(Note("Install Azure CLI separately. For tenant-specific access, Copy includes your tenant. Organization access policies still apply.", 11));
        card.Children.Add(cliFields);
        patFields = new StackPanel { Visibility = session.Draft.Authentication == AuthenticationMethod.PersonalAccessToken ? Visibility.Visible : Visibility.Collapsed };
        patFields.Children.Add(Note("Use a personal access token with Code (Read) and access to your projects."));
        password = new PasswordBox { Password = session.Token };
        Field(patFields, "New PAT (blank keeps your saved token)", password);
        password.PasswordChanged += PasswordChanged;
        patFields.Children.Add(Note("Kept in memory until you finish, then saved to Windows Credential Manager. Never stored in setup progress.", 11));
        card.Children.Add(patFields);
        Separator(card);
        var connection = new Grid();
        connection.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        connection.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        connection.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        connection.Children.Add(successIcon);
        connectionStatus.Margin = new(0, 0, 14, 0); connectionStatus.FontSize = 12; connectionStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(connectionStatus, 1);
        connection.Children.Add(connectionStatus);
        test = new Button { Content = session.HasVerifiedAccount ? "Test again" : "_Test connection", VerticalAlignment = VerticalAlignment.Center };
        test.SetResourceReference(StyleProperty, "SetupButton");
        AutomationProperties.SetName(test, "Test connection");
        test.Click += async (_, _) => await session.TestAsync();
        Grid.SetColumn(test, 2); connection.Children.Add(test); card.Children.Add(connection);
    }

    private void Companions()
    {
        heading.Text = "Your review companions";
        subtitle.Text = "Optional shortcuts. Your browser is always enough to get started.";
        var card = Card();
        var codeStatus = Companion(card, "code", "VS Code", "Opens a new Copilot Agent chat. Sign in to Copilot inside VS Code.");
        Separator(card);
        var scoutStatus = Companion(card, "scout", "Scout", "Starts a new review chat using Windows accessibility controls. Sign in inside Scout.");
        AutomationProperties.SetName(scoutStatus, "Scout installation status");
        Separator(card);
        var edgeStatus = Companion(card, "edge", "Microsoft Edge", "An optional browser shortcut. PR titles always open in your default browser.");
        void Detect()
        {
            try { _ = ReviewLauncher.FindCode(); codeStatus.Text = "Detected"; codeStatus.ToolTip = "VS Code found. Sign into Copilot inside VS Code."; }
            catch (ReviewException ex) { codeStatus.Text = "Not detected"; codeStatus.ToolTip = ex.Message; }
            var scout = ReviewLauncher.FindScout();
            scoutStatus.Text = scout is not null ? "Detected" : "Not detected";
            scoutStatus.ToolTip = scout is not null
                ? $"Microsoft Scout found at {scout}. ReviewQ verifies the prompt before sending it in a new chat."
                : "Microsoft Scout was not found in the standard per-user or system installation folders. Install it, then recheck.";
            var edge = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData }.Any(folder =>
                File.Exists(Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe")));
            edgeStatus.Text = edge ? "Detected" : "Not detected";
            edgeStatus.ToolTip = edge ? "Microsoft Edge found." : "Use your default browser or install Microsoft Edge.";
        }
        Detect();
        var detect = new Button { Content = "Recheck installed companions", HorizontalAlignment = HorizontalAlignment.Center };
        detect.SetResourceReference(StyleProperty, "SetupLinkButton"); detect.Click += (_, _) => Detect(); form.Children.Add(detect);
        var instructions = new StackPanel();
        Text(instructions, "Copilot additional instructions (optional, up to 1,000 characters)", session.Draft.CopilotAdditionalInstructions,
            value => session.Edit(session.Draft with { CopilotAdditionalInstructions = value }), 90);
        Text(instructions, "Scout additional instructions (optional, up to 1,000 characters)", session.Draft.ScoutAdditionalInstructions,
            value => session.Edit(session.Draft with { ScoutAdditionalInstructions = value }), 90);
        instructions.Children.Add(Note("Stored locally, not encrypted. Do not enter secrets. Every prompt begins with Review PR: {PR Link}.", 11));
        form.Children.Add(new Expander { Header = "Optional review instructions", Content = instructions, Margin = new(0, 12, 0, 8) });
        CenteredNote("VS Code uses your profile's default permissions. ReviewQ does not change them.", 11);
        CenteredNote("No microphone, screen recording, extra privacy permissions or blind typing is needed.", 11);
    }

    private void Preferences()
    {
        heading.Text = "Make it yours";
        subtitle.Text = "Start simple. You can change everything later in Settings.";
        var card = Card();
        var refresh = new Grid();
        refresh.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        refresh.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var refreshLabel = UiIcons.Label("refresh", "Refresh"); refreshLabel.VerticalAlignment = VerticalAlignment.Center; refresh.Children.Add(refreshLabel);
        var interval = new ComboBox { ItemsSource = new[] { 1, 5, 15, 30 }, SelectedItem = session.Draft.RefreshMinutes, MinWidth = 100 };
        AutomationProperties.SetName(interval, "Refresh interval (minutes)");
        interval.SelectionChanged += (_, _) => { if (interval.SelectedItem is int minutes) session.Edit(session.Draft with { RefreshMinutes = minutes }); };
        Grid.SetColumn(interval, 1); refresh.Children.Add(interval); card.Children.Add(refresh);
        Check(card, "Show pending count in the taskbar and tray", session.Draft.ShowCount, value => session.Edit(session.Draft with { ShowCount = value }));
        Check(card, "Include draft pull requests", session.Draft.IncludeDrafts, value => session.Edit(session.Draft with { IncludeDrafts = value }));
        Separator(card);
        var follow = Check(card, "People you follow", session.Draft.FollowPeople, value => session.Edit(session.Draft with { FollowPeople = value }));
        var emails = Text(card, "Followed-author emails", session.Draft.FollowedAuthorEmails,
            value => session.Edit(session.Draft with { FollowedAuthorEmails = value }));
        emails.IsEnabled = session.Draft.FollowPeople;
        follow.Checked += (_, _) => emails.IsEnabled = true; follow.Unchecked += (_, _) => emails.IsEnabled = false;
        card.Children.Add(Note("Use semicolons with no spaces. Adds organization-wide reads; PATs need Identity (Read) and Project and Team (Read). Emails go only to Azure DevOps to find authors.", 11));
        Check(card, "I'm the Author", session.Draft.ShowAuthoredPullRequests, value => session.Edit(session.Draft with { ShowAuthoredPullRequests = value }));
        card.Children.Add(Note("Shows your active PRs, comment counts and required approvals, not build status. Adds organization-wide reads; PATs need Project and Team (Read).", 11));
        card.Children.Add(Note("Both extra sections are off by default. Enabling them adds read-only Azure DevOps requests.", 11));
        Separator(card);
        Check(card, "Launch ReviewQ at Windows sign-in", session.Draft.StartAtLogin, value => session.Edit(session.Draft with { StartAtLogin = value }));
        Text(card, "Global shortcut (blank disables)", session.Draft.GlobalShortcut, value => session.Edit(session.Draft with { GlobalShortcut = value }));
    }

    private void Ready()
    {
        heading.Text = "You're ready.";
        subtitle.Text = "Your verified queue is ready. One click to make it home.";
        var card = Card();
        card.Children.Add(UiIcons.Label("repository", "Your review queue"));
        card.Children.Add(Note($"{session.Snapshot?.PendingCount ?? 0} assigned reviews; {session.Snapshot?.Items.Count ?? 0} PRs across your enabled queues."));
        foreach (var item in session.Snapshot?.Items.Take(8) ?? [])
            Preview(card, $"{item.Author} in {item.Repository}", $"#{item.Number} {item.Title}");
        if (session.Snapshot?.Items.Count == 0) card.Children.Add(Note("All caught up. New reviews will appear here after refresh."));
        CenteredNote("Finish to save your settings. Your verified queue opens without repeating the connection check.", 12);
        CenteredNote("Closing the queue keeps ReviewQ in the tray. Use its menu to Quit. Windows may hide the icon in overflow; drag it beside the clock or pin ReviewQ to your taskbar.", 11);
        CenteredNote("Green dots mean changes since the last successful refresh, not unread state.", 11);
    }

    private StackPanel Card()
    {
        var content = new StackPanel();
        var border = new Border { Child = content };
        border.SetResourceReference(StyleProperty, "SetupCard"); form.Children.Add(border);
        return content;
    }
    private static void Separator(Panel panel) => panel.Children.Add(new Border
    {
        Height = 1, Background = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Surface(), Margin = new(0, 18, 0, 18)
    });
    private static Brush Surface() => SystemParameters.HighContrast ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(22, 128, 128, 128));
    private void UpdateConnectionStatus()
    {
        var connected = session.HasVerifiedAccount && session.Error is null && !session.IsChecking;
        connectionStatus.Text = connected
            ? "Connected. Your review queue loaded successfully." : status.Text;
        connectionStatus.Opacity = 1;
        if (connectionSuccess is not null) connectionSuccess.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }
    private static void Preview(Panel panel, string caption, string title)
    {
        var labels = new StackPanel();
        labels.Children.Add(Note(caption, 11));
        labels.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new Border { Background = Surface(), CornerRadius = new(10), Padding = new(14, 8, 14, 12), Margin = new(0, 8, 0, 0), Child = labels });
    }
    private static TextBlock Companion(Panel panel, string icon, string title, string description, string status = "")
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new Border { Width = 52, Height = 52, CornerRadius = new(14), Background = Surface(), Margin = new(0, 0, 18, 0), Child = UiIcons.Create(icon, 32) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        var detail = Note(description, 12); detail.Margin = new(0, 4, 0, 0); text.Children.Add(detail);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var state = Note(status, 12); state.MaxWidth = 120; state.FontWeight = FontWeights.SemiBold; state.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(state, 2); row.Children.Add(state); panel.Children.Add(row);
        return state;
    }
    private static TextBox Text(Panel panel, string label, string value, Action<string> edit, int height = 0)
    {
        var box = new TextBox { Text = value, TextWrapping = TextWrapping.Wrap };
        if (height > 0) { box.Height = height; box.AcceptsReturn = true; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        Field(panel, label, box); box.TextChanged += (_, _) => edit(box.Text);
        return box;
    }
    private static CheckBox Check(Panel panel, string label, bool value, Action<bool> edit)
    {
        var box = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new(0, 8, 0, 8) };
        AutomationProperties.SetName(box, label);
        box.Checked += (_, _) => edit(true); box.Unchecked += (_, _) => edit(false); panel.Children.Add(box);
        return box;
    }
    private static void Field(Panel panel, string label, Control control)
    {
        panel.Children.Add(new Label { Content = label, Target = control, Padding = new(0, 6, 0, 4) });
        AutomationProperties.SetName(control, label); control.Margin = new(0, 0, 0, 8); panel.Children.Add(control);
    }
    private void CenteredNote(string text, int size = 13)
    {
        var note = Note(text, size); note.TextAlignment = TextAlignment.Center; form.Children.Add(note);
    }
    private static TextBlock Note(string text, int size = 13) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 10), Opacity = 0.7
    };
    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) session.Token = box.Password;
    }
    private void ClearPassword()
    {
        if (password is null) return;
        password.PasswordChanged -= PasswordChanged; password.Clear(); password = null;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (session.IsSaving && !app.IsQuitting) { e.Cancel = true; return; }
        session.Changed -= Update; ClearPassword();
        if (!completed && !app.IsQuitting) session.Pause();
    }
}
