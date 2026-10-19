using System.Diagnostics;
using System.Text;

namespace HealthHunter;

public sealed class ModuleInfo
{
    public string Name;
    public long Base;
    public long Size;
    public bool Contains(long addr) => addr >= Base && addr < Base + Size;
    public override string ToString() => $"{Name} @ {Base:X}";
}

public readonly struct MemRegion
{
    public readonly long Base;
    public readonly long Size;
    public readonly uint Type;
    public MemRegion(long b, long s, uint t) { Base = b; Size = s; Type = t; }
    public long End => Base + Size;
}

/// <summary>Read/write access to another process's memory.</summary>
public sealed unsafe class ProcessMemory : IDisposable
{
    public IntPtr Handle { get; private set; }
    public int Pid { get; }
    public string ProcessName { get; }
    public bool Is64Bit { get; }
    public int PointerSize => Is64Bit ? 8 : 4;
    public List<ModuleInfo> Modules { get; private set; } = new();

    // Processes / modules that indicate a kernel or user-mode anti-cheat.
    // HealthHunter refuses to attach to these: memory editing there gets accounts banned.
    private static readonly string[] AntiCheatModules =
    {
        "easyanticheat", "beclient", "battleye", "vgk", "faceit", "eaanticheat",
        "xigncode", "x3.xem", "nprotect", "gameguard", "ace-base", "anticheatexpert",
        "mhyprot", "equ8", "punkbuster", "pnkbstr", "ricochet", "nexon", "hackshield"
    };
    private static readonly string[] AntiCheatProcesses =
    {
        // Only services that start alongside a protected game (always-on services are skipped
        // so having another game installed doesn't block single-player titles).
        "easyanticheat", "easyanticheat_eos", "beservice", "beservice_x64",
        "eaanticheat.gameservice", "gameguard", "pnkbstra", "pnkbstrb"
    };

    private ProcessMemory(Process p, IntPtr handle)
    {
        Handle = handle;
        Pid = p.Id;
        ProcessName = p.ProcessName;
        Native.IsWow64Process(handle, out bool wow64);
        Is64Bit = Environment.Is64BitOperatingSystem && !wow64;
        RefreshModules();
    }

