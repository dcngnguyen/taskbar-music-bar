using System.IO;
using System.Security;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;

namespace MusicBar;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one Music Bar at a time (it also starts with Windows).
        _singleInstance = new Mutex(true, @"Local\MusicBar.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled error: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Unobserved task error: " + args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        RegisterStartWithWindows();
        new BarWindow().Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Makes Music Bar start when you sign in, through a Task Scheduler task (turn it off any time in Task Scheduler).
    /// The task starts it within seconds. Windows' Startup apps list starts apps one by one, which left it minutes late.
    /// </summary>
    private static void RegisterStartWithWindows()
    {
        const string name = "MusicBar";
        try
        {
            var exe = Environment.ProcessPath;
            // Only the installed copy, so a test build never takes over the startup task.
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name);
            if (exe is null || !exe.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            scheduler.Connect();
            dynamic root = scheduler.GetFolder(@"\");

            bool exists;
            try { root.GetTask(name); exists = true; }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { exists = false; } // "not found"

            // Only create it when missing, so turning it off in Task Scheduler stays off.
            if (!exists)
            {
                var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
                var xml = $"""
                    <?xml version="1.0" encoding="UTF-16"?>
                    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                      <RegistrationInfo><Description>Starts Music Bar when you sign in.</Description></RegistrationInfo>
                      <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
                      <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                      <Settings>
                        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                        <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                        <Priority>5</Priority>
                      </Settings>
                      <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command></Exec></Actions>
                    </Task>
                    """;
                root.RegisterTask(name, xml, 6 /* create or update */, null, null, 3 /* run as the signed-in user */);
            }

            // The old Startup apps entry isn't needed any more.
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("Could not set start with Windows: " + ex.Message);
        }
    }
}
