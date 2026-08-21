using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SanmiToys.Modules.FluidDrag.Core;

public class RunningAppInfo
{
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public IntPtr Hwnd { get; set; }
}

public static class RunningAppFinder
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public static List<RunningAppInfo> GetRunningWindows()
    {
        var list = new List<RunningAppInfo>();
        var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int currentPid = Process.GetCurrentProcess().Id;

        EnumWindows((hwnd, lParam) =>
        {
            if (!FluidDragNativeMethods.IsWindowVisible(hwnd)) return true;
            if (FluidDragNativeMethods.IsIconic(hwnd)) return true;

            string title = FluidDragNativeMethods.GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            FluidDragNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid || pid == 0) return true;

            string procName = FluidDragNativeMethods.GetProcessNameFromHwnd(hwnd);
            if (string.IsNullOrWhiteSpace(procName)) return true;

            if (procName is "explorer" or "SearchHost" or "ShellExperienceHost" or "StartMenuExperienceHost" or "TextInputHost" or "ApplicationFrameHost")
            {
                return true;
            }

            if (!seenProcesses.Contains(procName))
            {
                seenProcesses.Add(procName);

                string appName = procName;
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    string? exePath = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                    {
                        var vi = FileVersionInfo.GetVersionInfo(exePath);
                        if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                        {
                            appName = $"{vi.FileDescription} ({procName}.exe)";
                        }
                        else
                        {
                            appName = $"{procName}.exe";
                        }
                    }
                    else
                    {
                        appName = $"{procName}.exe";
                    }
                }
                catch
                {
                    appName = $"{procName}.exe";
                }

                list.Add(new RunningAppInfo
                {
                    ProcessName = procName,
                    DisplayName = appName,
                    WindowTitle = title,
                    Hwnd = hwnd
                });
            }

            return true;
        }, IntPtr.Zero);

        list.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
