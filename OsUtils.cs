// Translation of OsUtils.pas

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CBSEnum;

public static class OsUtils
{
    public static string GetSystemDir()
    {
        var buf = new char[260];
        uint len = NativeMethods.GetSystemDirectory(buf, (uint)buf.Length);
        return new string(buf, 0, (int)len);
    }

    public static string GetWindowsDir()
    {
        var buf = new char[260];
        uint len = NativeMethods.GetWindowsDirectory(buf, (uint)buf.Length);
        return new string(buf, 0, (int)len);
    }

    public static string GetModuleFilename(nint hModule = 0)
    {
        int size = 256;
        while (size < 8192)
        {
            var buf = new char[size];
            uint res = NativeMethods.GetModuleFileNameW(hModule, buf, (uint)size);
            if (res == 0) return "";
            if (res < size) return new string(buf, 0, (int)res);
            size *= 2;
        }
        return "";
    }

    public static string AppFolder()
    {
        string path = GetModuleFilename();
        return path.Length > 0 ? Path.GetDirectoryName(path) ?? "" : "";
    }

    public static System.Diagnostics.Process StartProcess(string programName, string commandLine)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = programName,
            Arguments = commandLine,
            UseShellExecute = false,
        };
        var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {programName}");
        return proc;
    }

    /// <summary>Points Regedit at a specific key and opens it.</summary>
    public static void RegeditOpenAndNavigate(string registryPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit",
            writable: true);
        key.SetValue("LastKey", registryPath);
        StartProcess(Path.Combine(GetWindowsDir(), "regedit.exe"), "regedit.exe");
    }

    public static void ShellOpen(string command, string parameters = "")
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            Arguments = parameters,
            UseShellExecute = true,
            Verb = "open",
        };
        System.Diagnostics.Process.Start(psi);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern uint GetSystemDirectory([Out] char[] lpBuffer, uint uSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern uint GetWindowsDirectory([Out] char[] lpBuffer, uint uSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleFileNameW(nint hModule, [Out] char[] lpFilename, uint nSize);
    }
}
