using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MaxFlowWindows.Core;

public sealed class ProcessJob : IDisposable
{
    private nint _jobHandle;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public ulong ProcessMemoryLimit;
        public ulong JobMemoryLimit;
        public ulong PeakProcessMemoryUsed;
        public ulong PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
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

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, JobObjectInfoType infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int infoLength);

    public ProcessJob()
    {
        _jobHandle = CreateJobObject(nint.Zero, null);
        if (_jobHandle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create job object.");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, ref info, size))
        {
            CloseHandle(_jobHandle);
            _jobHandle = nint.Zero;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set job object limits.");
        }

        nint currentProcess = GetCurrentProcess();
        if (!AssignProcessToJobObject(_jobHandle, currentProcess))
        {
            CloseHandle(_jobHandle);
            _jobHandle = nint.Zero;
        }
    }

    public bool AddProcess(Process process)
    {
        if (_jobHandle == nint.Zero)
            return false;

        try
        {
            return AssignProcessToJobObject(_jobHandle, process.Handle);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_jobHandle != nint.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = nint.Zero;
        }
    }
}