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
    private readonly StackPanel form = new();
    private readonly TextBlock heading = new() { FontSize = 25, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock subtitle = Note("");
    private readonly TextBlock status = Note("");
    private readonly TextBlock progress = Note("");
    private readonly Button back = new() { Content = "_Back", Margin = new(0, 0, 8, 0) };
    private readonly Button next = new() { Content = "_Continue", IsDefault = true };
    private readonly Button keep = new() { Content = "Keep existing settings", Margin = new(0, 0, 8, 0) };
    private SetupStep? rendered;
    private PasswordBox? password;
    private bool completed;
    private readonly bool canKeepExistingSettings;
    public SetupWindow(App app, SetupSession session, bool canKeepExistingSettings)
    {
        this.app = app; this.session = session;
        this.canKeepExistingSettings = canKeepExistingSettings;
        Title = "ReviewQ Setup";
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Width = 610; Height = 660; MinWidth = 480; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(24) };
        var header = new StackPanel();
        header.Children.Add(progress); header.Children.Add(heading); header.Children.Add(subtitle);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new(0, 14, 0, 0) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        footer.Children.Add(status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(keep); buttons.Children.Add(back); buttons.Children.Add(next); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        back.Click += (_, _) => session.Back();
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
            catch (Exception ex) when (UiErrors.IsExpected(ex)) { status.Text = UiErrors.Message(ex); }
        };
        Closing += OnClosing;
        Closed += (_, _) => { session.Changed -= Update; form.Children.Clear(); };
        session.Changed += Update;
        Update();
    }
    private void Update()
    {
        progress.Text = $"Step {(int)session.Step + 1} of 6";
        status.Text = session.IsChecking ? "Checking your current connection..." : session.Error ?? session.Info ?? "";
        back.IsEnabled = session.Step != SetupStep.Welcome && !session.IsChecking && !session.IsSaving;
        keep.Visibility = canKeepExistingSettings ? Visibility.Visible : Visibility.Collapsed;
        keep.IsEnabled = !session.IsChecking && !session.IsSaving;
        next.IsEnabled = !session.IsChecking && !session.IsSaving &&
            (session.Step != SetupStep.Authentication || session.HasVerifiedAccount);
        next.Content = session.Step == SetupStep.Welcome ? "_Get started" :
            session.Step == SetupStep.Ready ? "_Open my queue" :
            session.Step == SetupStep.Preferences && !session.HasVerifiedQueue ? "_Check and continue" : "_Continue";
        form.IsEnabled = !session.IsChecking && !session.IsSaving;
        if (rendered == session.Step) return;
        rendered = session.Step;
        ClearPassword();
        form.Children.Clear();
        switch (session.Step)
        {
            case SetupStep.Welcome:
                heading.Text = "Your reviews. One queue.";
                subtitle.Text = "The pull requests that need you, in your taskbar and notification area.";
                form.Children.Add(Note("Connect Azure DevOps, choose optional review companions, and load your first queue. ReviewQ only reads pull requests; it never votes, comments, or merges."));
                form.Children.Add(Note("Closing setup saves your non-secret choices so you can resume. An unsaved PAT and connection verification are cleared when you close."));
                form.Children.Add(Note("Your startup queue cache is local and unencrypted. It includes PR display data, but no credentials, discussion bodies, or assistant prompts."));
                break;
            case SetupStep.Sources:
                heading.Text = "Where do you review?";
                subtitle.Text = "Add organization or project URLs, one per line.";
                Text("Azure DevOps sources", session.Draft.SourceUrls,
                    value => session.Edit(session.Draft with { SourceUrls = value }), 150);
                form.Children.Add(Note("Example: https://dev.azure.com/your-organization/your-project\nAn organization URL includes its projects. Followed and authored queues search organization-wide."));
                break;
            case SetupStep.Authentication:
                heading.Text = "Connect Azure DevOps";
                subtitle.Text = "Use Azure CLI sign-in or a Code (Read) personal access token.";
                var auth = new ComboBox { ItemsSource = Enum.GetValues<AuthenticationMethod>(), SelectedItem = session.Draft.Authentication };
                Field("Authentication method", auth);
                auth.SelectionChanged += (_, _) =>
                {
                    if (auth.SelectedItem is AuthenticationMethod method) session.Edit(session.Draft with { Authentication = method });
                };
                Text("Optional Entra tenant GUID or domain", session.Draft.TenantId,
                    value => session.Edit(session.Draft with { TenantId = value }));
                form.Children.Add(Note("Azure CLI: install it separately and sign in using PowerShell:\naz login --allow-no-subscriptions\nFor tenant-specific access, add --tenant followed by your tenant GUID or domain. Organization access policies still apply."));
                var copy = new Button { Content = "Copy Azure CLI sign-in command", HorizontalAlignment = HorizontalAlignment.Left };
                copy.Click += (_, _) =>
                {
                    try
                    {
                        var settings = session.Draft with { FollowPeople = false };
                        settings.Validate();
                        Clipboard.SetText("az login --allow-no-subscriptions" +
                            (settings.TenantId.Trim().Length > 0 ? " --tenant " + settings.TenantId.Trim() : ""));
                        status.Text = "Sign-in command copied. Run it in PowerShell, then test your connection.";
                    }
                    catch (Exception ex) when (UiErrors.IsExpected(ex) || ex is System.Runtime.InteropServices.COMException)
                    { status.Text = ex is System.Runtime.InteropServices.COMException ? "The clipboard is busy. Copy the displayed command manually." : UiErrors.Message(ex); }
                };
                form.Children.Add(copy);
                password = new PasswordBox { Password = session.Token };
                Field("New PAT (blank keeps your saved token)", password);
                password.PasswordChanged += PasswordChanged;
                form.Children.Add(Note("The PAT remains in memory until you finish setup. It is never saved in setup progress. Finishing saves it to Windows Credential Manager."));
                var test = new Button { Content = "_Test connection", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 12, 0, 0) };
                test.Click += async (_, _) => await session.TestAsync();
                form.Children.Add(test);
                break;
            case SetupStep.Companions:
                heading.Text = "Your review companions";
                subtitle.Text = "Optional shortcuts. Your browser is always enough to get started.";
                form.Children.Add(Note("Default browser: available through Windows default apps."));
                var companionStatus = Note("");
                void Detect()
                {
                    string code;
                    try { _ = ReviewLauncher.FindCode(); code = "VS Code detected. Sign into Copilot in VS Code to use agent-chat review actions."; }
                    catch (ReviewException ex) { code = ex.Message; }
                    var edge = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                        Environment.SpecialFolder.LocalApplicationData }.Any(folder =>
                        File.Exists(Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe")));
                    companionStatus.Text = $"{code}\n\nMicrosoft Edge: {(edge ? "detected" : "not detected; use your default browser or install Edge")}.";
                }
                Detect(); form.Children.Add(companionStatus);
                var detect = new Button { Content = "Recheck installed companions", HorizontalAlignment = HorizontalAlignment.Left };
                detect.Click += (_, _) => Detect(); form.Children.Add(detect);
                form.Children.Add(Note("Scout remains unavailable on Windows. No extra Windows privacy permissions, blind typing, automatic submissions, or changes to VS Code permission defaults are required."));
                Text("Copilot additional instructions (optional, up to 1,000 characters)", session.Draft.CopilotAdditionalInstructions,
                    value => session.Edit(session.Draft with { CopilotAdditionalInstructions = value }), 110);
                form.Children.Add(Note("Stored locally, not encrypted. Do not enter secrets. Every review prompt begins with Review PR: {PR Link}."));
                break;
            case SetupStep.Preferences:
                heading.Text = "Make it yours";
                subtitle.Text = "You can change these preferences later in Settings.";
                var interval = new ComboBox { ItemsSource = new[] { 1, 5, 15, 30 }, SelectedItem = session.Draft.RefreshMinutes };
                Field("Refresh interval (minutes)", interval);
                interval.SelectionChanged += (_, _) => { if (interval.SelectedItem is int minutes) session.Edit(session.Draft with { RefreshMinutes = minutes }); };
                Check("Include draft pull requests", session.Draft.IncludeDrafts, value => session.Edit(session.Draft with { IncludeDrafts = value }));
                Check("Show pending count", session.Draft.ShowCount, value => session.Edit(session.Draft with { ShowCount = value }));
                Check("Show People you follow", session.Draft.FollowPeople, value => session.Edit(session.Draft with { FollowPeople = value }));
                Text("Followed-author emails (semicolon separated, without spaces)", session.Draft.FollowedAuthorEmails,
                    value => session.Edit(session.Draft with { FollowedAuthorEmails = value }));
                Check("Show PRs I've authored", session.Draft.ShowAuthoredPullRequests, value => session.Edit(session.Draft with { ShowAuthoredPullRequests = value }));
                Check("Start ReviewQ at Windows sign-in", session.Draft.StartAtLogin, value => session.Edit(session.Draft with { StartAtLogin = value }));
                Text("Global shortcut (blank disables)", session.Draft.GlobalShortcut, value => session.Edit(session.Draft with { GlobalShortcut = value }));
                break;
            case SetupStep.Ready:
                heading.Text = "You're ready";
                subtitle.Text = "Your verified queue is ready. Finish to save your settings.";
                form.Children.Add(Note($"{session.Snapshot?.PendingCount ?? 0} assigned reviews; {session.Snapshot?.Items.Count ?? 0} PRs across your enabled queues."));
                foreach (var item in session.Snapshot?.Items.Take(8) ?? [])
                    form.Children.Add(Note($"#{item.Number} {item.Title}\n{item.Author} in {item.Repository}"));
                form.Children.Add(Note("Closing the queue keeps ReviewQ running. Use the tray menu to Quit. Windows may place the tray icon in overflow; drag it beside the clock, or pin ReviewQ to your taskbar."));
                form.Children.Add(Note("Green dots show changes since the previous successful refresh, not unread state. Opening a PR does not clear a dot."));
                break;
        }
    }
    private void Text(string label, string value, Action<string> edit, int height = 0)
    {
        var box = new TextBox { Text = value, TextWrapping = TextWrapping.Wrap };
        if (height > 0) { box.Height = height; box.AcceptsReturn = true; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        Field(label, box);
        box.TextChanged += (_, _) => edit(box.Text);
    }
    private void Check(string label, bool value, Action<bool> edit)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new(0, 7, 0, 7) };
        AutomationProperties.SetName(box, label);
        box.Checked += (_, _) => edit(true); box.Unchecked += (_, _) => edit(false);
        form.Children.Add(box);
    }
    private void Field(string label, Control control)
    {
        form.Children.Add(new Label { Content = label, Target = control, Padding = new(0, 8, 0, 4) });
        AutomationProperties.SetName(control, label);
        control.Margin = new(0, 0, 0, 8);
        form.Children.Add(control);
    }
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 12), Opacity = 0.8 };
    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) session.Token = box.Password;
    }
    private void ClearPassword()
    {
        if (password is null) return;
        password.PasswordChanged -= PasswordChanged;
        password.Clear();
        password = null;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (session.IsSaving && !app.IsQuitting) { e.Cancel = true; return; }
        session.Changed -= Update;
        ClearPassword();
        if (!completed && !app.IsQuitting) session.Pause();
    }
}
