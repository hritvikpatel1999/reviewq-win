using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public sealed class ReminderEditor : StackPanel
{
    private readonly StackPanel rows = new();
    private readonly StackPanel options = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Button add = new() { Content = "+ Add time", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock frequency = new() { FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ReminderController? controller;
    private readonly App app;
    private readonly Action<ReminderConfiguration> edit;
    public ReminderConfiguration Draft { get; private set; }
    public ReminderEditor(App app, ReminderConfiguration configuration, Action<ReminderConfiguration> edit)
    {
        Draft = configuration;
        this.app = app;
        this.edit = edit;
        controller = app.Reminders;
        Margin = new(0, 20, 0, 0);
        var heading = UiIcons.Label("bell", "Review reminders"); heading.Margin = new(4, 0, 0, 7); heading.Opacity = 0.85;
        ((TextBlock)heading.Children[1]).FontWeight = FontWeights.SemiBold;
        Children.Add(heading);
        var content = new StackPanel();
        var card = new Border { Child = content, Tag = "Review reminders" };
        card.SetResourceReference(StyleProperty, "SettingsCard"); Children.Add(card);
        var enabled = new CheckBox { Content = "Enable review reminders", IsChecked = Draft.Enabled,
            FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetName(enabled, "Enable scheduled review reminders");
        enabled.Checked += (_, _) => Update(Draft with { Enabled = true });
        enabled.Unchecked += (_, _) => Update(Draft with { Enabled = false });
        content.Children.Add(enabled);
        content.Children.Add(Note("Off by default. Generic local reminders, even when your queue is empty. Click one to open your queue."));
        AutomationProperties.SetName(options, "Reminder settings");
        content.Children.Add(options);
        var body = options;
        body.Children.Add(Note("Days"));
        var days = new WrapPanel { Margin = new(0, 2, 0, 8) };
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var check = new CheckBox { Content = day.ToString()[..3], IsChecked = Draft.Weekdays.Contains(day) };
            check.SetResourceReference(StyleProperty, "SettingsDay");
            AutomationProperties.SetName(check, day.ToString());
            check.Checked += (_, _) => Update(Draft with { Weekdays = Draft.Weekdays.Append(day).Distinct().Order().ToArray() });
            check.Unchecked += (_, _) => Update(Draft with { Weekdays = Draft.Weekdays.Where(x => x != day).ToArray() });
            days.Children.Add(check);
        }
        body.Children.Add(days);
        body.Children.Add(rows);
        add.Click += (_, _) =>
        {
            if (Draft.Times.Length >= 8) return;
            var start = Draft.Times.LastOrDefault()?.MinuteOfDay ?? 570;
            var minute = Enumerable.Range(60, 1440).Select(offset => (start + offset) % 1440)
                .First(value => !Draft.Times.Any(time => time.MinuteOfDay == value));
            Update(Draft with { Times = Draft.Times.Append(new ReminderTime(minute / 60, minute % 60)).ToArray() });
            RenderTimes();
        };
        add.SetResourceReference(StyleProperty, "SettingsButton");
        AutomationProperties.SetName(add, "Add reminder time");
        var timeActions = new Grid { Margin = new(0, 6, 0, 8) };
        timeActions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        timeActions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        timeActions.Children.Add(add); frequency.HorizontalAlignment = HorizontalAlignment.Right;
        frequency.Margin = new(12, 0, 0, 0); Grid.SetColumn(frequency, 1); timeActions.Children.Add(frequency);
        body.Children.Add(timeActions);
        body.Children.Add(Note("Up to 8 per selected day, in this PC's local time (24-hour). Windows must be signed in and awake. Focus and notification settings control delivery."));
        var sound = new CheckBox { Content = "Play reminder sound", IsChecked = Draft.PlaysSound };
        sound.Checked += (_, _) => Update(Draft with { PlaysSound = true });
        sound.Unchecked += (_, _) => Update(Draft with { PlaysSound = false });
        body.Children.Add(sound);
        var test = new WrapPanel { Margin = new(0, 6, 0, 4) };
        AddAction(test, "Send test reminder", () => controller?.SendTest(Draft.PlaysSound));
        body.Children.Add(test);
        body.Children.Add(Note("Saving Settings or opening your verified queue applies this schedule. You can send a test before saving. Quitting leaves saved reminders active."));
        var actions = new WrapPanel { Margin = new(0, 8, 0, 0) };
        AddAction(actions, "Set up / check notifications", () => controller?.CheckPermission());
        AddAction(actions, "Windows notification settings", WindowsReminders.OpenNotificationSettings);
        AddAction(actions, "Retry saved schedule", () => { if (controller is not null) controller.Apply(controller.Saved, true); });
        body.Children.Add(new Expander { Header = "Windows notification options", Content = actions, Margin = new(0, 8, 0, 0) });
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        status.FontSize = 12; status.Opacity = 0.8;
        content.Children.Add(status);
        Loaded += (_, _) => { if (controller is not null) controller.Changed += RenderStatus; RenderStatus(); };
        Unloaded += (_, _) => { if (controller is not null) controller.Changed -= RenderStatus; };
        RenderTimes();
        RenderEnabled();
        RenderStatus();
    }
    private void AddAction(Panel parent, string title, Action action)
    {
        var button = new Button { Content = title, IsEnabled = controller is not null, Margin = new(0, 0, 8, 6) };
        button.SetResourceReference(StyleProperty, "SettingsButton");
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) when (UiErrors.IsExpected(ex))
            {
                status.Text = UiErrors.Message(ex);
                app.ReportReminderCallbackFailure(ex);
            }
        };
        parent.Children.Add(button);
    }
    private void Update(ReminderConfiguration configuration)
    {
        Draft = configuration;
        RenderEnabled();
        frequency.Text = $"{Draft.Times.Length} reminder{(Draft.Times.Length == 1 ? "" : "s")} per selected day";
        edit(configuration);
    }
    private void RenderEnabled()
    {
        options.IsEnabled = Draft.Enabled;
        options.Opacity = Draft.Enabled || SystemParameters.HighContrast ? 1 : 0.45;
    }
    private void RenderTimes()
    {
        rows.Children.Clear();
        for (var index = 0; index < Draft.Times.Length; index++)
        {
            var position = index;
            var row = new Grid { Margin = new(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new() { Width = new(64) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"Time {index + 1}", VerticalAlignment = VerticalAlignment.Center });
            var time = new StackPanel { Orientation = Orientation.Horizontal };
            var hour = new ComboBox { ItemsSource = Enumerable.Range(0, 24), SelectedItem = Draft.Times[index].Hour, Width = 80, Margin = new(0), ItemStringFormat = "{0:00}" };
            var minute = new ComboBox { ItemsSource = Enumerable.Range(0, 60), SelectedItem = Draft.Times[index].Minute, Width = 80, Margin = new(0), ItemStringFormat = "{0:00}" };
            AutomationProperties.SetName(hour, $"Reminder {index + 1} hour");
            AutomationProperties.SetName(minute, $"Reminder {index + 1} minute");
            void TimeChanged()
            {
                if (hour.SelectedItem is not int h || minute.SelectedItem is not int m) return;
                var times = Draft.Times.ToArray(); times[position] = new(h, m); Update(Draft with { Times = times });
            }
            hour.SelectionChanged += (_, _) => TimeChanged();
            minute.SelectionChanged += (_, _) => TimeChanged();
            time.Children.Add(hour);
            time.Children.Add(new TextBlock { Text = ":", Margin = new(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            time.Children.Add(minute);
            Grid.SetColumn(time, 1); row.Children.Add(time);
            var remove = new Button { Content = UiIcons.Create("remove", 16), ToolTip = "Remove this reminder time", Padding = new(8, 4, 8, 4) };
            remove.SetResourceReference(StyleProperty, "SettingsButton");
            AutomationProperties.SetName(remove, $"Remove reminder {index + 1}");
            remove.Click += (_, _) =>
            {
                Update(Draft with { Times = Draft.Times.Where((_, i) => i != position).ToArray() });
                RenderTimes();
            };
            Grid.SetColumn(remove, 3); row.Children.Add(remove);
            rows.Children.Add(row);
        }
        add.IsEnabled = Draft.Times.Length < 8;
        frequency.Text = $"{Draft.Times.Length} reminder{(Draft.Times.Length == 1 ? "" : "s")} per selected day";
    }
    private static TextBlock Note(string text) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7, Margin = new(0, 4, 0, 8) };
    private void RenderStatus() => status.Text = controller is null ? "Notification actions are unavailable in this test session." :
        (controller.TestRequested ? "Test requested. Check Notification Center if no banner appears.\n\n" : "") +
        (controller.IsScheduled ? "Your saved schedule is active." : controller.Saved.Enabled ? "Your saved schedule is not active." : "Reminders are off in your saved settings.") +
        (controller.Permission == ReminderPermission.NotInitialized ? "\nSend a test to initialize this app's Windows notification profile." : "") +
        (controller.Permission == ReminderPermission.NotConfigured ? "\nSend a test or set up notifications under Windows notification options." : "") +
        (controller.Error is null ? "" : "\n" + controller.Error);
}
