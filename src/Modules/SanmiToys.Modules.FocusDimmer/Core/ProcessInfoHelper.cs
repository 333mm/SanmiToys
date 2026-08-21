using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SanmiToys.Modules.FocusDimmer.Core;

public static class ProcessInfoHelper
{
    private static readonly ConcurrentDictionary<uint, string> _processNameCache = new();

    public static string GetProcessName(uint pid)
    {
        if (pid == 0) return string.Empty;

        if (_processNameCache.TryGetValue(pid, out var cachedName))
        {
            return cachedName;
        }

        string name = QueryProcessName(pid);
        _processNameCache[pid] = name;
        return name;
    }

    public static void ClearCache()
    {
        _processNameCache.Clear();
    }

    private static string QueryProcessName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.ToLowerInvariant();
        }
        catch
        {
            IntPtr hProcess = FocusDimmerNativeMethods.OpenProcess(0x1000, false, pid);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var buffer = new StringBuilder(1024);
                    int size = buffer.Capacity;
                    if (FocusDimmerNativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                    {
                        return System.IO.Path.GetFileNameWithoutExtension(buffer.ToString()).ToLowerInvariant();
                    }
                }
                finally
                {
                    FocusDimmerNativeMethods.CloseHandle(hProcess);
                }
            }
        }
        return string.Empty;
    }
}
