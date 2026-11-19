using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Agents;

/// <summary>
/// A Windows job object for ONE child process and everything it spawns, created with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. <see cref="Terminate"/> kills the whole tree atomically (including
/// grandchildren started a moment before the kill), and if the host (AutoCAD) exits or crashes, Windows closes the
/// handle and kills the tree too. No-op on other operating systems; failures are logged and the caller falls back
/// to Process.Kill(entireProcessTree).
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private IntPtr _handle;

    private ChildProcessJob(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>Creates a job and assigns <paramref name="process"/> to it; null when that is not possible.</summary>
    public static ChildProcessJob? TryCreateFor(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            CopilotLog.Warn("CreateJobObject failed", new Win32Exception(Marshal.GetLastWin32Error()));
            return null;
        }

        var job = new ChildProcessJob(handle);
        try
        {
            if (!job.SetKillOnClose())
            {
                job.Dispose();
                return null;
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                CopilotLog.Warn($"Could not add process {process.Id} to a job object", new Win32Exception(Marshal.GetLastWin32Error()));
                job.Dispose();
                return null;
            }

            return job;
        }
        catch (InvalidOperationException)
        {
            job.Dispose();
            return null;
        }
    }

    /// <summary>Kills every process in the job. Returns false when the call failed.</summary>
    public bool Terminate()
    {
        var handle = _handle;
        return handle != IntPtr.Zero && TerminateJobObject(handle, 1);
    }

    /// <summary>Closes the job handle (which also kills anything still running in it).</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    private bool SetKillOnClose()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CopilotLog.Warn("SetInformationJobObject failed", new Win32Exception(Marshal.GetLastWin32Error()));
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
