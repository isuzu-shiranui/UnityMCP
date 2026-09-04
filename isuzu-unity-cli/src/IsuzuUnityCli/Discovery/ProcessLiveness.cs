using System.Runtime.InteropServices;

namespace IsuzuUnityCli.Discovery;

public static partial class ProcessLiveness
{
    /// <summary>A descriptor without a pid is kept: discarding it would drop a possibly valid Editor.</summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return true;
        }

        return OperatingSystem.IsWindows() ? IsAliveWindows(pid) : IsAliveUnix(pid);
    }

    // Process.GetProcessById enumerates every process on the machine to answer; opening the one
    // handle takes a fraction of that.
    private static bool IsAliveWindows(int pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        const int InvalidParameter = 87;
        const uint StillActive = 259;

        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);

        if (handle == IntPtr.Zero)
        {
            // A process of another user cannot be opened but does exist; only an unknown id is dead.
            return Marshal.GetLastWin32Error() != InvalidParameter;
        }

        try
        {
            return GetExitCodeProcess(handle, out var code) && code == StillActive;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsAliveUnix(int pid)
    {
        const int NoSuchProcess = 3;

        if (Kill(pid, 0) == 0)
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() != NoSuchProcess;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
