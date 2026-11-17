using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskbarNowPlaying.Setup;

static class Program
{
    const string AppName = "SpotiBai Taskbar";
    const string AppExeName = "SpotiBaiTaskbar.exe";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var choice = MessageBox.Show(
            $"Install {AppName} for the current Windows user?\n\n" +
            "This installs the app, adds a Desktop shortcut, and starts it with Windows.",
            $"{AppName} Setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (choice != DialogResult.Yes) return;

        try
        {
            string installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", AppName);
            string appPath = Path.Combine(installDir, AppExeName);

            StopInstalledApp(appPath);
            Directory.CreateDirectory(installDir);

            using Stream payload = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("TaskbarNowPlaying.Setup.Payload.exe")
                ?? throw new InvalidOperationException("The app payload is missing from this installer.");
            using (var output = File.Create(appPath))
                payload.CopyTo(output);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            File.Delete(Path.Combine(desktop, "TaskbarNowPlaying.lnk"));
            File.Delete(Path.Combine(startMenu, "Programs", "TaskbarNowPlaying.lnk"));
            File.Delete(Path.Combine(startup, "TaskbarNowPlaying.lnk"));
            CreateShortcut(Path.Combine(desktop, $"{AppName}.lnk"), appPath, installDir);
            CreateShortcut(
                Path.Combine(startMenu, "Programs", $"{AppName}.lnk"),
                appPath,
                installDir);

            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                key.DeleteValue("TaskbarNowPlaying", throwOnMissingValue: false);
                key.SetValue(AppName, $"\"{appPath}\"");
            }

            MessageBox.Show(
                $"{AppName} was installed successfully.",
                $"{AppName} Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Setup could not complete:\n\n{ex.Message}",
                $"{AppName} Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    static void StopInstalledApp(string appPath)
    {
        string fullAppPath = Path.GetFullPath(appPath);
        foreach (string processName in new[] { Path.GetFileNameWithoutExtension(AppExeName), "TaskbarNowPlaying" })
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        string processPath = Path.GetFullPath(process.MainModule?.FileName ?? "");
                        if (string.Equals(processPath, fullAppPath, StringComparison.OrdinalIgnoreCase) ||
                            processName == "TaskbarNowPlaying")
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The process may have exited between enumeration and inspection.
                    }
                }
            }
        }
    }

    static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create the Windows shortcut service.");
        try
        {
            dynamic shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath })!;
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = targetPath;
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
