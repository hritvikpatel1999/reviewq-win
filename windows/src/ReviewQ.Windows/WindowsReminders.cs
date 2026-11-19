using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using ReviewQ.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ReviewQ.Windows;

public sealed class WindowsReminders : IReminderBackend
{
    public const string AppId = "ReviewQ.Windows";
    public const string Protocol = "reviewq-reminder";
    private const string Activator = "EC48903A-8DAA-4C72-AC6D-0DDCB1B4D799";
    private bool prepared;
    private static string Executable => Environment.ProcessPath ?? throw new ReviewException("The ReviewQ executable path is unavailable.");
    public static string UserId => WindowsIdentity.GetCurrent().User?.Value ??
        throw new ReviewException("The Windows user identity is unavailable.");
    public static string TaskName => "ReviewQ-Reminders-" + UserId;
    public static bool IsQueueActivation(IReadOnlyList<string> arguments) => arguments.Count == 2 &&
        arguments[0] == "--reminder-open" && string.Equals(arguments[1], Protocol + ":queue", StringComparison.OrdinalIgnoreCase);
    public ReminderPermission Permission
    {
        get
        {
            if (!prepared) return ReminderPermission.NotConfigured;
            try
            {
                return ToastNotificationManager.CreateToastNotifier(AppId).Setting == NotificationSetting.Enabled
                    ? ReminderPermission.Allowed : ReminderPermission.Blocked;
            }
            // Windows can create the per-app settings profile only after the first toast.
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
            { return ReminderPermission.NotInitialized; }
        }
    }

