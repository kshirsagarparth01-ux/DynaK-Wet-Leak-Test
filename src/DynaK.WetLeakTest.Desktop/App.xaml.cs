using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DynaK.WetLeakTest.Desktop;

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\DynaK.WetLeakTest.Desktop";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DesktopLog.Information("Desktop application starting.");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DesktopLog.Information("Desktop application stopped.");
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExistingWindow()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var handle = NativeMethods.FindWindow(null, DynaK.WetLeakTest.Desktop.MainWindow.WindowTitle);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
                NativeMethods.SetForegroundWindow(handle);
                return;
            }

            Thread.Sleep(100);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DesktopLog.Error("Unhandled desktop UI exception.", e.Exception);
        MessageBox.Show(
            "DynaK encountered an unexpected application error. Details were written to the desktop log.",
            DynaK.WetLeakTest.Desktop.MainWindow.WindowTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        DesktopLog.Error("Unhandled desktop process exception.", e.ExceptionObject as Exception);
    }

    private static class NativeMethods
    {
        internal const int SwRestore = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindow(string? className, string windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr windowHandle, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr windowHandle);
    }
}
