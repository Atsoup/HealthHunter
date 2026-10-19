using System.Runtime.InteropServices;
using System.Text;

namespace HealthHunter;

internal static class Native
{
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_PRIVATE = 0x20000;
    public const uint MEM_MAPPED = 0x40000;
    public const uint MEM_IMAGE = 0x1000000;

    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_WRITECOPY = 0x08;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    public const uint PAGE_GUARD = 0x100;

    public const uint LIST_MODULES_ALL = 0x03;

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress, void* buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool WriteProcessMemory(IntPtr hProcess, IntPtr baseAddress, void* buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr address, out MEMORY_BASIC_INFORMATION info, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr hProcess, out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint code);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] modules, int cb, out int needed, uint filter);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleBaseNameW(IntPtr hProcess, IntPtr module, StringBuilder name, int size);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetModuleInformation(IntPtr hProcess, IntPtr module, out MODULEINFO info, int cb);

    // Same calls Task Manager / Resource Monitor use for "Suspend process". Optional, off by default.
    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr hProcess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    // ---- v6 assistant: which window is in front, where the game's window is, and whether movement keys are held.
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    /// <summary>Read-only key state (is a key held), the same call games use themselves.</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
}