    public void Prepare()
    {
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Protocol))
        {
            key.SetValue("", "URL:ReviewQ reminder");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{Executable}\" --reminder-open \"%1\"");
        }
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ReviewQ", "ReviewQ.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        object link = new ShellLink();
        try
        {
            var shell = (IShellLink)link;
            shell.SetPath(Executable);
            shell.SetWorkingDirectory(Path.GetDirectoryName(Executable)!);
            shell.SetDescription("ReviewQ");
            shell.SetIconLocation(Path.Combine(AppContext.BaseDirectory, "ReviewQ.ico"), 0);
            var properties = (IPropertyStore)link;
            var identity = PropertyValue.String(AppId);
            var activator = PropertyValue.Guid(new(Activator));
            try
            {
                properties.SetValue(new PropertyKey(5), ref identity);
                properties.SetValue(new PropertyKey(26), ref activator);
                properties.Commit();
            }
            finally { identity.Free(); activator.Free(); }
            ((IPersistFile)link).Save(shortcut, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
        prepared = true;
    }

    public void ReplaceSchedule(ReminderConfiguration configuration) => RegisterSchedule(configuration, TaskName);
    public static bool IsOwnedSchedule(string xml, string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var actions = XDocument.Parse(xml).Root?.Element(ns + "Actions")?.Elements().ToArray();
        return actions is { Length: 1 } && actions[0].Name == ns + "Exec" &&
            string.Equals(actions[0].Element(ns + "Command")?.Value, executable, StringComparison.OrdinalIgnoreCase) &&
            actions[0].Element(ns + "Arguments")?.Value == "--deliver-reminder";
    }
    public static void RemoveRegistration()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new ReviewException("Windows Task Scheduler is unavailable.");
        object service = ActivatorCreate(type);
        object? folder = null, task = null;
        try
        {
            dynamic scheduler = service; scheduler.Connect();
            folder = scheduler.GetFolder("\\"); dynamic root = folder;
            try { task = root.GetTask(TaskName); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            if (task is not null)
            {
                dynamic registered = task;
                if (IsOwnedSchedule((string)registered.Xml, Executable)) root.DeleteTask(TaskName, 0);
            }
        }
        finally
        {
            if (task is not null) Marshal.FinalReleaseComObject(task);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
        var keyPath = @"Software\Classes\" + Protocol;
        bool owned;
        using (var command = Registry.CurrentUser.OpenSubKey(keyPath + @"\shell\open\command"))
            owned = string.Equals(command?.GetValue("") as string, $"\"{Executable}\" --reminder-open \"%1\"", StringComparison.OrdinalIgnoreCase);
        if (owned) Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
    }
    public static void RegisterSchedule(ReminderConfiguration configuration, string taskName)
    {
        configuration.Validate();
        if (taskName != TaskName && !taskName.StartsWith("ReviewQ-Validation-", StringComparison.Ordinal))
            throw new ReviewException("Refusing to modify a task not owned by ReviewQ.");
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new ReviewException("Windows Task Scheduler is unavailable.");
        object service = ActivatorCreate(type);
        object? folder = null;
        object? registered = null;
        try
        {
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            dynamic root = folder;
            if (configuration.Enabled)
                registered = root.RegisterTask(taskName, ScheduleXml(configuration, Executable, UserId), 6, UserId, null, 3, null);
            else
            {
                try { root.DeleteTask(taskName, 0); }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            }
        }
        finally
        {
            if (registered is not null) Marshal.FinalReleaseComObject(registered);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }
    private static object ActivatorCreate(Type type) => System.Activator.CreateInstance(type) ??
        throw new ReviewException("Windows Task Scheduler could not be opened.");
    public static string ScheduleXml(ReminderConfiguration configuration, string executable, string userId)
    {
        configuration.Validate();
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? value) => new(ns + name, value);
        var triggers = configuration.Times.OrderBy(x => x.MinuteOfDay).Select(time =>
            E("CalendarTrigger", new object[]
            {
                E("StartBoundary", $"2000-01-03T{time}:00"), E("Enabled", true),
                E("ScheduleByWeek", new object[] { E("WeeksInterval", 1),
                    E("DaysOfWeek", configuration.Weekdays.Order().Select(day => E(day.ToString(), null))) })
            }));
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "ReviewQ generic local review reminders")),
            E("Triggers", triggers),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                E("UserId", userId), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new object[]
            {
                E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("StartWhenAvailable", false),
                E("RunOnlyIfNetworkAvailable", false), E("Enabled", configuration.Enabled), E("Hidden", true),
                E("WakeToRun", false), E("ExecutionTimeLimit", "PT1M")
            }),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                E("Exec", new object[] { E("Command", executable), E("Arguments", "--deliver-reminder") })))).ToString();
    }
    public static string ToastXml(bool sound) => new XElement("toast",
        new XAttribute("activationType", "protocol"), new XAttribute("launch", Protocol + ":queue"),
        new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", "Review reminder"), new XElement("text", "Take a moment to check your ReviewQ."))),
        sound ? new XElement("audio", new XAttribute("src", "ms-winsoundevent:Notification.Default")) :
            new XElement("audio", new XAttribute("silent", "true"))).ToString();
    public void SendTest(bool sound)
    {
        if (!prepared) throw new ReviewException("Set up Windows notifications before sending a reminder.");
        var document = new XmlDocument();
        document.LoadXml(ToastXml(sound));
        var notification = new ToastNotification(document) { ExpirationTime = DateTimeOffset.UtcNow.AddHours(1) };
        ToastNotificationManager.CreateToastNotifier(AppId).Show(notification);
    }
    public static void OpenNotificationSettings()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications")
            { UseShellExecute = true }) ?? throw new ReviewException("Windows notification settings could not be opened.");
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath(StringBuilder path, int length, IntPtr findData, int flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription(StringBuilder text, int length); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory(StringBuilder path, int length); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments(StringBuilder text, int length); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short key); void SetHotkey(short key); void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation(StringBuilder path, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr window, int flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(uint id)
    {
        public readonly Guid Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        public readonly uint Id = id;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyValue
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer, Extra;
        public static PropertyValue String(string value) => new() { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        public static PropertyValue Guid(Guid value)
        {
            var pointer = Marshal.AllocCoTaskMem(16);
            Marshal.StructureToPtr(value, pointer, false);
            return new() { Type = 72, Pointer = pointer };
        }
        public void Free() { Marshal.FreeCoTaskMem(Pointer); Pointer = IntPtr.Zero; }
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropertyValue value);
        void SetValue(in PropertyKey key, ref PropertyValue value); void Commit();
    }
}
