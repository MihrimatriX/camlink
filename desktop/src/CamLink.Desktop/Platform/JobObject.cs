using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CamLink.Desktop.Platform;

/// <summary>
/// Bağlanan süreçler (FFmpeg) CamLink kapandığında, çökse bile, Windows tarafından sonlandırılır.
/// Kaynak: deckpad 48923a0 studiohub/src/StudioHub/Platform/JobObject.cs.
/// </summary>
public sealed class JobObject
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // Tanıtıcı bilerek hiç kapatılmaz: süreç bitince Windows kapatır ve iş nesnesindeki her şeyi öldürür.
    readonly IntPtr _job;

    public JobObject()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            App.Log($"İş nesnesi oluşturulamadı (Win32 {Marshal.GetLastWin32Error()}); FFmpeg uygulama çökerse açık kalabilir.");
            return;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            App.Log($"İş nesnesine KILL_ON_JOB_CLOSE verilemedi (Win32 {Marshal.GetLastWin32Error()}); FFmpeg uygulama çökerse açık kalabilir.");
    }

    public void Add(Process p)
    {
        if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, p.Handle))
            App.Log($"FFmpeg (PID {p.Id}) iş nesnesine eklenemedi (Win32 {Marshal.GetLastWin32Error()}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
