using System.Runtime.InteropServices;
using System.Management.Automation;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace XPSThermalTray;




class RoundedContextMenuStrip : ContextMenuStrip
{
    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern long DwmSetWindowAttribute(IntPtr hwnd,
                                                        DWMWINDOWATTRIBUTE attribute,
                                                        ref DWM_WINDOW_CORNER_PREFERENCE pvAttribute,
                                                        uint cbAttribute);

    public RoundedContextMenuStrip()
    {
        var preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;     //change as you want
        DwmSetWindowAttribute(Handle,
                              DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                              ref preference,
                              sizeof(uint));
    }

    public enum DWMWINDOWATTRIBUTE
    {
        DWMWA_WINDOW_CORNER_PREFERENCE = 33
    }
    public enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWA_DEFAULT = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND = 2,
        DWMWCP_ROUNDSMALL = 3,
    }
}

public enum ThermalProfile
{
    UltraPerformance,
    Quiet,
    Cool,
    Optimized
}

static class Program
{
    static NotifyIcon notifyIcon = null!;
    static ContextMenuStrip contextMenu = null!;

    private static readonly Dictionary<ThermalProfile, int> profileToIndexMap = new Dictionary<ThermalProfile, int>
    {
        { ThermalProfile.Cool, 3 },
        { ThermalProfile.Optimized, 4 },
        { ThermalProfile.Quiet, 5 },
        { ThermalProfile.UltraPerformance, 6 },
    };
    private static readonly Dictionary<int, ThermalProfile> indexToProfileMap = profileToIndexMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    private static string appPath = Application.ExecutablePath;
    private static string assetsPath = Path.Combine(Application.StartupPath, "assets");
    private static Image loadingImg = Image.FromFile(Path.Combine(assetsPath, "loading.gif"));
    private static Image checkImg = Image.FromFile(Path.Combine(assetsPath, "check.png"));

    const string appName = "Dell XPS Thermal Tray";

    [STAThread]
    static void Main()
    {
        AppDomain? currentDomain = default(AppDomain);
        currentDomain = AppDomain.CurrentDomain;
        currentDomain.UnhandledException += GlobalUnhandledExceptionHandler;
        System.Windows.Forms.Application.ThreadException += GlobalThreadExceptionHandler;

        ApplicationConfiguration.Initialize();

        notifyIcon = new NotifyIcon();
        notifyIcon.Icon = new Icon(Path.Combine(assetsPath, "fire.ico"));
        notifyIcon.Text = appName;

        contextMenu = new RoundedContextMenuStrip();
        Font headerFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        contextMenu.Items.Add(" " + appName).Font = headerFont;

        ToolStripMenuItem startupLaunchMi = new ToolStripMenuItem(" Start On Launch", null, OnStartOnLaunchClicked);
        using (TaskService ts = new TaskService())
        {
            var existingTask = ts.GetTask(appName);
            startupLaunchMi.Image = existingTask != null && existingTask.Enabled ? checkImg : null;

        }
        contextMenu.Items.Add(startupLaunchMi);

        contextMenu.Items.Add(new ToolStripSeparator());

        contextMenu.Items.Add(" ❄️ Cool", loadingImg, OnMenuItemClicked);
        contextMenu.Items.Add(" 📈 Optimized", loadingImg, OnMenuItemClicked);
        contextMenu.Items.Add(" 🔇 Quiet", loadingImg, OnMenuItemClicked);
        contextMenu.Items.Add(" 🔥 Ultra Performance", loadingImg, OnMenuItemClicked);

        contextMenu.Items.Add(new ToolStripSeparator());

        contextMenu.Items.Add(" Quit", null, (_, _) => Environment.Exit(0));

        contextMenu.Closing += ContextMenuStrip_Closing;
        notifyIcon.ContextMenuStrip = contextMenu;
        notifyIcon.Visible = true;

        var hasAdminAccess = IsAdministrationRules();
        Console.WriteLine("Has admin access: " + hasAdminAccess);

        if (!hasAdminAccess)
        {
            MessageBox.Show("Admin access is required. Closing...");
            Environment.Exit(0);
        }

        updateCurrentProfile();
        periodicUpdate();
        Application.Run();
    }

