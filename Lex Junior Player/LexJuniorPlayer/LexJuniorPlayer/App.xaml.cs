using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace LexJuniorPlayer;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\LexJuniorPlayer.SingleInstance";
    private const string ShuttingDownEventName = @"Local\LexJuniorPlayer.ShuttingDown";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _shuttingDownEvent;
    private bool _ownsMutex;

    public static string? StartupApkArg { get; private set; }
    public static bool IsSessionEnding { get; private set; }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static void SignalShuttingDown()
    {
        try
        {
            (Current as App)?._shuttingDownEvent?.Set();
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Services.PlayerInstallerService.ShouldRunAsUninstaller(e.Args))
        {
            base.OnStartup(e);
            var uninstaller = new SetupWizardWindow(isInstallerMode: false, isUninstallMode: true, shutdownOnFinish: true);
            MainWindow = uninstaller;
            uninstaller.Show();
            return;
        }

        if (Services.PlayerInstallerService.ShouldRunAsSetupMode(e.Args))
        {
            base.OnStartup(e);
            var setup = new SetupWizardWindow(isInstallerMode: true, isUninstallMode: false, shutdownOnFinish: true);
            MainWindow = setup;
            setup.Show();
            return;
        }

        _shuttingDownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ShuttingDownEventName);
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            var isShuttingDown = false;
            try { isShuttingDown = _shuttingDownEvent.WaitOne(0); } catch { }

            if (isShuttingDown)
            {
                try
                {
                    _ownsMutex = _instanceMutex.WaitOne(TimeSpan.FromSeconds(75));
                }
                catch (AbandonedMutexException)
                {
                    _ownsMutex = true;
                }

                if (!_ownsMutex)
                {
                    Shutdown(0);
                    return;
                }
            }
            else
            {
                try
                {
                    _ownsMutex = _instanceMutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    _ownsMutex = true;
                }

                if (!_ownsMutex)
                {
                    BringExistingInstanceToFront();
                    Shutdown(0);
                    return;
                }
            }
        }
        else
        {
            _ownsMutex = true;
        }

        try { _shuttingDownEvent.Reset(); } catch { }

        StartupApkArg = e.Args.FirstOrDefault(a =>
            Services.ApkBundleExtractor.IsSupportedPackageFile(a) && File.Exists(a));
        base.OnStartup(e);
        var mw = new MainWindow();
        MainWindow = mw;
        mw.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        // Never cancel Windows logoff or shutdown
        e.Cancel = false;
        IsSessionEnding = true;
        SignalShuttingDown();

        if (MainWindow is MainWindow mw)
        {
            mw.HandleSessionEndingFastShutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex && _instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _ownsMutex = false;
        }
        _instanceMutex?.Dispose();
        _instanceMutex = null;

        try
        {
            _shuttingDownEvent?.Dispose();
            _shuttingDownEvent = null;
        }
        catch { }

        base.OnExit(e);
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var procName = Process.GetCurrentProcess().ProcessName;
            foreach (var proc in Process.GetProcessesByName(procName))
            {
                try
                {
                    if (proc.Id == currentPid) continue;
                    var hwnd = proc.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        hwnd = FindMainWindowForProcess((uint)proc.Id);
                    }
                    if (hwnd != IntPtr.Zero)
                    {
                        if (IsIconic(hwnd))
                            ShowWindow(hwnd, 9);
                        else
                            ShowWindow(hwnd, 5);
                        SetForegroundWindow(hwnd);
                        return;
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    private static IntPtr FindMainWindowForProcess(uint targetPid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid != targetPid) return true;
            if (!IsWindowVisible(h)) return true;
            if (GetWindow(h, 4) != IntPtr.Zero) return true;
            var sb = new StringBuilder(128);
            _ = GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().Contains("Lex Junior Player", StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