    public static ProcessMemory Open(Process p)
    {
        var h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE |
                                   Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION |
                                   Native.PROCESS_SUSPEND_RESUME, false, p.Id);
        if (h == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Could not open {p.ProcessName} (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}). " +
                "If the game runs as administrator, run HealthHunter as administrator too.");
        return new ProcessMemory(p, h);
    }

    /// <summary>Returns a reason string if this process uses anti-cheat, otherwise null.</summary>
    public string DetectAntiCheat()
    {
        foreach (var m in Modules)
        {
            var n = m.Name.ToLowerInvariant();
            foreach (var ac in AntiCheatModules)
                if (n.Contains(ac)) return $"module '{m.Name}' is loaded";
        }
        foreach (var proc in Process.GetProcesses())
        {
            string n;
            try { n = proc.ProcessName.ToLowerInvariant(); } catch { continue; }
            foreach (var ac in AntiCheatProcesses)
                if (n == ac) return $"anti-cheat service '{proc.ProcessName}' is running";
        }
        return null;
    }

    /// <summary>
    /// Size and file version of the game's exe, to notice when the game was updated since a profile was saved.
    /// </summary>
    public string ExeFingerprint()
    {
        try
        {
            using var p = Process.GetProcessById(Pid);
            var path = p.MainModule?.FileName;
            if (path == null || !File.Exists(path)) return null;
            var ver = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
            return $"{new FileInfo(path).Length}|{ver}";
        }
        catch { return null; }
    }

    /// <summary>Pauses every thread of the game (optional, while scanning). Always pair with Resume.</summary>
    public bool Suspend()
    {
        lock (_suspendLock)
        {
            if (Handle == IntPtr.Zero || Native.NtSuspendProcess(Handle) < 0) return false;
            _suspends++;
            return true;
        }
    }

    /// <summary>Undoes one successful <see cref="Suspend"/>. Extra calls do nothing.</summary>
    public void Resume()
    {
        lock (_suspendLock)
        {
            if (_suspends == 0 || Handle == IntPtr.Zero) return;
            _suspends--;
            Native.NtResumeProcess(Handle);
        }
    }

    // Suspends not resumed yet. Closing the handle resumes them, so the game is never left frozen (for example when
    // HealthHunter is detached or closed in the middle of a paused scan).
    private int _suspends;
    private readonly object _suspendLock = new();

    /// <summary>Parses "1A2B3C", "0x1A2B3C" or "game.exe+1F00" into an address.</summary>
    public long? ParseAddress(string text)
    {
        text = (text ?? "").Trim().Replace("\"", "");
        if (text.Length == 0) return null;
        int plus = text.LastIndexOf('+');
        long baseAddr = 0;
        if (plus > 0)
        {
            var mod = FindModule(text[..plus].Trim());
            if (mod == null) return null;
            baseAddr = mod.Base;
            text = text[(plus + 1)..].Trim();
        }
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var off) ? baseAddr + off : null;
    }

    public bool IsAlive
    {
        get
        {
            if (Handle == IntPtr.Zero) return false;
            return Native.GetExitCodeProcess(Handle, out uint code) && code == 259; // STILL_ACTIVE
        }
    }

    public void RefreshModules()
    {
        var list = new List<ModuleInfo>();
        var mods = new IntPtr[2048];
        int cb = IntPtr.Size * mods.Length;
        if (Native.EnumProcessModulesEx(Handle, mods, cb, out int needed, Native.LIST_MODULES_ALL))
        {
            int count = Math.Min(needed / IntPtr.Size, mods.Length);
            var sb = new StringBuilder(260);
            for (int i = 0; i < count; i++)
            {
                sb.Clear();
                Native.GetModuleBaseNameW(Handle, mods[i], sb, sb.Capacity);
                if (Native.GetModuleInformation(Handle, mods[i], out var info, sizeof(Native.MODULEINFO)))
                    list.Add(new ModuleInfo { Name = sb.ToString(), Base = (long)info.lpBaseOfDll, Size = info.SizeOfImage });
            }
        }
        Modules = list;
    }

    public ModuleInfo FindModule(string name) =>
        Modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public ModuleInfo ModuleAt(long addr) => Modules.FirstOrDefault(m => m.Contains(addr));

    /// <summary>Enumerates committed regions. writableOnly limits to pages a game can store data in.</summary>
    public List<MemRegion> GetRegions(bool writableOnly, bool includeMapped)
    {
        var list = new List<MemRegion>();
        long addr = 0;
        long max = Is64Bit ? 0x7FFF_FFFF_FFFF : 0xFFFF_FFFF;
        int mbiSize = sizeof(Native.MEMORY_BASIC_INFORMATION);
        while (addr < max)
        {
            if (Native.VirtualQueryEx(Handle, (IntPtr)addr, out var mbi, (IntPtr)mbiSize) == IntPtr.Zero) break;
            long b = (long)mbi.BaseAddress, s = (long)mbi.RegionSize;
            if (s <= 0) break;
            if (mbi.State == Native.MEM_COMMIT && (mbi.Protect & Native.PAGE_GUARD) == 0 && mbi.Protect != Native.PAGE_NOACCESS)
            {
                uint p = mbi.Protect & 0xFF;
                bool writable = p == Native.PAGE_READWRITE || p == Native.PAGE_WRITECOPY ||
                                p == Native.PAGE_EXECUTE_READWRITE || p == Native.PAGE_EXECUTE_WRITECOPY;
                bool readable = writable || p == Native.PAGE_READONLY || p == Native.PAGE_EXECUTE_READ;
                bool typeOk = mbi.Type != Native.MEM_MAPPED || includeMapped;
                if (readable && typeOk && (!writableOnly || writable))
                {
                    // Merge adjacent regions to reduce read calls.
                    if (list.Count > 0 && list[^1].End == b && list[^1].Type == mbi.Type)
                        list[^1] = new MemRegion(list[^1].Base, list[^1].Size + s, mbi.Type);
                    else
                        list.Add(new MemRegion(b, s, mbi.Type));
                }
            }
            addr = b + s;
        }
        return list;
    }

    public int Read(long address, byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        fixed (byte* p = &buffer[offset])
        {
            Native.ReadProcessMemory(Handle, (IntPtr)address, p, (IntPtr)count, out var read);
            return (int)read;
        }
    }

    public const int PageSize = 4096;

    /// <summary>
    /// Reads like <see cref="Read"/>, but never loses a whole block because one page in it is unreadable.
    /// ReadProcessMemory fails (or stops short) when any page in the range is decommitted, guarded or freed,
    /// which happens all the time in a running game's heap. When the fast single read comes back short,
    /// this re-reads page by page. ok[i] tells whether page i (counted from the page containing address) was read.
    /// Unreadable pages are zero-filled. Returns the number of unreadable bytes.
    /// </summary>
    public int ReadPaged(long address, byte[] buffer, int count, bool[] ok)
    {
        int pages = PageCount(address, count);
        int got = Read(address, buffer, 0, count);
        if (got >= count)
        {
            Array.Fill(ok, true, 0, pages);
            return 0;
        }
        int bad = 0;
        long pageBase = address & ~(long)(PageSize - 1);
        for (int i = 0; i < pages; i++)
        {
            long ps = Math.Max(address, pageBase + (long)i * PageSize);
            long pe = Math.Min(address + count, pageBase + (long)(i + 1) * PageSize);
            int off = (int)(ps - address), len = (int)(pe - ps);
            if (len <= 0) { ok[i] = false; continue; }
            int r = Read(ps, buffer, off, len);
            ok[i] = r == len;
            if (!ok[i]) { Array.Clear(buffer, off, len); bad += len; }
        }
        return bad;
    }

    public static int PageCount(long address, int count) =>
        count <= 0 ? 0 : (int)(((address + count - 1) >> 12) - (address >> 12) + 1);

    /// <summary>True when every byte of [address+off, address+off+size) lies in a page ReadPaged could read.</summary>
    public static bool PagesOk(bool[] ok, long address, int off, int size)
    {
        long first = address >> 12;
        int a = (int)(((address + off) >> 12) - first), b = (int)(((address + off + size - 1) >> 12) - first);
        return ok[a] && ok[b];
    }

    public bool ReadBytes(long address, Span<byte> dest)
    {
        fixed (byte* p = dest)
            return Native.ReadProcessMemory(Handle, (IntPtr)address, p, (IntPtr)dest.Length, out var read) && (int)read == dest.Length;
    }

    public bool WriteBytes(long address, ReadOnlySpan<byte> src)
    {
        fixed (byte* p = src)
            return Native.WriteProcessMemory(Handle, (IntPtr)address, p, (IntPtr)src.Length, out var w) && (int)w == src.Length;
    }

    public bool ReadPointer(long address, out long value)
    {
        Span<byte> b = stackalloc byte[8];
        b.Clear();
        if (!ReadBytes(address, b[..PointerSize])) { value = 0; return false; }
        value = Is64Bit ? BitConverter.ToInt64(b) : BitConverter.ToUInt32(b);
        return true;
    }

    public bool TryReadValue(long address, ValueKind kind, out double value)
    {
        Span<byte> b = stackalloc byte[8];
        int size = kind.Size();
        if (!ReadBytes(address, b[..size])) { value = 0; return false; }
        value = kind.Decode(b);
        return true;
    }

    public bool WriteValue(long address, ValueKind kind, double value)
    {
        Span<byte> b = stackalloc byte[8];
        kind.Encode(value, b);
        return WriteBytes(address, b[..kind.Size()]);
    }

    public void Dispose()
    {
        lock (_suspendLock)
        {
            if (Handle != IntPtr.Zero)
            {
                for (; _suspends > 0; _suspends--) Native.NtResumeProcess(Handle);
                Native.CloseHandle(Handle);
            }
            _suspends = 0;
            Handle = IntPtr.Zero;
        }
    }
}
