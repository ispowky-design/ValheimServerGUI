using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ValheimServerGUI.Game.Mods;

/// <summary>
/// The executable of a running process, read with the least rights the OS asks for, so that a server started by
/// another tool or user can usually still be located: <c>QueryFullProcessImageName</c> on Windows, the
/// <c>/proc/&lt;pid&gt;/exe</c> link on Linux. Null when it cannot be read.
/// </summary>
internal static class ProcessImagePath
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static string? Get(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return GetWindows(process.Id);
            var link = new FileInfo($"/proc/{process.Id}/exe").ResolveLinkTarget(returnFinalTarget: true);
            return link?.FullName ?? process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetWindows(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