    private static async void periodicUpdate()
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync())
        {
            updateCurrentProfile();
        }
    }

    private static bool IsAdministrationRules()
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return (new WindowsPrincipal(identity)).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    private static async void updateCurrentProfile()
    {
        var currentProfile = await getCurrentThermalProfileAsync();
        clearContextMenuImages();
        (contextMenu.Items[profileToIndexMap[currentProfile]] as ToolStripMenuItem)!.Image = checkImg;
    }

    private static void clearContextMenuImages()
    {
        for (int i = 3; i < 7; i++)
        {
            ToolStripMenuItem menuItem = (contextMenu.Items[i] as ToolStripMenuItem)!;
            menuItem.Image = null;
        }
    }

    private static async Task<ThermalProfile> getCurrentThermalProfileAsync()
    {
        using (PowerShell ps = PowerShell.Create())
        {
            await ps.AddCommand("Set-ExecutionPolicy")
                   .AddParameter("ExecutionPolicy", "RemoteSigned")
                   .AddParameter("Scope", "Process")
                   .InvokeAsync();
            ps.Commands.Clear();

            await ps.AddCommand("Import-Module").AddParameter("Name", "DellBIOSProvider").InvokeAsync();
            ps.Commands.Clear();

            await ps.AddCommand("cd").AddArgument("dellsmbios:").InvokeAsync();
            ps.Commands.Clear();

            var result = await ps.AddCommand("Get-Item").AddArgument(@".\PreEnabled\ThermalManagement").AddCommand("Select-Object").AddParameter("Property", "CurrentValue").InvokeAsync();
            var resultString = (result[0].Properties["CurrentValue"].Value as string)!;
            if (Enum.TryParse<ThermalProfile>(resultString, out var profile))
            {
                return profile;
            }
            else
            {
                throw new Exception("Invalid Thermal Profile");
            }
        }
    }
    private static async void setCurrentThermalProfile(ThermalProfile profile)
    {
        using (PowerShell ps = PowerShell.Create())
        {
            await ps.AddCommand("Set-ExecutionPolicy")
                   .AddParameter("ExecutionPolicy", "RemoteSigned")
                   .AddParameter("Scope", "Process")
                   .InvokeAsync();
            ps.Commands.Clear();

            await ps.AddCommand("Import-Module").AddParameter("Name", "DellBIOSProvider").InvokeAsync();
            ps.Commands.Clear();

            await ps.AddCommand("cd").AddArgument("dellsmbios:").InvokeAsync();
            ps.Commands.Clear();


            await ps.AddCommand("Set-Item").AddArgument(@".\PreEnabled\ThermalManagement").AddArgument(profile.ToString()).InvokeAsync();
            clearContextMenuImages();
            (contextMenu.Items[profileToIndexMap[profile]] as ToolStripMenuItem)!.Image = checkImg;

        }
    }




    private static void OnStartOnLaunchClicked(object? sender, EventArgs e)
    {
        ToolStripMenuItem clickedMenuItem = (sender as ToolStripMenuItem)!;
        clickedMenuItem.Image = clickedMenuItem.Image == checkImg ? null : checkImg;

        using (TaskService ts = new TaskService())
        {
            var existingTask = ts.GetTask(appName);

            if (existingTask != null && clickedMenuItem.Image != checkImg)
            {
                ts.RootFolder.DeleteTask(appName);
                return;
            }

            if (clickedMenuItem.Image == checkImg && existingTask == null)
            {
                TaskDefinition td = ts.NewTask();

                td.RegistrationInfo.Description = $"Launch {appName} at startup.";
                td.Principal.RunLevel = TaskRunLevel.Highest;
                td.Triggers.Add(new LogonTrigger { UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name });
                td.Actions.Add(new ExecAction(appPath));

                ts.RootFolder.RegisterTaskDefinition(appName, td);
            }
        }
    }

    private static void OnMenuItemClicked(object? sender, EventArgs e)
    {
        ToolStripMenuItem clickedMenuItem = (sender as ToolStripMenuItem)!;
        if (clickedMenuItem.Image != checkImg)
        {
            clickedMenuItem.Image = loadingImg;
            var index = contextMenu.Items.IndexOf(clickedMenuItem);
            var profile = indexToProfileMap[index];
            setCurrentThermalProfile(profile);

        }
    }

    private static void ContextMenuStrip_Closing(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
        {
            e.Cancel = true;
        }
    }

    private static void GlobalUnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? ex = default(Exception);
        ex = (Exception)e.ExceptionObject;
        string crashLog = $"[Unhandled Exception] {DateTime.Now}\n\n{ex.ToString()}\n\n";
        File.AppendAllText(Path.Combine(assetsPath, "crash.log"), crashLog);
    }

    private static void GlobalThreadExceptionHandler(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        Exception? ex = default(Exception);
        ex = e.Exception;
        string crashLog = $"[Thread Exception] {DateTime.Now}\n\n{ex.ToString()}\n\n";
        File.AppendAllText(Path.Combine(assetsPath, "crash.log"), crashLog);
    }
}