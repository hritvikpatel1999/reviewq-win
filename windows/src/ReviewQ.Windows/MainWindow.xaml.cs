using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public partial class MainWindow : Window
{
    private readonly App app;
    private readonly ReviewController controller;
    private readonly UpdateStore? updates;
    private readonly ReminderController? reminders;
    private readonly DispatcherTimer ageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, bool> collapsed = new();
    private ReviewSnapshot? rendered;
    private ReviewChanges? renderedChanges;
    public MainWindow(App app, ReviewController controller, UpdateStore? updates = null)
    {
        InitializeComponent();
        this.app = app;
        this.controller = controller;
        this.updates = updates ?? app.Updates;
        reminders = app.Reminders;
        AppSymbol.Content = UiIcons.Create("app", 28, true);
        RefreshButton.Content = UiIcons.Create("refresh", 18);
        SettingsButton.Content = UiIcons.Create("settings", 18);
        QuitButton.Content = UiIcons.Create("power", 18);
        controller.PropertyChanged += Updated;
        if (this.updates is not null) this.updates.Changed += RenderUpdates;
        if (reminders is not null) reminders.Changed += Render;
        ageTimer.Tick += (_, _) => RenderAge();
        IsVisibleChanged += (_, _) => { if (IsVisible) { RenderAge(); ageTimer.Start(); } else ageTimer.Stop(); };
        Closed += (_, _) =>
        {
            ageTimer.Stop();
            controller.PropertyChanged -= Updated;
            if (this.updates is not null) this.updates.Changed -= RenderUpdates;
            if (reminders is not null) reminders.Changed -= Render;
        };
        Closing += OnClosing;
        RenderUpdates();
        Render();
    }
    private void Updated(object? sender, PropertyChangedEventArgs e) => Render();
    private void Render()
    {
        var snapshot = controller.Snapshot;
        Title = controller.Settings.ShowCount && snapshot is not null ? $"ReviewQ - {controller.PendingCount} pending" : "ReviewQ";
        StatusText.Text = controller.IsRefreshing ? "Refreshing..." :
            snapshot is not null ? $"{controller.PendingCount} pending reviews" :
            controller.Settings.HasSources ? "Refresh to load your review queue." : "Welcome! Open Settings to add your Azure DevOps sources and sign-in method.";
        RefreshButton.IsEnabled = !controller.IsRefreshing && controller.Settings.HasSources;
        RefreshButton.ToolTip = controller.IsRefreshing ? "Refreshing reviews..." : "Refresh reviews";
        RenderAge();
        ErrorPanel.Visibility = controller.Error is null && controller.CacheWarning is null && controller.ArchiveError is null &&
            reminders?.Error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = (snapshot is not null && controller.Error is not null ? "Showing the last successful queue. " : "") +
            string.Join("\n", new[] { controller.Error, controller.CacheWarning, controller.ArchiveError, reminders?.Error }.Where(x => x is not null));
        if (ReferenceEquals(rendered, snapshot) && ReferenceEquals(renderedChanges, controller.Changes) && QueuePanel.Children.Count > 0) return;
        rendered = snapshot;
        renderedChanges = controller.Changes;
        QueuePanel.Children.Clear();
        if (snapshot is null || snapshot.Items.Count == 0)
        {
            QueuePanel.Children.Add(new TextBlock { Text = snapshot is null ? "Your queue will appear here after a successful refresh." : "No pull requests in your enabled queues.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 20, 0, 0) });
            return;
        }
        foreach (var queue in new[] { QueueKind.Assigned, QueueKind.Followed, QueueKind.Authored })
        {
            var items = controller.ActiveItems.Where(x => x.Queue == queue).ToList();
            if (items.Count == 0) continue;
            var content = new StackPanel();
            if (queue == QueueKind.Assigned)
            {
                foreach (var group in items.GroupBy(x => x.GroupKey))
                {
                    var cards = new StackPanel();
                    foreach (var item in group) cards.Children.Add(Card(item));
                    var first = group.First();
                    QueuePanel.Children.Add(Expandable($"{queue}/{group.Key}", GroupHeader("repository", first.Repository,
                        $"{first.Organization} / {first.Project}", group.Count(), controller.Changes.RepositoryKeys.Contains(first.GroupKey)), cards, group.Key));
                }
                continue;
            }
            foreach (var item in items) content.Children.Add(Card(item));
            var title = queue == QueueKind.Followed ? "People you follow" : "I'm the Author";
            QueuePanel.Children.Add(Expandable(queue.ToString(),
                GroupHeader(queue == QueueKind.Followed ? "people" : "author", title, null, items.Count,
                    queue == QueueKind.Followed ? controller.Changes.Followed : controller.Changes.Authored), content, title));
        }
        var archived = controller.ArchivedItems;
        if (archived.Count > 0)
        {
            var cards = new StackPanel();
            foreach (var item in archived) cards.Children.Add(Card(item, true));
            QueuePanel.Children.Add(Expandable("Archives", GroupHeader("archive", "Archives", null, archived.Count), cards, "Archives", true));
        }
    }
    private static UIElement GroupHeader(string icon, string title, string? subtitle, int count, bool changed = false)
    {
        var grid = new Grid { Margin = new(2, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var symbol = UiIcons.Create(icon, 23, icon == "repository");
        symbol.Margin = new(0, 0, 14, 0);
        grid.Children.Add(symbol);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new DockPanel { LastChildFill = false };
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (changed)
        {
            var dot = ChangeDot("Section changed since the previous successful refresh");
            dot.Margin = new(10, 0, 0, 0);
            titleRow.Children.Add(dot);
        }
        labels.Children.Add(titleRow);
        if (subtitle is not null) labels.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.6, FontSize = 12, Margin = new(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(labels, 1); grid.Children.Add(labels);
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, 10, 132, 255)), CornerRadius = new(12),
            MinWidth = 28, Padding = new(8, 3, 8, 3), Margin = new(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count.ToString(), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }
        };
        if (!SystemParameters.HighContrast)
            ((TextBlock)badge.Child).Foreground = Brushes.DodgerBlue;
        else badge.SetResourceReference(Border.BackgroundProperty, SystemColors.ControlBrushKey);
        Grid.SetColumn(badge, 2); grid.Children.Add(badge);
        return grid;
    }
    private Expander Expandable(string key, UIElement heading, UIElement content, string description, bool initiallyCollapsed = false)
    {
        var expander = new Expander { Header = heading, Content = content, IsExpanded = !collapsed.GetValueOrDefault(key, initiallyCollapsed), Margin = new(0, 5, 0, 9) };
        expander.SetResourceReference(StyleProperty, "ReviewGroupExpander");
        AutomationProperties.SetName(expander, description);
        expander.ToolTip = description;
        expander.Expanded += (_, e) => { if (ReferenceEquals(e.Source, expander)) collapsed[key] = false; };
        expander.Collapsed += (_, e) => { if (ReferenceEquals(e.Source, expander)) collapsed[key] = true; };
        return expander;
    }
    private UIElement Card(ReviewItem item, bool archived = false)
    {
        var panel = new StackPanel { Margin = new(30, 8, 5, 18) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var stage = new TextBlock
        {
            Text = item.StageLabel, ToolTip = item.Explanation, TextWrapping = TextWrapping.Wrap, Opacity = 0.65,
            FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0)
        };
        var stageRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (controller.Changes.ItemIds.Contains(item.Id))
            stageRow.Children.Add(ChangeDot("Pull request is new or changed since the previous successful refresh"));
        stageRow.Children.Add(stage); top.Children.Add(stageRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var archive = ActionButton(archived ? "restore" : "archive", archived ? "Restore pull request" : "Archive pull request");
        archive.Click += (_, e) => { e.Handled = true; controller.SetArchived(item, !archived); };
        actions.Children.Add(archive);
        var edge = ActionButton(archived ? "browser" : "edge", archived ? "Open in default browser" : "Open in Microsoft Edge");
        edge.Click += async (_, _) => await app.RunActionAsync(archived ? "browser" : "edge", () => { ReviewLauncher.Browser(item.Url, !archived); return Task.CompletedTask; });
        actions.Children.Add(edge);
        if (!archived && item.Queue != QueueKind.Authored)
        {
            var scout = ActionButton("scout", "Start a new Scout review chat");
            scout.Click += async (_, _) => await app.RunActionAsync("scout",
                () => ReviewLauncher.ScoutAsync(item.Url, controller.Settings.ScoutAdditionalInstructions, app.Lifetime));
            actions.Children.Add(scout);
            var copilot = ActionButton("copilot", "Start a new VS Code Copilot agent chat");
            copilot.Click += async (_, _) => await app.RunActionAsync("copilot", () => ReviewLauncher.CopilotAsync(item.Url, controller.Settings.CopilotAdditionalInstructions, app.Lifetime));
            actions.Children.Add(copilot);
        }
        Grid.SetColumn(actions, 1); top.Children.Add(actions); panel.Children.Add(top);
        var title = new Button { Content = new TextBlock { Text = $"#{item.Number}  {item.Title}", TextWrapping = TextWrapping.Wrap },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = item.Url.AbsoluteUri, Padding = new(0, 6, 0, 6), Margin = new(0, 3, 0, 3), BorderThickness = new(0),
            Background = Brushes.Transparent, FontSize = 15, FontWeight = FontWeights.SemiBold };
        title.Click += async (_, _) => await app.RunActionAsync("browser", () => { ReviewLauncher.Browser(item.Url, false); return Task.CompletedTask; });
        AutomationProperties.SetName(title, $"Open pull request {item.Number}: {item.Title}");
        panel.Children.Add(title);
        var age = DateTimeOffset.UtcNow - item.CreatedAt;
        var ageLabel = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d" : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h" : $"{Math.Max(0, (int)age.TotalMinutes)}m";
        var metadata = new Grid();
        metadata.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        metadata.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        metadata.Children.Add(new TextBlock { Text = item.Author + (item.Queue == QueueKind.Assigned && !archived ? "" : $" in {item.Repository}") +
            (item.IsDraft ? " - Draft" : "") + (item.IsRequired ? " - Required" : ""),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12, Margin = new(0, 0, 10, 0) });
        var ageText = new TextBlock { Text = ageLabel, Opacity = 0.6, FontSize = 12, ToolTip = $"Created {item.CreatedAt.LocalDateTime:g}" };
        Grid.SetColumn(ageText, 1); metadata.Children.Add(ageText); panel.Children.Add(metadata);
        return new Border { Child = panel, BorderThickness = new(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(22, 128, 128, 128)), Margin = new(0, 0, 0, 4) };
    }
    private static Button ActionButton(string icon, string description)
    {
        var button = new Button { Content = UiIcons.Create(icon, 16, icon == "edge"), ToolTip = description };
        button.SetResourceReference(StyleProperty, "ReviewIconButton");
        AutomationProperties.SetName(button, description);
        return button;
    }
    private static FrameworkElement ChangeDot(string description)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.SeaGreen,
            Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = description };
        AutomationProperties.SetName(dot, description);
        return dot;
    }
    private static string Age(TimeSpan age) => age.TotalDays >= 1 ? $"{(int)age.TotalDays}d" :
        age.TotalHours >= 1 ? $"{(int)age.TotalHours}h" : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m" : "a few seconds";
    private void RenderAge()
    {
        var snapshot = controller.Snapshot;
        var age = snapshot is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - snapshot.FetchedAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var elapsed = age.TotalHours >= 1 ? Age(age) :
            age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes} min, {age.Seconds} sec" : $"{age.Seconds} sec";
        UpdatedText.Text = snapshot is null ? "Not updated yet" :
            $"{(controller.Error is null ? "Updated" : "Last successful update")} {elapsed} ago" +
            (controller.IsCached ? " - cached queue" : "");
        UpdatedText.ToolTip = snapshot is null ? null : $"Last successful update: {snapshot.FetchedAt.LocalDateTime:g}";
    }
    private void RenderUpdates()
    {
        UpdateBanner.Visibility = updates?.BannerUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateText.Text = updates?.BannerUpdate is { } release ? $"ReviewQ v{release.Version} is out" : "";
        if (SystemParameters.HighContrast) UpdateBanner.SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
    }
    private void DismissUpdateClick(object sender, RoutedEventArgs e) => updates?.DismissUpdate();
    private async void ViewUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (updates?.AvailableUpdate is { } release)
            await app.RunActionAsync("browser", () =>
            {
                ReviewLauncher.OpenWeb(release.ReleaseUrl);
                return Task.CompletedTask;
            });
    }
    private async void CreatorClick(object sender, RoutedEventArgs e) =>
        await app.RunActionAsync("browser", () =>
        {
            ReviewLauncher.OpenWeb(new Uri("https://github.com/hritvikpatel1999"));
            return Task.CompletedTask;
        });
    private async void RefreshClick(object sender, RoutedEventArgs e) => await controller.RefreshAsync();
    private void SettingsClick(object sender, RoutedEventArgs e) => app.ShowSettings();
    private void DiagnosticsClick(object sender, RoutedEventArgs e) => app.ShowDiagnostics();
    private void SetupClick(object sender, RoutedEventArgs e) => app.ShowSetup();
    private void QuitClick(object sender, RoutedEventArgs e) => app.Quit();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (app.IsQuitting) return;
        e.Cancel = true;
        if (!controller.Settings.FirstCloseShown)
        {
            MessageBox.Show(this, "ReviewQ will keep refreshing in the background. Find it beside the clock (possibly under the overflow arrow). Use the tray menu to Quit. You can pin the app to your taskbar yourself for easy reopening.",
                "ReviewQ is still running", MessageBoxButton.OK, MessageBoxImage.Information);
            app.MarkFirstClose();
        }
        Hide();
    }
}
