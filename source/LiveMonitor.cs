using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SimpleUnlockerRemastered;

/// <summary>Системные вызовы Windows, нужные для LIVE-режима.</summary>
internal static class NativeMethods
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int samDesired, out IntPtr result);

    [DllImport("advapi32.dll")]
    internal static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, int notifyFilter, IntPtr hEvent, bool asynchronous);

    [DllImport("advapi32.dll")]
    internal static extern int RegCloseKey(IntPtr hKey);

    [DllImport("kernel32.dll")]
    internal static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [DllImport("kernel32.dll")]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

/// <summary>Общая загрузка CPU и RAM (обновляется раз в секунду из таймера формы).</summary>
internal static class SystemLoad
{
    private static ulong prevIdle, prevTotal;
    private static bool hasPrev;

    private static ulong ToU64(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    public static double CpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong idleNow = ToU64(idle);
        ulong totalNow = ToU64(kernel) + ToU64(user); // kernel уже включает idle
        double result = 0;
        if (hasPrev)
        {
            ulong dTotal = totalNow - prevTotal;
            ulong dIdle = idleNow - prevIdle;
            if (dTotal > 0) result = (1.0 - (double)dIdle / dTotal) * 100.0;
        }
        prevIdle = idleNow; prevTotal = totalNow; hasPrev = true;
        return Math.Clamp(result, 0, 100);
    }

    public static bool Memory(out double usedGb, out double totalGb, out int percent)
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        usedGb = totalGb = 0; percent = 0;
        if (!NativeMethods.GlobalMemoryStatusEx(ref m)) return false;
        totalGb = m.ullTotalPhys / 1073741824.0;
        usedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        percent = (int)m.dwMemoryLoad;
        return true;
    }
}

/// <summary>
/// Событийное наблюдение за веткой реестра (RegNotifyChangeKeyValue).
/// Работает в фоновом потоке, сам переоткрывает ключ, если тот был удалён/ещё не существует.
/// </summary>
internal sealed class RegistryWatcher : IDisposable
{
    public static readonly IntPtr HKCU = new IntPtr(unchecked((int)0x80000001));
    public static readonly IntPtr HKLM = new IntPtr(unchecked((int)0x80000002));

    private const int KEY_NOTIFY = 0x0010;
    private const int KEY_WOW64_64KEY = 0x0100;
    private const int REG_NOTIFY_CHANGE_NAME = 0x1;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    private readonly IntPtr hive;
    private readonly string path;
    private readonly bool subtree;
    private readonly Action onChange;
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly Thread thread;
    private volatile bool active;

    public bool Active => active;

    public RegistryWatcher(IntPtr hive, string path, bool subtree, Action onChange)
    {
        this.hive = hive; this.path = path; this.subtree = subtree; this.onChange = onChange;
        thread = new Thread(Loop) { IsBackground = true, Name = "RegWatch:" + path };
        thread.Start();
    }

    private void Loop()
    {
        while (!stop.WaitOne(0))
        {
            if (NativeMethods.RegOpenKeyEx(hive, path, 0, KEY_NOTIFY | KEY_WOW64_64KEY, out var h) != 0)
            {
                active = false;
                if (stop.WaitOne(5000)) return; // ключа нет — пробуем снова через 5 с
                continue;
            }
            try
            {
                using var ev = new ManualResetEvent(false);
                active = true;
                while (true)
                {
                    ev.Reset();
                    int rc = NativeMethods.RegNotifyChangeKeyValue(h, subtree,
                        REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, ev.SafeWaitHandle.DangerousGetHandle(), true);
                    if (rc != 0) break; // ключ удалён — переоткрыть
                    int idx = WaitHandle.WaitAny(new WaitHandle[] { stop, ev });
                    if (idx == 0) return;
                    try { onChange(); } catch { }
                }
            }
            finally
            {
                active = false;
                NativeMethods.RegCloseKey(h);
            }
            if (stop.WaitOne(1000)) return;
        }
    }

    public void Dispose()
    {
        try { stop.Set(); } catch { }
    }
}

/// <summary>Сортировка списка процессов (числовые колонки сравниваются как числа).</summary>
internal sealed class ProcState
{
    public TimeSpan LastCpu;
    public DateTime LastTime;
    public double Cpu;
    public double MemMb;
}

internal sealed class ProcSorter : System.Collections.IComparer
{
    public int Column = 3;
    public bool Descending = true;

    public int Compare(object? x, object? y)
    {
        var a = (ListViewItem)x!; var b = (ListViewItem)y!;
        int r;
        switch (Column)
        {
            case 0:
                r = int.Parse(a.Text).CompareTo(int.Parse(b.Text)); break;
            case 2:
                r = ((ProcState)a.Tag!).Cpu.CompareTo(((ProcState)b.Tag!).Cpu); break;
            case 3:
                r = ((ProcState)a.Tag!).MemMb.CompareTo(((ProcState)b.Tag!).MemMb); break;
            default:
                r = string.Compare(a.SubItems[Column].Text, b.SubItems[Column].Text, StringComparison.OrdinalIgnoreCase); break;
        }
        if (r == 0) r = string.Compare(a.SubItems[1].Text, b.SubItems[1].Text, StringComparison.OrdinalIgnoreCase);
        return Descending ? -r : r;
    }
}
