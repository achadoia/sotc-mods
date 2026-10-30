// crashwatch: gruda no bb-probe como depurador e, quando o jogo cai (violação de acesso em código do
// jogo), grava registradores e a pilha de chamadas (cadeia de rbp + varredura) com os endereços relativos
// ao eboot em mod_backup\crashwatch.log. A exceção segue para o jogo (DBG_EXCEPTION_NOT_HANDLED), que
// continua gerando o seu próprio .dmp. Faltas de escrita (o loader usa páginas protegidas para vigiar a
// memória) são ignoradas. Uso: crashwatch [pasta_do_log]  (termina quando o jogo fecha)
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class Watch
{
    [DllImport("kernel32", SetLastError = true)] static extern bool DebugActiveProcess(int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool DebugActiveProcessStop(int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32", SetLastError = true)] static extern bool WaitForDebugEvent(ref DEBUG_EVENT ev, uint ms);
    [DllImport("kernel32", SetLastError = true)] static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenThread(int access, bool inherit, uint tid);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI info, IntPtr len);
    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public uint pad0; public IntPtr RegionSize; public uint State, Protect, Type, pad1; }

    const uint EXCEPTION_DEBUG_EVENT = 1, CREATE_THREAD = 2, CREATE_PROCESS = 3, EXIT_THREAD = 4, EXIT_PROCESS = 5, LOAD_DLL = 6, UNLOAD_DLL = 7, OUTPUT_STRING = 8, RIP_EVENT = 9;
    const uint DBG_CONTINUE = 0x00010002, DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
    const uint CONTEXT_FULL = 0x10000B;   // CONTEXT_AMD64 | CONTROL | INTEGER | SEGMENTS

    [StructLayout(LayoutKind.Sequential)]
    struct DEBUG_EVENT
    {
        public uint dwDebugEventCode, dwProcessId, dwThreadId;
        // união: EXCEPTION_DEBUG_INFO = EXCEPTION_RECORD (152 bytes) + dwFirstChance; reservamos o maior membro
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)] public byte[] u;
    }

    static readonly byte[] AnchorBytes = Hex("4889FB488B43304885C0740D4889DFFFD048C7433000000000");
    const long ANCHOR = 0x577967;
    static byte[] Hex(string s) { var b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16); return b; }

    static IntPtr proc; static long Base; static string LogPath; static int logged;

    static void Log(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n", Encoding.UTF8); } catch { }
    }

    static bool FindBase()
    {
        long addr = 0; MBI m; var buf = new byte[1 << 22];
        while (VirtualQueryEx(proc, new IntPtr(addr), out m, new IntPtr(Marshal.SizeOf(typeof(MBI)))) != IntPtr.Zero)
        {
            long size = m.RegionSize.ToInt64();
            if (m.State == 0x1000 && (m.Protect & 0x100) == 0 && (m.Protect & 0xF0) != 0)
            {
                for (long off = 0; off < size; off += buf.Length - AnchorBytes.Length)
                {
                    int len = (int)Math.Min(buf.Length, size - off); IntPtr rd;
                    if (!ReadProcessMemory(proc, new IntPtr(addr + off), buf, new IntPtr(len), out rd)) break;
                    int n = (int)rd;
                    for (int i = 0; i + AnchorBytes.Length <= n; i++)
                    {
                        if (buf[i] != AnchorBytes[0]) continue;
                        int k = 1; while (k < AnchorBytes.Length && buf[i + k] == AnchorBytes[k]) k++;
                        if (k == AnchorBytes.Length) { Base = addr + off + i - ANCHOR; return true; }
                    }
                    if (len < buf.Length) break;
                }
            }
            addr += size; if (addr <= 0) break;
        }
        return false;
    }

    static bool ReadQ(long at, out ulong v)
    {
        var b = new byte[8]; IntPtr rd; v = 0;
        if (!ReadProcessMemory(proc, new IntPtr(at), b, new IntPtr(8), out rd) || rd.ToInt64() != 8) return false;
        v = BitConverter.ToUInt64(b, 0); return true;
    }

    static string Rel(ulong v)
    {
        if (Base != 0 && v >= (ulong)Base && v < (ulong)Base + 0x4000000) return " = eboot+" + (v - (ulong)Base).ToString("X");
        return "";
    }

    static void Dump(uint tid, uint code, ulong excAddr, ulong info0, ulong info1)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format("==== exceção {0:X8} na thread {1}: rip {2:X}{3}; acesso {4} em {5:X}", code, tid, excAddr, Rel(excAddr), info0 == 0 ? "leitura" : info0 == 1 ? "escrita" : "exec/" + info0, info1));
        IntPtr th = OpenThread(0x1F03FF, false, tid);
        if (th == IntPtr.Zero) { sb.AppendLine("OpenThread falhou"); Log(sb.ToString()); return; }
        IntPtr ctx = Marshal.AllocHGlobal(1232 + 16);
        try
        {
            long aligned = (ctx.ToInt64() + 15) & ~15L; IntPtr c = new IntPtr(aligned);
            for (int i = 0; i < 1232; i++) Marshal.WriteByte(c, i, 0);
            Marshal.WriteInt32(c, 0x30, (int)CONTEXT_FULL);
            if (!GetThreadContext(th, c)) { sb.AppendLine("GetThreadContext falhou: " + Marshal.GetLastWin32Error()); Log(sb.ToString()); return; }
            string[] names = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "rip" };
            var regs = new ulong[17];
            for (int i = 0; i < 17; i++) { regs[i] = (ulong)Marshal.ReadInt64(c, 0x78 + i * 8); sb.AppendLine(string.Format("  {0,-3} = {1:X}{2}", names[i], regs[i], Rel(regs[i]))); }
            ulong rsp = regs[4], rbp = regs[5];
            sb.AppendLine("  -- cadeia de rbp:");
            ulong fp = rbp;
            for (int f = 0; f < 40 && fp != 0; f++)
            {
                ulong ret, next;
                if (!ReadQ((long)fp + 8, out ret) || !ReadQ((long)fp, out next)) { sb.AppendLine("  (fim: rbp " + fp.ToString("X") + " ilegível)"); break; }
                sb.AppendLine(string.Format("  #{0} rbp {1:X} ret {2:X}{3}", f, fp, ret, Rel(ret)));
                if (next <= fp || next - fp > 0x100000) { sb.AppendLine("  (fim: próximo rbp " + next.ToString("X") + ")"); break; }
                fp = next;
            }
            sb.AppendLine("  -- varredura da pilha (valores no eboot) a partir de rsp:");
            var buf = new byte[0x1800]; IntPtr rd;
            if (ReadProcessMemory(proc, new IntPtr((long)rsp), buf, new IntPtr(buf.Length), out rd))
            {
                int n = (int)rd.ToInt64(), shown = 0;
                for (int i = 0; i + 8 <= n && shown < 80; i += 8)
                {
                    ulong v = BitConverter.ToUInt64(buf, i);
                    string r = Rel(v);
                    if (r != "") { sb.AppendLine(string.Format("  [rsp+{0:X}] {1:X}{2}", i, v, r)); shown++; }
                }
            }
            else sb.AppendLine("  (pilha ilegível em " + rsp.ToString("X") + ")");
        }
        finally { Marshal.FreeHGlobal(ctx); CloseHandle(th); }
        Log(sb.ToString());
    }

    static int Main(string[] a)
    {
        string dir = a.Length > 0 ? a[0] : AppDomain.CurrentDomain.BaseDirectory;
        LogPath = Path.Combine(dir, "crashwatch.log");
        Process p = null;
        Console.WriteLine("esperando o bb-probe...");
        while ((p = Find()) == null) Thread.Sleep(1000);
        Thread.Sleep(3000);   // deixa o loader terminar de preparar o processo
        proc = OpenProcess(0x0010 | 0x0400, false, p.Id);
        if (proc == IntPtr.Zero) { Log("OpenProcess falhou"); return 1; }
        if (!DebugActiveProcess(p.Id)) { Log("DebugActiveProcess falhou: " + Marshal.GetLastWin32Error()); return 1; }
        DebugSetProcessKillOnExit(false);
        Log("vigiando o bb-probe (pid " + p.Id + ")");
        var ev = new DEBUG_EVENT(); ev.u = new byte[160];
        int baseTries = 0;
        try
        {
            while (true)
            {
                if (!WaitForDebugEvent(ref ev, 1000))
                {
                    if (p.HasExited) break;
                    if (Base == 0 && baseTries++ < 30) { if (FindBase()) Log("base do eboot " + Base.ToString("X")); }
                    continue;
                }
                uint status = DBG_CONTINUE;
                if (ev.dwDebugEventCode == EXCEPTION_DEBUG_EVENT)
                {
                    uint code = BitConverter.ToUInt32(ev.u, 0);
                    ulong excAddr = BitConverter.ToUInt64(ev.u, 16);
                    uint nparams = BitConverter.ToUInt32(ev.u, 24);
                    ulong info0 = nparams > 0 ? BitConverter.ToUInt64(ev.u, 32) : 0, info1 = nparams > 1 ? BitConverter.ToUInt64(ev.u, 40) : 0;
                    uint firstChance = BitConverter.ToUInt32(ev.u, 152);
                    status = DBG_EXCEPTION_NOT_HANDLED;
                    if (code == 0x80000003 || code == 0x4000001F || code == 0x80000004) status = DBG_CONTINUE;   // breakpoint do attach / single step
                    else if (code == 0xC0000005 || code == 0xC000001D || code == 0xC0000409 || code == 0xC000008C || code == 0xC0000094 || code == 0xC0000096)
                    {
                        // escrita em memória mapeada = vigilância do loader (ignorada); o resto é queda provável
                        bool tracking = code == 0xC0000005 && info0 == 1 && info1 >= 0x10000;
                        if (!tracking && logged < 40)
                        {
                            logged++;
                            if (Base == 0) FindBase();
                            Dump(ev.dwThreadId, code, excAddr, info0, info1);
                        }
                    }
                }
                else if (ev.dwDebugEventCode == EXIT_PROCESS) { Log("jogo fechado"); ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, DBG_CONTINUE); break; }
                else if (ev.dwDebugEventCode == CREATE_PROCESS || ev.dwDebugEventCode == LOAD_DLL)
                {
                    // fecha os handles que o sistema entrega nesses eventos (hFile)
                    IntPtr hFile = (IntPtr)BitConverter.ToInt64(ev.u, 0);
                    if (hFile != IntPtr.Zero) CloseHandle(hFile);
                }
                ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, status);
            }
        }
        catch (Exception e) { Log("erro: " + e.Message); }
        finally { try { DebugActiveProcessStop(p.Id); } catch { } }
        return 0;
    }

    static Process Find()
    {
        foreach (var p in Process.GetProcessesByName("bb-probe")) return p;
        return null;
    }
}
