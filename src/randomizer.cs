// SotC randomizer: runs beside the game (D:\SotC-PC port, EU 1.01 / US 1.00 eboot) and patches it in memory.
// Two modes (both can be on):
//   items   - every colossus defeated puts a random Time Attack reward in the shrine fountain
//   colossi - the order of the colossi is shuffled: the game picks "the next colossus" through
//             FirstAliveBoss() (0x7D1A70); a code cave makes that function walk our order instead of the
//             table order. The sword light, the arena, the arrival cutscene and the return to the shrine all
//             follow the chosen entry; the ending starts when no colossus is left alive, whichever was last.
// All addresses are eboot virtual addresses; the eboot base is found by scanning.
//   - game flags: bit array at 0x325F3B0; FlagGet 0x7dbba0, FlagSet 0x7dbb00, FlagClear 0x7dbb50
//     DEAD_<boss> 40..55 (1 = alive), LAYOUT_HOME_ITEMn_LIGHT 164+n, HOME_ITEMn_START_CAM 190+n (n = 1..16)
//   - boss table: 16 entries of 0x1E8 bytes at 0x16D4010 in campaign order (Valus .. Malus); +0x2C = DEAD flag
//   - current boss slot: 0x325F7B8 (written by SetCurrentBoss 0x7D1900)
//   - cave 1 (0x1593980) hooks FlagGet: mailbox dword (0x3956FF0) holding flag+1 -> FlagSet(flag) on the game
//     thread (bit 16: FlagClear); cave 2 (0x1593A00) replaces FirstAliveBoss while ORDER_VALID (0x3956FD0) is 1,
//     using ORDER (16 slot bytes at 0x3956FC0); cave 3 (0x1593B00) counts kills on KillBoss 0x7D1700;
//     cave 4 (0x1593BC0, v2.12) makes the shrine arrival routine 0x7E78A0 use the entry of the k-th campaign
//     colossus (k = kills so far), so the k-th return plays the k-th vanilla arrival scene (k shadows)
//   - null guard on the item finaliser 0x577960 (crashes when the item sequence lacks its object)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Game
{
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI info, IntPtr len);
    [DllImport("kernel32", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint prot, out uint old);
    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public uint pad0; public IntPtr RegionSize; public uint State, Protect, Type, pad1; }

    public const long FLAGS = 0x325F3B0, FLAGGET = 0x7DBBA0, FLAGSET = 0x7DBB00, FLAGCLEAR = 0x7DBB50;
    public const long GIVEITEM = 0x7DBF60;   // GiveItem(id): liga o bit do item em 0x325F7A2 e avisa o menu (o mesmo que shadow_giveAll*Items usa)
    // o bit do item é escrito direto daqui (v2.4): chamar GiveItem de dentro do gancho de FlagGet (aviso do menu
    // 0x7837F0 numa hora qualquer, p.ex. na cena da morte) coincidiu com um crash no autosave depois do Malus
    public const long ITEMS = 0x325F7A2;
    public const long CAVE = 0x1593980, MAILBOX = 0x3956FF0, ANCHOR = 0x577967;
    public const long CAVE2 = 0x1593A00, ORDER = 0x3956FC0, ORDER_VALID = 0x3956FD0;
    public const long FIRSTALIVE = 0x7D1A70, ENTRIES = 0x16D4010, ENTRY_SIZE = 0x1E8, CURRENT_SLOT = 0x325F7B8;
    // cave 3: KillBoss(entry) hook. KILLCOUNT/KILLPTR tell the randomizer about every kill the game registers;
    // GAMEFLOW_KILL_RET is the return address of the call made when a save taken right after a kill is loaded.
    public const long CAVE3 = 0x1593B00, KILLBOSS = 0x7D1700, LASTKILLED = 0x325E8A0, KILLCOUNT = 0x3956FE0, KILLPTR = 0x3956FE8, GAMEFLOW_KILL_RET = 0x7E0851;
    static readonly byte[] KillBossOld = Hex("48893D99D1A802");          // mov [rip+LASTKILLED], rdi
    // next game's order: when the gameflow asks for the first colossus of a new game (return address
    // GAMEFLOW_PICK_RET) cave 2 moves NEXT_ORDER into ORDER and raises NEWGAME so the randomizer notices.
    public const long NEXT_ORDER = 0x3956FA0, NEXT_VALID = 0x3956FB0, NEWGAME = 0x3956FD1, GAMEFLOW_PICK_RET = 0x7E0124;
    // cave 4 (v2.12): the shrine arrival routine 0x7E78A0 (gameflow state 5, live return and reload of the post-kill
    // autosave alike) starts with "call GetLastKilled" (0x7D1710 -> [LASTKILLED]) and drives EVERYTHING from that
    // boss entry: demo A (+0x130/+0x138, the shadows scene "Temple_(NN)_boss_disappears_demo", skipped when flag 27
    // is set = reload), demo B (+0x128, +0x140/+0x148), optional demo C (+0x150..+0x160), the Pelagia special case
    // (entry == [0x16D5EE8]: Agro absent on arrival) and finally FlagSet(entry+0x34) = <boss>_HOME_END. With a
    // shuffled order the k-th kill therefore showed the scene of whichever colossus died (its number of shadows).
    // Cave 4 replaces that one call: while ORDER_VALID it returns the entry of the k-th campaign colossus, k = dead
    // count (0x7D1740, already including the colossus that just died), so the k-th return plays the k-th vanilla
    // scene. LASTKILLED itself is untouched; DEMO_ENTRY receives the entry chosen (0 = original path) for the log.
    public const long CAVE4 = 0x1593BC0, DEMO_CALL = 0x7E78BF, DEADCOUNT = 0x7D1740, LASTKILLED_GET = 0x7D1710, DEMO_ENTRY = 0x3956F90;
    static readonly byte[] DemoCallOld = Hex("E84C9EFEFF");                // call 0x7D1710 at 0x7E78BF
    // cave 5 (v2.13, "mapa das moedas"): the game-side world-map builder 0x78FA30 fills cp10::ShadowWorldMapState
    // (0x2EF9EC0) every map update: save points (push12 0x499060 -> vec +0x38), dead colossi (push16 0x498EA0 -> +0x58,
    // icon index), FRUIT TREES (push12 0x499180 -> +0x78, only with item 37 / debug 0x3256464), lizards (0x4992B0 ->
    // +0x98, item 38), breadcrumbs (0x498D80 -> +0x18), icon groups (0x498FA0 -> +0xB8). The fruit section starts at
    // 0x78FEAA with "call 0x7F72B0" (fruit manager) and ends at 0x7900E2 (lizard section). Cave 5 replaces that call:
    // it pushes every entry of our COINS table (x, y, z floats) through push12 and jumps to 0x7900E2, so the coins are
    // drawn where the fruit trees would be (same icon, same toggle). COIN_GATE is the "je 0x7900E2" taken when the
    // fruit map item is missing; NOPed so the coins show without the item.
    public const long CAVE5 = 0x1593C10, CAVE5_END = 0x1593D00, COIN_HOOK = 0x78FEAA, COIN_GATE = 0x78FEA4, COIN_RESUME = 0x7900E2, MAPSTATE = 0x2EF9EC0, PUSH_FRUIT = 0x499180;
    public const long COIN_COUNT = 0x3956AB8, COINS = 0x3956AC0, COIN_MAX = 85;   // data page slack after the segment end (0x3956AA4), before the mailboxes at 0x3956F00
    static readonly byte[] CoinHookOld = Hex("E801740600");               // call 0x7F72B0 at 0x78FEAA
    static readonly byte[] CoinGateOld = Hex("0F8438020000"), CoinGateNew = Hex("909090909090");   // je 0x7900E2 at 0x78FEA4
    static readonly byte[] AnchorBytes = Hex("4889FB488B43304885C0740D4889DFFFD048C7433000000000");
    static readonly byte[] GuardTrampOld = Hex("909090909090909090"), GuardTrampNew = Hex("554889E5415653EB09");
    static readonly byte[] GuardEntryOld = Hex("554889E5415653"), GuardEntryNew = Hex("4885FF74DDEBEE");
    static readonly byte[] FlagGetOld = Hex("89F8488D0D0738A802");
    static readonly byte[] FirstAliveOld = Hex("554889E54157");          // push rbp; mov rbp,rsp; push r15

    public Process Proc; IntPtr h; public long Base;

    public static Process Find()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Equals("bb-probe", StringComparison.OrdinalIgnoreCase) && p.MainModule.FileName.StartsWith(Randomizer.PortDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch { }
        }
        return null;
    }

    public Game(Process p)
    {
        Proc = p;
        h = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, p.Id);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess failed");
    }

    public static byte[] Hex(string s) { var b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16); return b; }
    static byte[] Rel32(long next, long dst) { return BitConverter.GetBytes((int)(dst - next)); }

    public byte[] Read(long va, int n)
    {
        var b = new byte[n]; IntPtr rd;
        if (!ReadProcessMemory(h, new IntPtr(Base + va), b, new IntPtr(n), out rd) || (int)rd != n) throw new Exception(string.Format("read {0:X} failed", va));
        return b;
    }
    public byte[] ReadAbs(long abs, int n)
    {
        var b = new byte[n]; IntPtr rd;
        if (!ReadProcessMemory(h, new IntPtr(abs), b, new IntPtr(n), out rd) || (int)rd != n) throw new Exception(string.Format("readabs {0:X} failed", abs));
        return b;
    }
    // Wander's object (same walk as the trainer): handle at [[eboot+0x15FE570]+0x228], handle table [eboot+0x2F3B8C0];
    // the entry {object, ..., +0x18 flag, +0x20 next}; a valid player object has type 9 at +0xC; position floats at +0x130
    public const long PLAYER_HANDLE_GLOBALS = 0x15FE570, HANDLE_TABLE = 0x2F3B8C0; public const int POS = 0x130;
    public long PlayerObject()
    {
        long globals = BitConverter.ToInt64(Read(PLAYER_HANDLE_GLOBALS, 8), 0);
        if (globals == 0) return 0;
        uint handle = BitConverter.ToUInt32(ReadAbs(globals + 0x228, 4), 0);
        if (handle == 0) return 0;
        long tbl = BitConverter.ToInt64(Read(HANDLE_TABLE, 8), 0);
        long entry = tbl + (handle & 0x7fffffff);
        for (int i = 0; i < 16 && entry != 0; i++)
        {
            var e = ReadAbs(entry, 0x28);
            long o = BitConverter.ToInt64(e, 0);
            if (e[0x18] == 0 && o != 0 && (ReadAbs(o + 0xE, 1)[0] & 1) == 0) return ReadAbs(o + 0xC, 1)[0] == 9 ? o : 0;
            entry = BitConverter.ToInt64(e, 0x20);
        }
        return 0;
    }
    public float[] PlayerPos()
    {
        long o = PlayerObject(); if (o == 0) return null;
        var b = ReadAbs(o + POS, 12);
        var p = new[] { BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8) };
        if (float.IsNaN(p[0]) || float.IsNaN(p[2]) || Math.Abs(p[0]) > 1e5 || Math.Abs(p[2]) > 1e5) return null;
        return p;
    }
    public void Write(long va, byte[] b, bool code = false)
    {
        uint old = 0; IntPtr wr;
        if (code && !VirtualProtectEx(h, new IntPtr(Base + va), new IntPtr(b.Length), 0x40, out old) && !VirtualProtectEx(h, new IntPtr(Base + va), new IntPtr(b.Length), 0x80, out old))
            throw new Exception(string.Format("protect {0:X} failed ({1})", va, Marshal.GetLastWin32Error()));
        bool ok = WriteProcessMemory(h, new IntPtr(Base + va), b, new IntPtr(b.Length), out wr);
        if (code) { uint tmp; VirtualProtectEx(h, new IntPtr(Base + va), new IntPtr(b.Length), old, out tmp); }
        if (!ok) throw new Exception(string.Format("write {0:X} failed", va));
    }

    // the eboot is not at a fixed address: find the unique bytes at ANCHOR in the process memory
    public bool FindBase()
    {
        long addr = 0; MBI m; var buf = new byte[1 << 22];
        while (VirtualQueryEx(h, new IntPtr(addr), out m, new IntPtr(Marshal.SizeOf(typeof(MBI)))) != IntPtr.Zero)
        {
            long size = m.RegionSize.ToInt64();
            if (m.State == 0x1000 && (m.Protect & 0x100) == 0 && (m.Protect & 0xF0) != 0)   // committed, executable
            {
                for (long off = 0; off < size; off += buf.Length - AnchorBytes.Length)
                {
                    int len = (int)Math.Min(buf.Length, size - off); IntPtr rd;
                    if (!ReadProcessMemory(h, new IntPtr(addr + off), buf, new IntPtr(len), out rd)) break;
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

    bool Patch(long va, byte[] old, byte[] nw, string what)
    {
        var cur = Read(va, old.Length);
        if (cur.SequenceEqual(nw)) return true;
        if (!cur.SequenceEqual(old)) { Log("{0}: unexpected bytes {1}", what, BitConverter.ToString(cur)); return false; }
        Write(va, nw, true);
        return Read(va, nw.Length).SequenceEqual(nw);
    }

    // cave 1: FlagGet hook with the mailbox (flag requests executed on the game thread)
    static byte[] BuildCave()
    {
        var c = new List<byte>(); long C = CAVE;
        Action<byte[]> add = bs => c.AddRange(bs);
        add(new byte[] { 0x57 });                                                         // push rdi
        add(new byte[] { 0x8B, 0x05 }); add(Rel32(C + c.Count + 4, MAILBOX));             // mov eax,[mailbox]
        add(new byte[] { 0x85, 0xC0 });                                                   // test eax,eax
        int jzAt = c.Count; add(new byte[] { 0x74, 0x00 });                               // jz done (patched below)
        add(new byte[] { 0xC7, 0x05 }); add(Rel32(C + c.Count + 8, MAILBOX)); add(new byte[4]);   // mov dword [mailbox],0
        add(Hex("56525141504151415241534883EC08"));                                       // push rsi,rdx,rcx,r8-r11; sub rsp,8
        add(new byte[] { 0x8D, 0x78, 0xFF });                                             // lea edi,[rax-1]
        add(Hex("81E7FFFF0000"));                                                         // and edi,0xFFFF
        add(Hex("A900000200"));                                                           // test eax,0x20000 (give item)
        add(new byte[] { 0x74, 0x07 });                                                   // jz flags
        add(new byte[] { 0xE8 }); add(Rel32(C + c.Count + 4, GIVEITEM));                  // call GiveItem(edi)
        add(new byte[] { 0xEB, 0x13 });                                                   // jmp after
        add(Hex("A900000100"));                                                           // flags: test eax,0x10000
        add(new byte[] { 0x75, 0x07 });                                                   // jnz clear
        add(new byte[] { 0xE8 }); add(Rel32(C + c.Count + 4, FLAGSET));                   // call FlagSet
        add(new byte[] { 0xEB, 0x05 });                                                   // jmp after
        add(new byte[] { 0xE8 }); add(Rel32(C + c.Count + 4, FLAGCLEAR));                 // clear: call FlagClear
        add(Hex("4883C408415B415A41594158595A5E"));                                       // after: add rsp,8; pops
        c[jzAt + 1] = (byte)(c.Count - (jzAt + 2));                                       // jz -> done
        add(new byte[] { 0x5F });                                                         // done: pop rdi
        add(new byte[] { 0x89, 0xF8 });                                                   // mov eax,edi
        add(new byte[] { 0x48, 0x8D, 0x0D }); add(Rel32(C + c.Count + 4, FLAGS));         // lea rcx,[flags]
        add(new byte[] { 0xE9 }); add(Rel32(C + c.Count + 4, FLAGGET + 9));               // jmp FlagGet+9
        return c.ToArray();
    }

    // cave 2: FirstAliveBoss() -> the first entry of our ORDER whose DEAD flag is still set (alive); NULL when
    // none is left (that is what starts the ending). Falls back to the original function while ORDER_VALID == 0.
    static byte[] BuildCave2()
    {
        var c = new List<byte>(); long C = CAVE2;
        Action<byte[]> add = bs => c.AddRange(bs);
        add(new byte[] { 0x80, 0x3D }); add(Rel32(C + c.Count + 5, ORDER_VALID)); add(new byte[] { 0x00 });   // cmp byte [valid],0
        int jeAt = c.Count; add(new byte[] { 0x0F, 0x84, 0, 0, 0, 0 });                   // je orig (rel32, patched below)
        // new game? (called from the gameflow start with no current colossus) -> adopt NEXT_ORDER
        add(new byte[] { 0x50 });                                                         // push rax
        add(new byte[] { 0x48, 0x8D, 0x05 }); add(Rel32(C + c.Count + 4, GAMEFLOW_PICK_RET)); // lea rax,[gameflow return]
        add(Hex("4839442408"));                                                           // cmp [rsp+8],rax
        add(new byte[] { 0x58 });                                                         // pop rax
        int jne1 = c.Count; add(new byte[] { 0x75, 0x00 });                               // jne walk
        add(new byte[] { 0x80, 0x3D }); add(Rel32(C + c.Count + 5, NEXT_VALID)); add(new byte[] { 0x00 });    // cmp byte [next_valid],0
        int je2 = c.Count; add(new byte[] { 0x74, 0x00 });                                // je walk
        add(new byte[] { 0x50 });                                                         // push rax
        add(new byte[] { 0x48, 0x8B, 0x05 }); add(Rel32(C + c.Count + 4, NEXT_ORDER));    // mov rax,[next]
        add(new byte[] { 0x48, 0x89, 0x05 }); add(Rel32(C + c.Count + 4, ORDER));         // mov [order],rax
        add(new byte[] { 0x48, 0x8B, 0x05 }); add(Rel32(C + c.Count + 4, NEXT_ORDER + 8)); // mov rax,[next+8]
        add(new byte[] { 0x48, 0x89, 0x05 }); add(Rel32(C + c.Count + 4, ORDER + 8));     // mov [order+8],rax
        add(new byte[] { 0xC6, 0x05 }); add(Rel32(C + c.Count + 5, NEXT_VALID)); add(new byte[] { 0x00 });    // mov byte [next_valid],0
        add(new byte[] { 0xC6, 0x05 }); add(Rel32(C + c.Count + 5, NEWGAME)); add(new byte[] { 0x01 });       // mov byte [newgame],1
        add(new byte[] { 0x58 });                                                         // pop rax
        c[jne1 + 1] = (byte)(c.Count - (jne1 + 2)); c[je2 + 1] = (byte)(c.Count - (je2 + 2));   // walk:
        add(Hex("5341564157"));                                                           // push rbx; push r14; push r15
        add(new byte[] { 0x4C, 0x8D, 0x35 }); add(Rel32(C + c.Count + 4, ORDER));         // lea r14,[order]
        add(Hex("4531FF"));                                                               // xor r15d,r15d
        int loopAt = c.Count;
        add(Hex("430FB6043E"));                                                           // loop: movzx eax, byte [r14+r15]
        add(Hex("83F810"));                                                               // cmp eax,16
        int jaeAt = c.Count; add(new byte[] { 0x73, 0x00 });                              // jae next
        add(Hex("4869D8E8010000"));                                                       // imul rbx,rax,0x1E8
        add(new byte[] { 0x48, 0x8D, 0x0D }); add(Rel32(C + c.Count + 4, ENTRIES));       // lea rcx,[entries]
        add(Hex("4801CB"));                                                               // add rbx,rcx
        add(Hex("8B7B2C"));                                                               // mov edi,[rbx+0x2C]  (DEAD flag)
        add(new byte[] { 0xE8 }); add(Rel32(C + c.Count + 4, FLAGGET));                   // call FlagGet
        add(Hex("85C0"));                                                                 // test eax,eax
        int jneAt = c.Count; add(new byte[] { 0x75, 0x00 });                              // jne found
        c[jaeAt + 1] = (byte)(c.Count - (jaeAt + 2));                                     // next:
        add(Hex("49FFC7"));                                                               // inc r15
        add(Hex("4983FF10"));                                                             // cmp r15,16
        add(new byte[] { 0x72, (byte)(loopAt - (c.Count + 2)) });                         // jb loop
        add(Hex("31C0"));                                                                 // xor eax,eax (none alive)
        int jmpAt = c.Count; add(new byte[] { 0xEB, 0x00 });                              // jmp done
        c[jneAt + 1] = (byte)(c.Count - (jneAt + 2));                                     // found:
        add(Hex("4889D8"));                                                               // mov rax,rbx
        c[jmpAt + 1] = (byte)(c.Count - (jmpAt + 2));                                     // done:
        add(Hex("415F415E5BC3"));                                                         // pop r15; pop r14; pop rbx; ret
        { var rel = BitConverter.GetBytes(c.Count - (jeAt + 6)); for (int k = 0; k < 4; k++) c[jeAt + 2 + k] = rel[k]; }   // orig:
        add(FirstAliveOld);                                                               // displaced prologue
        add(new byte[] { 0xE9 }); add(Rel32(C + c.Count + 4, FIRSTALIVE + 6));            // jmp FirstAlive+6
        return c.ToArray();
    }

    // cave 3: KillBoss(entry) = "mov [LASTKILLED],rdi; mov edi,[rdi+0x2C]; jmp FlagClear" (clears the DEAD flag).
    // Every call bumps KILLCOUNT and stores the entry in KILLPTR, so a DEAD flag that flips together with the
    // counter is a real kill and one that flips alone is a loaded save. The game calls it from two places: the
    // shrine script (current colossus) and the gameflow when it loads a save taken right after a kill, where it
    // kills "current entry - 1" assuming table order. With ORDER active that call is redirected to the colossus
    // that precedes the current one in ORDER, or skipped when there is none.
    static byte[] BuildCave3()
    {
        var c = new List<byte>(); long C = CAVE3;
        Action<byte[]> add = bs => c.AddRange(bs);
        Func<byte, int> J = op => { int at = c.Count; c.Add(op); c.Add(0); return at; };
        Action<int> Fix = at => { c[at + 1] = (byte)(c.Count - (at + 2)); };
        add(new byte[] { 0x80, 0x3D }); add(Rel32(C + c.Count + 5, ORDER_VALID)); add(new byte[] { 0x00 });   // cmp byte [valid],0
        int j1 = J(0x74);                                                                 // je record
        add(new byte[] { 0x50 });                                                         // push rax
        add(new byte[] { 0x48, 0x8D, 0x05 }); add(Rel32(C + c.Count + 4, GAMEFLOW_KILL_RET)); // lea rax,[gameflow return]
        add(Hex("4839442408"));                                                           // cmp [rsp+8],rax
        add(new byte[] { 0x58 });                                                         // pop rax
        int j2 = J(0x75);                                                                 // jne record
        add(Hex("505152"));                                                               // push rax; push rcx; push rdx
        add(Hex("488D87E8010000"));                                                       // lea rax,[rdi+0x1E8]  (= current entry)
        add(new byte[] { 0x48, 0x8D, 0x0D }); add(Rel32(C + c.Count + 4, ENTRIES));       // lea rcx,[entries]
        add(Hex("4829C8"));                                                               // sub rax,rcx
        add(Hex("31D2"));                                                                 // xor edx,edx
        add(Hex("B9E8010000"));                                                           // mov ecx,0x1E8
        add(Hex("48F7F1"));                                                               // div rcx  -> rax = current slot
        add(Hex("4883F810"));                                                             // cmp rax,16
        int j3 = J(0x73);                                                                 // jae skip (no current)
        add(new byte[] { 0x48, 0x8D, 0x0D }); add(Rel32(C + c.Count + 4, ORDER));         // lea rcx,[order]
        add(Hex("31D2"));                                                                 // xor edx,edx
        int loopAt = c.Count;
        add(Hex("380411"));                                                               // loop: cmp byte [rcx+rdx],al
        int j4 = J(0x74);                                                                 // je found
        add(Hex("48FFC2"));                                                               // inc rdx
        add(Hex("4883FA10"));                                                             // cmp rdx,16
        add(new byte[] { 0x72, (byte)(loopAt - (c.Count + 2)) });                         // jb loop
        int j5 = J(0xEB);                                                                 // jmp skip (not in order)
        Fix(j4);                                                                          // found:
        add(Hex("4885D2"));                                                               // test rdx,rdx
        int j6 = J(0x74);                                                                 // jz skip (first of the order: nothing before it)
        add(Hex("0FB64411FF"));                                                           // movzx eax, byte [rcx+rdx-1]
        add(Hex("83F810"));                                                               // cmp eax,16
        int j7 = J(0x73);                                                                 // jae skip
        add(Hex("4869F8E8010000"));                                                       // imul rdi,rax,0x1E8
        add(new byte[] { 0x48, 0x8D, 0x05 }); add(Rel32(C + c.Count + 4, ENTRIES));       // lea rax,[entries]
        add(Hex("4801C7"));                                                               // add rdi,rax  -> previous colossus of the order
        add(Hex("5A5958"));                                                               // pop rdx; pop rcx; pop rax
        int j8 = J(0xEB);                                                                 // jmp record
        Fix(j3); Fix(j5); Fix(j6); Fix(j7);                                               // skip:
        add(Hex("5A5958C3"));                                                             // pops; ret (kill nothing)
        Fix(j1); Fix(j2); Fix(j8);                                                        // record:
        add(new byte[] { 0x48, 0x89, 0x3D }); add(Rel32(C + c.Count + 4, LASTKILLED));    // mov [lastkilled],rdi (displaced)
        add(new byte[] { 0x48, 0x89, 0x3D }); add(Rel32(C + c.Count + 4, KILLPTR));       // mov [killptr],rdi
        add(new byte[] { 0xFF, 0x05 }); add(Rel32(C + c.Count + 4, KILLCOUNT));           // inc dword [killcount]
        add(Hex("8B7F2C"));                                                               // mov edi,[rdi+0x2C]  (DEAD flag)
        add(new byte[] { 0xE9 }); add(Rel32(C + c.Count + 4, FLAGCLEAR));                 // jmp FlagClear
        return c.ToArray();
    }

    // cave 4: stands in for "call GetLastKilled" at the start of the shrine arrival routine (see DEMO_CALL above).
    // Caller-saved registers are free here (we sit where a function call was); rsp is 8 mod 16 on entry like in any
    // function, so one "sub rsp,8" aligns it for the call to the dead-count function.
    static byte[] BuildCave4()
    {
        var c = new List<byte>(); long C = CAVE4;
        Action<byte[]> add = bs => c.AddRange(bs);
        Func<byte, int> J = op => { int at = c.Count; c.Add(op); c.Add(0); return at; };
        Action<int> Fix = at => { c[at + 1] = (byte)(c.Count - (at + 2)); };
        add(new byte[] { 0x80, 0x3D }); add(Rel32(C + c.Count + 5, ORDER_VALID)); add(new byte[] { 0x00 });   // cmp byte [valid],0
        int j1 = J(0x74);                                                                 // je orig
        add(Hex("4883EC08"));                                                             // sub rsp,8
        add(new byte[] { 0xE8 }); add(Rel32(C + c.Count + 4, DEADCOUNT));                 // call DeadCount -> eax = k
        add(Hex("4883C408"));                                                             // add rsp,8
        add(Hex("83F801"));                                                               // cmp eax,1
        int j2 = J(0x7C);                                                                 // jl orig
        add(Hex("83F810"));                                                               // cmp eax,16
        int j3 = J(0x7F);                                                                 // jg orig
        add(Hex("FFC8"));                                                                 // dec eax
        add(Hex("4869C0E8010000"));                                                       // imul rax,rax,0x1E8
        add(new byte[] { 0x48, 0x8D, 0x0D }); add(Rel32(C + c.Count + 4, ENTRIES));       // lea rcx,[entries]
        add(Hex("4801C8"));                                                               // add rax,rcx  -> entry of the k-th campaign colossus
        add(new byte[] { 0x48, 0x89, 0x05 }); add(Rel32(C + c.Count + 4, DEMO_ENTRY));    // mov [demo_entry],rax
        add(new byte[] { 0xC3 });                                                         // ret
        Fix(j1); Fix(j2); Fix(j3);                                                        // orig:
        add(new byte[] { 0x48, 0xC7, 0x05 }); add(Rel32(C + c.Count + 8, DEMO_ENTRY)); add(new byte[4]);   // mov qword [demo_entry],0
        add(new byte[] { 0xE9 }); add(Rel32(C + c.Count + 4, LASTKILLED_GET));            // jmp GetLastKilled
        return c.ToArray();
    }

    // cave 5: sits where "call 0x7F72B0" was (a call site: rsp is 16-aligned; two pushes keep it so). r12 holds the
    // builder's context and must survive; rbx/r13 are saved. Each coin: push12(MAPSTATE, &coin) -> fruit list.
    static byte[] BuildCave5()
    {
        var c = new List<byte>(); long C = CAVE5;
        Action<byte[]> add = bs => c.AddRange(bs);
        add(Hex("53"));                                                                   // push rbx
        add(Hex("4155"));                                                                 // push r13
        add(new byte[] { 0x44, 0x8B, 0x2D }); add(Rel32(C + c.Count + 4, COIN_COUNT));    // mov r13d,[coin_count]
        add(Hex("4585ED"));                                                               // test r13d,r13d
        int jdone = c.Count; add(Hex("7400"));                                            // je done
        add(new byte[] { 0x48, 0x8D, 0x1D }); add(Rel32(C + c.Count + 4, COINS));         // lea rbx,[coins]
        int loop = c.Count;
        add(new byte[] { 0x48, 0x8D, 0x3D }); add(Rel32(C + c.Count + 4, MAPSTATE));      // lea rdi,[mapstate]
        add(Hex("4889DE"));                                                               // mov rsi,rbx
        add(new byte[] { 0xE8 }); add(Rel32(C + c.Count + 4, PUSH_FRUIT));                // call push12 (fruit list)
        add(Hex("4883C30C"));                                                             // add rbx,12
        add(Hex("41FFCD"));                                                               // dec r13d
        add(new byte[] { 0x75, (byte)(loop - (c.Count + 2)) });                           // jne loop
        c[jdone + 1] = (byte)(c.Count - (jdone + 2));                                     // done:
        add(Hex("415D"));                                                                 // pop r13
        add(Hex("5B"));                                                                   // pop rbx
        add(new byte[] { 0xE9 }); add(Rel32(C + c.Count + 4, COIN_RESUME));               // jmp lizard section
        return c.ToArray();
    }

    // the coin table read by cave 5: (x, 0, z) floats per coin; count written last so a map update never sees a
    // half-written table
    public void WriteCoins(IList<float[]> coins)
    {
        int n = Math.Min(coins.Count, (int)COIN_MAX);
        var b = new byte[n * 12];
        for (int i = 0; i < n; i++)
        {
            BitConverter.GetBytes(coins[i][0]).CopyTo(b, i * 12);
            BitConverter.GetBytes(coins[i].Length > 2 ? coins[i][1] : 0f).CopyTo(b, i * 12 + 4);
            BitConverter.GetBytes(coins[i][coins[i].Length > 2 ? 2 : 1]).CopyTo(b, i * 12 + 8);
        }
        Write(COIN_COUNT, new byte[4]);
        if (n > 0) Write(COINS, b);
        Write(COIN_COUNT, BitConverter.GetBytes(n));
    }

    public static byte[] CaveBytes() { return BuildCave(); }
    public static byte[] Cave2Bytes() { return BuildCave2(); }
    public static byte[] Cave3Bytes() { return BuildCave3(); }
    public static byte[] Cave4Bytes() { return BuildCave4(); }
    public static byte[] Cave5Bytes() { return BuildCave5(); }

    public bool Install(bool colossi, bool coins = false)
    {
        if (coins)
        {
            var cave5 = BuildCave5();
            if (CAVE5 + cave5.Length > CAVE5_END) { Log("cave 5 não cabe ({0} bytes)", cave5.Length); return false; }
            var entry5 = new byte[] { 0xE9 }.Concat(Rel32(COIN_HOOK + 5, CAVE5)).ToArray();
            if (Read(COIN_HOOK, 5).SequenceEqual(entry5) && !Read(CAVE5, cave5.Length).SequenceEqual(cave5))
            {
                Write(COIN_HOOK, CoinHookOld, true); Thread.Sleep(100);
            }
            var cur5 = Read(CAVE5, cave5.Length);
            if (!(Patch(CAVE5, cur5.SequenceEqual(cave5) ? cave5 : cur5, cave5, "cave 5") && Patch(COIN_HOOK, CoinHookOld, entry5, "gancho das moedas")
                  && (Randomizer.CoinsNeedItem || Patch(COIN_GATE, CoinGateOld, CoinGateNew, "moedas sem o item")))) return false;
            if (coins && Randomizer.CoinsNeedItem) Log("moedas: só aparecem com o mapa das árvores frutíferas no inventário (item 0 em moedas.txt desliga isso)");
        }
        bool ok = Patch(0x577955, GuardTrampOld, GuardTrampNew, "guard trampoline") && Patch(0x577960, GuardEntryOld, GuardEntryNew, "guard entry");
        var cave = BuildCave();
        var entry = new byte[] { 0xE9 }.Concat(Rel32(FLAGGET + 5, CAVE)).Concat(new byte[] { 0x90, 0x90, 0x90, 0x90 }).ToArray();
        if (Read(FLAGGET, 9).SequenceEqual(entry) && !Read(CAVE, cave.Length).SequenceEqual(cave))
        {
            // an older cave is live: unhook, let any call in flight finish, then replace it
            Write(FLAGGET, FlagGetOld, true); Thread.Sleep(100);
        }
        var curCave = Read(CAVE, cave.Length);
        ok &= Patch(CAVE, curCave.SequenceEqual(cave) ? cave : curCave, cave, "cave") && Patch(FLAGGET, FlagGetOld, entry, "FlagGet hook");
        // cave 3 (kill counter) is needed by both modes
        var cave3 = BuildCave3();
        var entry3 = new byte[] { 0xE9 }.Concat(Rel32(KILLBOSS + 5, CAVE3)).Concat(new byte[] { 0x90, 0x90 }).ToArray();
        if (Read(KILLBOSS, 7).SequenceEqual(entry3) && !Read(CAVE3, cave3.Length).SequenceEqual(cave3))
        {
            Write(KILLBOSS, KillBossOld, true); Thread.Sleep(100);
        }
        var cur3 = Read(CAVE3, cave3.Length);
        if (!colossi) Write(ORDER_VALID, new byte[] { 0 });
        ok &= Patch(CAVE3, cur3.SequenceEqual(cave3) ? cave3 : cur3, cave3, "cave 3") && Patch(KILLBOSS, KillBossOld, entry3, "KillBoss hook");
        if (colossi)
        {
            var cave2 = BuildCave2();
            var entry2 = new byte[] { 0xE9 }.Concat(Rel32(FIRSTALIVE + 5, CAVE2)).Concat(new byte[] { 0x90 }).ToArray();
            if (Read(FIRSTALIVE, 6).SequenceEqual(entry2) && !Read(CAVE2, cave2.Length).SequenceEqual(cave2))
            {
                Write(FIRSTALIVE, FirstAliveOld, true); Thread.Sleep(100);
            }
            var cur2 = Read(CAVE2, cave2.Length);
            Write(ORDER_VALID, new byte[] { 0 }); Write(NEXT_VALID, new byte[] { 0 }); Write(NEWGAME, new byte[] { 0 });
            ok &= Patch(CAVE2, cur2.SequenceEqual(cave2) ? cave2 : cur2, cave2, "cave 2") && Patch(FIRSTALIVE, FirstAliveOld, entry2, "FirstAlive hook");
            // cave 4: shrine arrival scene by kill count (the caves must stay inside the code page: cave 3 ends
            // before CAVE4, cave 4 before 0x1594000 where the next segment begins)
            var cave4 = BuildCave4();
            if (CAVE3 + cave3.Length > CAVE4 || CAVE4 + cave4.Length > 0x1594000) { Log("cave 4 não cabe ({0} bytes)", cave4.Length); return false; }
            var entry4 = new byte[] { 0xE8 }.Concat(Rel32(DEMO_CALL + 5, CAVE4)).ToArray();
            if (Read(DEMO_CALL, 5).SequenceEqual(entry4) && !Read(CAVE4, cave4.Length).SequenceEqual(cave4))
            {
                Write(DEMO_CALL, DemoCallOld, true); Thread.Sleep(100);
            }
            var cur4 = Read(CAVE4, cave4.Length);
            Write(DEMO_ENTRY, new byte[8]);
            ok &= Patch(CAVE4, cur4.SequenceEqual(cave4) ? cave4 : cur4, cave4, "cave 4") && Patch(DEMO_CALL, DemoCallOld, entry4, "arrival hook");
        }
        return ok;
    }

    public void WriteOrder(IList<int> slots)
    {
        var b = new byte[16];
        for (int i = 0; i < 16; i++) b[i] = (byte)(i < slots.Count ? slots[i] : 0xFF);
        Write(ORDER, b);
        Write(ORDER_VALID, new byte[] { 1 });
    }
    public void DisableOrder() { Write(ORDER_VALID, new byte[] { 0 }); }
    public void WriteNext(IList<int> slots)
    {
        var b = new byte[16];
        for (int i = 0; i < 16; i++) b[i] = (byte)(i < slots.Count ? slots[i] : 0xFF);
        Write(NEXT_VALID, new byte[] { 0 });
        Write(NEXT_ORDER, b);
        Write(NEXT_VALID, new byte[] { 1 });
    }
    public bool NewGameStarted() { return Read(NEWGAME, 1)[0] != 0; }
    public void AckNewGame() { Write(NEWGAME, new byte[] { 0 }); }
    public List<int> ReadOrder() { return Read(ORDER, 16).Select(x => (int)x).ToList(); }
    // drop every flag request not yet consumed (a new game or a loaded save must not receive them)
    public void ClearQueue() { queue.Clear(); if (inFlight != 0) { Write(MAILBOX, new byte[4]); inFlight = 0; } }
    public int CurrentSlot() { return BitConverter.ToInt32(Read(CURRENT_SLOT, 4), 0); }
    // v2.14: Time Attack / Reminiscence ("memória": rezar no corpo de um colosso para lutar de novo). The start routine
    // 0x7FD020 writes 1 to SPECIAL_MODE before it swaps in a fresh game state (0 dead, the chosen colossus current) and
    // copies [0x328863C] to SPECIAL_REMINISCENCE (nonzero = "ReminiscenceModeStart", zero = "TimeAttackModeStart");
    // the exit routine 0x7FD2B0 writes 0 and the real progress comes back right after. Kills in there are not kills.
    public const long SPECIAL_MODE = 0x3260A18, SPECIAL_REMINISCENCE = 0x3260A1C;
    public int SpecialMode() { var b = Read(SPECIAL_MODE, 8); if (BitConverter.ToInt32(b, 0) == 0) return 0; return BitConverter.ToInt32(b, 4) != 0 ? 2 : 1; }
    public int ReadInt32(long va) { return BitConverter.ToInt32(Read(va, 4), 0); }
    public long ReadInt64(long va) { return BitConverter.ToInt64(Read(va, 8), 0); }
    // <boss>_HOME_END flag of a campaign slot (entry+0x34; 0 for Malus, who never returns to the shrine)
    public int HomeEndFlagOfSlot(int slot) { return ReadInt32(ENTRIES + slot * ENTRY_SIZE + 0x34); }
    // entry cave 4 handed to the arrival routine (0 = none since the last reset), as a campaign slot or -1
    public int DemoSlot() { long e = ReadInt64(DEMO_ENTRY); return e == 0 ? -1 : SlotOfEntry(e); }
    public void ResetDemoEntry() { Write(DEMO_ENTRY, new byte[8]); }
    // kills registered by the game (cave 3): running count and the boss entry of the last one
    public void ReadKill(out uint count, out long entry)
    {
        var b = Read(KILLCOUNT, 16);
        count = BitConverter.ToUInt32(b, 0); entry = BitConverter.ToInt64(b, 8);
    }
    public int SlotOfEntry(long entry)
    {
        long off = entry - Base - ENTRIES;
        if (off < 0 || off % ENTRY_SIZE != 0 || off / ENTRY_SIZE > 15) return -1;
        return (int)(off / ENTRY_SIZE);
    }

    public bool GetFlag(int idx) { return (Read(FLAGS + idx / 8, 1)[0] >> (idx % 8) & 1) != 0; }
    // inventário: bit <id> em ITEMS (mesma conta de GiveItem 0x7DBF60); devolve true se precisou ligar
    public bool HasItem(int id) { return (Read(ITEMS + (id >> 3), 1)[0] & (1 << (id & 7))) != 0; }
    public byte[] ReadInv() { return Read(ITEMS, 5); }   // item bits 0..39
    public bool GiveItemDirect(int id)
    {
        long at = ITEMS + (id >> 3); byte mask = (byte)(1 << (id & 7));
        byte b = Read(at, 1)[0];
        if ((b & mask) != 0) return false;
        Write(at, new[] { (byte)(b | mask) });
        return true;
    }
    public byte[] FlagBytes() { return Read(FLAGS, 80); }

    // flag requests go through the mailbox one at a time; the game thread consumes them on its next FlagGet,
    // which can take a while (death cinematics, loading), so they are queued and pumped from the main loop
    readonly Queue<uint> queue = new Queue<uint>();
    uint inFlight; DateTime sentAt;
    public void Request(int idx, bool clear) { queue.Enqueue((uint)(idx + 1) | (clear ? 0x10000u : 0)); }
    public void RequestGive(int itemId) { queue.Enqueue((uint)(itemId + 1) | 0x20000u); }   // cave 1: call GiveItem(id)
    public int Outstanding { get { return queue.Count + (inFlight != 0 ? 1 : 0); } }
    // v2.5: while Hold is on nothing goes to the mailbox (set by Randomizer.UpdateHold during cutscenes: the two
    // v2.4 crashes came right after NEXT flags were switched through the mailbox during the death cinematic, and
    // FlagSet runs on whatever thread calls FlagGet, with the remake's world-state notifications behind it)
    public bool Hold;
    public void Pump()
    {
        if (inFlight != 0)
        {
            if (BitConverter.ToUInt32(Read(MAILBOX, 4), 0) != 0)
            {
                if (Hold)
                {
                    // not consumed yet and a cutscene began: take it back; it goes out again after the hold
                    Write(MAILBOX, new byte[4]);
                    var rest = queue.ToArray(); queue.Clear(); queue.Enqueue(inFlight); foreach (var r in rest) queue.Enqueue(r);
                    Log("pedido retirado da caixa de correio (cutscene em andamento), vai de novo depois: {0}", Describe(inFlight));
                    inFlight = 0;
                    return;
                }
                if ((DateTime.Now - sentAt).TotalSeconds > 120) { Log("pedido não atendido em 2 min; reenviando: {0}", Describe(inFlight)); Write(MAILBOX, BitConverter.GetBytes(inFlight)); sentAt = DateTime.Now; }
                return;
            }
            Log("pedido aplicado pelo jogo: {0}", Describe(inFlight));
            inFlight = 0;
        }
        if (Hold || queue.Count == 0) return;
        inFlight = queue.Dequeue();
        Write(MAILBOX, BitConverter.GetBytes(inFlight));
        sentAt = DateTime.Now;
    }
    static string Describe(uint req)
    {
        if ((req & 0x20000) != 0) return string.Format("item {0}", (req & 0xFFFF) - 1);
        return string.Format("flag {0} {1}", (req & 0xFFFF) - 1, (req & 0x10000) != 0 ? "apagada" : "ligada");
    }

    // cutscene cameras: the cameraman manager (thread "cameraman_manager_task", loop at 0x7CCAB0, per-slot update
    // 0x5902C0) keeps 10 slots of 0x138 bytes at 0x2FD8C70; slot+0 is the active camera cut (0 when free).
    // Read from outside it tells "a cutscene is playing". It is also where the v2.4 crashes happened: the slot's
    // +0xB8 object had been freed when the shrine loaded (Mutex::Lock(0x1A) at 0x295B43 via 0x60B610/0x6111C0).
    public const long CAM_SLOTS = 0x2FD8C70, CAM_SLOT_SIZE = 0x138; public const int CAM_SLOT_COUNT = 10;
    public int ActiveCuts()
    {
        var b = Read(CAM_SLOTS, (int)(CAM_SLOT_SIZE * CAM_SLOT_COUNT)); int n = 0;
        for (int i = 0; i < CAM_SLOT_COUNT; i++) if (BitConverter.ToInt64(b, (int)(i * CAM_SLOT_SIZE)) != 0) n++;
        return n;
    }
    // clears a flag bit without FlagClear (no notifications, no code run inside the game); only for flags the
    // game is not touching at that moment
    public bool SetFlagDirect(int idx)
    {
        long at = FLAGS + idx / 8; byte m = (byte)(1 << (idx % 8));
        byte b = Read(at, 1)[0];
        if ((b & m) != 0) return false;
        Write(at, new[] { (byte)(b | m) });
        return true;
    }
    public bool ClearFlagDirect(int idx)
    {
        long at = FLAGS + idx / 8; byte m = (byte)(1 << (idx % 8));
        byte b = Read(at, 1)[0];
        if ((b & m) == 0) return false;
        Write(at, new[] { (byte)(b & ~m) });
        return true;
    }

    public static void Log(string fmt, params object[] a)
    {
        var line = DateTime.Now.ToString("HH:mm:ss ") + string.Format(fmt, a);
        Console.WriteLine(line);
        try { File.AppendAllText(Randomizer.LogPath, line + "\r\n", Encoding.UTF8); } catch { }
    }
}

class State
{
    public List<int> Order = new List<int>();     // items: shuffled reward slots for this run
    public int Given;                              // how many of Order were handed out
    public List<int> Pending = new List<int>();    // slots lit in the fountain, not yet picked
    public List<int> Collected = new List<int>();
    public int DeadCount;                          // DEAD_* flags seen last time
    public List<int> COrder = new List<int>();     // colossi: boss slots (0 = Valus .. 15 = Malus) in the order to fight them
    public List<int> Kills = new List<int>();      // colossi: slots defeated so far, in order
    public List<int> CNext = new List<int>();      // colossi: order the next new game will use (cave 2 swaps it in)
    public int MapVersion;                         // 2 = fountain slots numbered as the game's HOME_ITEMn (v2.6)
    public List<int> ToGive = new List<int>();     // slots drawn at a kill, handed out after the cutscenes (v2.7)

    public static State Load(string path)
    {
        var s = new State();
        if (!File.Exists(path)) return s;
        foreach (var line in File.ReadAllLines(path))
        {
            var kv = line.Split(new[] { '=' }, 2); if (kv.Length != 2) continue;
            List<int> ints;
            try { ints = kv[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList(); } catch { continue; }
            switch (kv[0])
            {
                case "order": s.Order = ints; break;
                case "given": s.Given = ints.Count > 0 ? ints[0] : 0; break;
                case "pending": s.Pending = ints; break;
                case "collected": s.Collected = ints; break;
                case "dead": s.DeadCount = ints.Count > 0 ? ints[0] : 0; break;
                case "corder": s.COrder = ints; break;
                case "kills": s.Kills = ints; break;
                case "cnext": s.CNext = ints; break;
                case "map": s.MapVersion = ints.Count > 0 ? ints[0] : 0; break;
                case "togive": s.ToGive = ints; break;
            }
        }
        if (!ValidOrder(s.COrder)) s.COrder.Clear();
        if (!ValidOrder(s.CNext)) s.CNext.Clear();
        return s;
    }
    public static bool ValidOrder(List<int> o) { return o.Count == 16 && o.Distinct().Count() == 16 && o.All(x => x >= 0 && x <= 15); }
    public void Save(string path)
    {
        File.WriteAllText(path, string.Format("order={0}\r\ngiven={1}\r\npending={2}\r\ncollected={3}\r\ndead={4}\r\ncorder={5}\r\nkills={6}\r\ncnext={7}\r\nmap={8}\r\ntogive={9}\r\n# order/pending/collected: slots da fonte (HOME_ITEMn, 1..16); corder: ordem dos colossos desta partida; cnext: ordem da próxima partida nova (0=Valus 1=Quadratus 2=Gaius 3=Phaedra 4=Avion 5=Barba 6=Hydrus 7=Kuromori 8=Basaran 9=Dirge 10=Celosia 11=Pelagia 12=Phalanx 13=Cenobia 14=Argus 15=Malus)\r\n",
            string.Join(",", Order), Given, string.Join(",", Pending), string.Join(",", Collected), DeadCount, string.Join(",", COrder), string.Join(",", Kills), string.Join(",", CNext), MapVersion, string.Join(",", ToGive)));
    }
}

static class Randomizer
{
    // the port folder (the one with SotC.exe) and its mod_backup; set by whoever starts the loop
    public static string PortDir = @"D:\SotC-PC";
    public static bool Items = true, Colossi = false, Coins = false, Music = false, Hud = false, Watermark = false;
    public static string Dir { get { return Path.Combine(PortDir, "mod_backup"); } }
    public static string CoinsPath { get { return Path.Combine(Dir, "moedas.txt"); } }

    // mod_backup\moedas.txt: "calib ax bx az bz" (world x = ax*px + bx, z = az*py + bz) then one coin per line as
    // "px py" (pixel of the 1920x1080 reference map) or "w x z" (world coordinates). '#' starts a comment.
    static List<float[]> LoadCoins()
    {
        var coins = new List<float[]>();
        double ax = 1, bx = 0, az = 1, bz = 0;
        if (!File.Exists(CoinsPath)) { Game.Log("moedas: {0} não existe", CoinsPath); return coins; }
        foreach (var raw in File.ReadAllLines(CoinsPath))
        {
            var line = raw.Split('#')[0].Trim();
            if (line == "") continue;
            var t = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                if (t[0] == "calib" && t.Length >= 5) { ax = P(t[1]); bx = P(t[2]); az = P(t[3]); bz = P(t[4]); }
                else if (t[0] == "raio" && t.Length >= 2) COIN_RADIUS = (float)P(t[1]);          // metres around Wander
                else if (t[0] == "coleta" && t.Length >= 3) { COIN_NEAR = (float)P(t[1]); COIN_SECS = (float)P(t[2]); }
                else if (t[0] == "sinal" && t.Length >= 2) COIN_SIGN = (float)P(t[1]);           // world = sinal * list value
                else if (t[0] == "item" && t.Length >= 2) CoinsNeedItem = t[1] != "0";           // item 0 = coins without the fruit map
                else if (t[0] == "w" && t.Length >= 3) coins.Add(new[] { (float)P(t[1]), 0f, (float)P(t[2]) });
                else if (t.Length >= 2) coins.Add(new[] { (float)(ax * P(t[0]) + bx), 0f, (float)(az * P(t[1]) + bz) });
            }
            catch (Exception e) { Game.Log("moedas: linha ignorada '{0}' ({1})", line, e.Message); }
        }
        return coins;
    }
    static double P(string s) { return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

    // ---- coins that behave like an exploration map (2026-10-28): only the coins within COIN_RADIUS of Wander are
    // pushed to the map, and a coin counts as collected after Wander stands within COIN_NEAR of it for COIN_SECS
    // (the prayer at a coin takes about that long). Collected coins go to mod_backup\moedas_coletadas.txt and stay
    // hidden; delete that file to show them all again. The list values are the fruit-path values (-world x, -world z:
    // the map builder negates the fruit positions, and the coins were calibrated through that path), so world = -list.
    // COIN_NEAR 20 m: the pixel calibration of the map puts a coin ~11 m from where Wander really prays (log 2026-10-28 20:12)
    public static float COIN_RADIUS = 250f, COIN_NEAR = 20.0f, COIN_SECS = 3.0f, COIN_SIGN = -1f;
    public static bool CoinsNeedItem = true;   // the coins take the fruit-tree map's place: shown only once that item is owned ("item 0" = always)
    public static string CollectedPath { get { return Path.Combine(Dir, "moedas_coletadas.txt"); } }
    static List<float[]> allCoins = new List<float[]>();
    static HashSet<int> collectedCoins = new HashSet<int>();
    static Dictionary<int, DateTime> nearSince = new Dictionary<int, DateTime>();
    static string lastVisibleKey = null; static DateTime lastNearLog = DateTime.MinValue;
    static void LoadCollected()
    {
        collectedCoins.Clear();
        try { if (File.Exists(CollectedPath)) foreach (var t in File.ReadAllText(CollectedPath).Split(new[] { ' ', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries)) collectedCoins.Add(int.Parse(t)); }
        catch (Exception e) { Game.Log("moedas coletadas: {0}", e.Message); }
    }
    static void SaveCollected() { try { File.WriteAllText(CollectedPath, string.Join(" ", collectedCoins.OrderBy(i => i))); } catch (Exception e) { Game.Log("moedas coletadas: {0}", e.Message); } }
    static void UpdateCoins(Game g)
    {
        float[] pos = null;
        try { pos = g.PlayerPos(); } catch { }
        if (pos == null) return;   // menu / loading: keep whatever the map has
        var now = DateTime.Now; var visible = new List<float[]>(); var vis = new List<int>();
        int nearest = -1; double nearestD = 1e9;
        for (int i = 0; i < allCoins.Count; i++)
        {
            double wx = COIN_SIGN * allCoins[i][0], wz = COIN_SIGN * allCoins[i][2];
            double d = Math.Sqrt((pos[0] - wx) * (pos[0] - wx) + (pos[2] - wz) * (pos[2] - wz));
            if (collectedCoins.Contains(i)) continue;
            if (d < nearestD) { nearestD = d; nearest = i; }
            if (d <= COIN_NEAR)
            {
                if (!nearSince.ContainsKey(i)) nearSince[i] = now;
                else if ((now - nearSince[i]).TotalSeconds >= COIN_SECS)
                {
                    collectedCoins.Add(i); SaveCollected(); nearSince.Remove(i);
                    Game.Log("moeda {0} coletada (Wander ficou {1:0.0} s a {2:0.0} m dela); {3} de {4} coletadas", i + 1, COIN_SECS, d, collectedCoins.Count, allCoins.Count);
                    continue;
                }
            }
            else nearSince.Remove(i);
            if (d <= COIN_RADIUS) { visible.Add(allCoins[i]); vis.Add(i); }
        }
        if (nearest >= 0 && nearestD <= 25 && (now - lastNearLog).TotalSeconds >= 5)
        {
            lastNearLog = now; Game.Log("moeda mais próxima: {0} a {1:0.0} m (Wander em {2:0.0}, {3:0.0})", nearest + 1, nearestD, pos[0], pos[2]);
        }
        string key = string.Join(",", vis);
        if (key != lastVisibleKey)
        {
            lastVisibleKey = key; g.WriteCoins(visible);
            Game.Log("mapa: {0} moedas no raio de {1:0} m ({2} coletadas ocultas)", visible.Count, COIN_RADIUS, collectedCoins.Count);
        }
    }
    public static string LogPath { get { return Path.Combine(Dir, "randomizer.log"); } }
    public static string StatePath { get { return Path.Combine(Dir, "randomizer_state.txt"); } }
    // as 16 recompensas do Time Attack, pelo id interno do item (tabela de itens do jogo em 0x1BCA0C0 e
    // tabela de recompensas em [0x1BBBF60+0x20]); o item é dado direto com GiveItem(id)
    // slot n da fonte (HOME_ITEMn: LIGHT 164+n, START_CAM 190+n, GET_EVENT 216+n) -> id do item. Mapeamento tirado
    // de 0x5707E0, que liga o world state Collected_<Item> de cada GET_EVENT (217 WhistlingArrow, 218 CloakOfForce,
    // 219 HarpoonOfThunder, 220 MaskOfStrength, 221 SwordOfTheSun, 222 WhiteAgro, 223 FruitTreeMap, 224 MaskOfPower,
    // 225 CorruptWander, 226 ShamansCloak, 227 LizardDetectionStone, 228 ShamansMask, 229 CloakOfDeception,
    // 230 FlashArrow, 231 ClothOfDesperation, 232 QueensSword). Os ids 29/34 (Wander corrompido / Agro branco) não
    // foram confirmados no jogo. v2.5 e anteriores usavam outra ordem de slots (v2.6 migra o estado pelo id).
    // (item 29 = cavalo: o aviso do jogo disse "corcel", logo 29 = Agro branco e 34 = Wander corrompido)
    static readonly int[] RewardIds = { 0, 19, 6, 20, 2, 22, 29, 37, 3, 34, 5, 38, 1, 7, 18, 35, 23 };
    static readonly int[] OldRewardIds = { 0, 19, 7, 2, 38, 37, 3, 6, 22, 20, 18, 5, 1, 35, 23, 29, 34 };
    static readonly string[] ItemNames = {
        "", "Flecha assobiante", "Capa de energia", "Arpão do trovão", "Máscara de força", "Espada do sol",
        "Agro branco", "Mapa de árvores frutíferas", "Máscara de poder", "Wander corrompido",
        "Capa do Xamã", "Pedra de detecção de lagartos", "Máscara do Xamã", "Capa de dissimulação",
        "Flecha explosiva", "Tecido do desespero", "Espada da rainha" };
    static int SlotOfItem(int id) { return Array.IndexOf(RewardIds, id); }
    static string ItemName(int id) { int s = SlotOfItem(id); return s > 0 ? ItemNames[s] : "item " + id; }

    // what the GAME itself hands out when a lit fountain slot is picked up, learned by watching the inventory at the
    // pickup (mod_backup\fonte_slots.txt, "slot=item"; 0 = the game gives nothing there, the slot is invisible). Seeds
    // from the 2026-10-27 tests: slot 2 -> whistling arrow, 10 -> shaman's cloak, 16 -> flash arrow; 9, 11, 13 -> nothing.
    static readonly Dictionary<int, int> gameSlotItem = new Dictionary<int, int> { { 2, 19 }, { 10, 5 }, { 16, 18 }, { 3, 0 }, { 9, 0 }, { 11, 0 }, { 13, 0 } };
    static string SlotsPath { get { return Path.Combine(Dir, "fonte_slots.txt"); } }
    static void LoadSlots()
    {
        try
        {
            if (!File.Exists(SlotsPath)) return;
            foreach (var line in File.ReadAllLines(SlotsPath))
            {
                var kv = line.Split('='); int s, it;
                if (kv.Length == 2 && int.TryParse(kv[0].Trim(), out s) && int.TryParse(kv[1].Trim(), out it) && s >= 1 && s <= 16) gameSlotItem[s] = it;
            }
        }
        catch (Exception e) { Game.Log("fonte_slots: {0}", e.Message); }
    }
    static void SaveSlots()
    {
        try { File.WriteAllLines(SlotsPath, new[] { "# slot da fonte = item que o JOGO entrega ao pegar (0 = nada, slot invisível); aprendido observando o inventário" }.Concat(gameSlotItem.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)).ToArray()); }
        catch (Exception e) { Game.Log("fonte_slots: {0}", e.Message); }
    }
    // pickups being judged: inventory before, time of the pickup
    class Pickup { public int Slot; public byte[] InvBefore; public DateTime At; }
    static readonly List<Pickup> pickups = new List<Pickup>();
    static byte[] lastInv, prevInv;
    // boss slots in the game's own table order (campaign order)
    public static readonly string[] Bosses = { "Valus", "Quadratus", "Gaius", "Phaedra", "Avion", "Barba", "Hydrus", "Kuromori", "Basaran", "Dirge", "Celosia", "Pelagia", "Phalanx", "Cenobia", "Argus", "Malus" };
    // DEAD_* flag index of each slot (flag order: KNIGHT, BIRD, KAME, SNAKE_C, MAMMOTH, MINOTOUR_A, MINOTOUR_B, MINOTOUR_C, KIRIN, LEO, POSEIDON, NARGA, CERBERUS, YAMORI_B, EEL, EVIS)
    static readonly int[] DeadFlagOfSlot = { 45, 44, 40, 48, 41, 46, 54, 53, 42, 51, 49, 50, 43, 52, 47, 55 };
    static int SlotOfDeadFlag(int flag) { return Array.IndexOf(DeadFlagOfSlot, flag); }
    // G_FLAG_NEXT_<boss> (555..569): "este colosso é o próximo"; os scripts das áreas usam (o portão do Malus em F8
    // só abre com NEXT_EVIS). O jogo liga a flag pelo script do santuário seguindo a ordem da tabela
    // (HOME_END do colosso morto + 15), então com a ordem sorteada o randomizer liga a do colosso atual e apaga as
    // outras. Valus (1º) não tem NEXT.
    static readonly int[] NextFlagOfSlot = { 0, 555, 558, 556, 561, 564, 557, 560, 562, 565, 563, 566, 559, 568, 567, 569 };
    static readonly Dictionary<int, DateTime> nextRequested = new Dictionary<int, DateTime>();
    // logs every NEXT flag change, telling the game's own (shrine script) from ours
    static int lastNextMask = -1;
    static void WatchNextFlags(Func<int, bool> flag)
    {
        int mask = 0;
        for (int t = 0; t < 16; t++) if (NextFlagOfSlot[t] != 0 && flag(NextFlagOfSlot[t])) mask |= 1 << t;
        if (lastNextMask < 0) { lastNextMask = mask; return; }
        int diff = mask ^ lastNextMask; lastNextMask = mask;
        for (int t = 0; t < 16; t++)
        {
            if ((diff & (1 << t)) == 0) continue;
            DateTime at; bool ours = nextRequested.TryGetValue(NextFlagOfSlot[t], out at) && (DateTime.Now - at).TotalSeconds < 90;
            Game.Log("{0} NEXT_{1}", (ours ? "nosso pedido " : "o jogo ") + (((mask >> t) & 1) != 0 ? "ligou" : "apagou"), Bosses[t]);
        }
    }

    // v2.5: hold on mailbox requests while the game is in a cutscene. After a kill (or a loaded save / new game)
    // requests wait at least holdMin s and until no cutscene camera has been active for 3 s, at most holdMax s.
    static DateTime holdStart, holdMinUntil, holdMaxUntil, lastCutSeen;
    static bool holding, horsePending;
    // v2.12: after a kill the hold may also wait for a flag the game sets when the arrival sequence is over
    // (<boss>_HOME_END, set at the end of 0x7E78A0); then the minimum can be short and the item cutscene is not delayed
    static int holdEndFlag; static string holdEndName; static bool holdEndSeen;
    static void StartHold(string why, int minSec, int maxSec, int endFlag = 0, string endName = null)
    {
        var now = DateTime.Now;
        holdStart = now; holdMinUntil = now.AddSeconds(minSec); holdMaxUntil = now.AddSeconds(maxSec); lastCutSeen = now;
        holdEndFlag = endFlag; holdEndName = endName; holdEndSeen = false;
        if (endFlag != 0) Game.Log("pedidos de flag em espera ({0}): até o jogo ligar {1} (fim da chegada) e passarem as cutscenes, no mínimo {2} s e no máximo {3} s", why, endName, minSec, maxSec);
        else Game.Log("pedidos de flag em espera ({0}): depois das cutscenes, no mínimo {1} s e no máximo {2} s", why, minSec, maxSec);
        holding = true;
    }
    static void UpdateHold(Game g)
    {
        if (!holding) { g.Hold = false; return; }
        var now = DateTime.Now;
        int cuts = -1;
        try { cuts = g.ActiveCuts(); } catch { }
        if (cuts > 0) lastCutSeen = now;
        if (holdEndFlag != 0 && !holdEndSeen)
        {
            try { holdEndSeen = g.GetFlag(holdEndFlag); } catch { }
            if (holdEndSeen)
            {
                // the shrine streams a new area right after HOME_END ("Carregando um save ou uma área nova" ~2 s
                // later): keep the requests out of that load with a 10 s grace period
                lastCutSeen = now; if (holdMinUntil < now.AddSeconds(10)) holdMinUntil = now.AddSeconds(10);
                Game.Log("o jogo ligou {0}: sequência de chegada concluída ({1:0} s após a morte); pedidos em 10 s se não houver cutscene", holdEndName, (now - holdStart).TotalSeconds);
            }
        }
        bool calm = (now - lastCutSeen).TotalSeconds >= 3;
        bool arrived = holdEndFlag == 0 || holdEndSeen;
        if (now >= holdMinUntil && calm && arrived) { holding = false; Game.Log("sem cutscene há 3 s ({0:0} s após o início da espera): pedidos de flag liberados", (now - holdStart).TotalSeconds); }
        else if (now >= holdMaxUntil) { holding = false; Game.Log("espera máxima atingida ({0} câmera(s) de cutscene ativas{1}): pedidos de flag liberados", cuts, holdEndFlag != 0 && !holdEndSeen ? ", " + holdEndName + " nunca ligou" : ""); }
        g.Hold = holding;
    }
    // cave 4 told us which entry the arrival routine got: logged once per kill
    static bool demoLogPending;
    static void WatchDemo(Game g, int killed, int k)
    {
        if (!demoLogPending) return;
        int s; try { s = g.DemoSlot(); } catch { return; }
        if (s < 0) return;
        demoLogPending = false;
        if (s == k - 1) Game.Log("cena de chegada: a do {0}º colosso ({1}), como no jogo original para a {0}ª morte (morto de fato: {2})", k, Bosses[s], Bosses[killed]);
        else Game.Log("cena de chegada: entrada de {0} (esperava a {1}ª, {2})", Bosses[s], k, Bosses[k - 1]);
    }

    static void KeepNextFlags(Game g, int cur, Func<int, bool> flag)
    {
        if (cur < 0 || cur > 15 || g.Outstanding > 0) return;
        for (int t = 0; t < 16; t++)
        {
            int nf = NextFlagOfSlot[t]; if (nf == 0) continue;
            bool want = t == cur;
            if (flag(nf) == want) { nextRequested.Remove(nf); continue; }
            DateTime at; if (nextRequested.TryGetValue(nf, out at) && (DateTime.Now - at).TotalSeconds < 15) continue;
            nextRequested[nf] = DateTime.Now;
            g.Request(nf, !want);
            Game.Log("flag NEXT_{0} {1} (colosso atual: {2})", Bosses[t], want ? "ligada" : "apagada", Bosses[cur]);
        }
    }
    // v2.14: an item already in the inventory is never handed out again. 2026-10-28 22:14: GiveItem(19) for a
    // whistling arrow the player already had, right after an empty fountain pickup, and the game froze 10 s later.
    // A fountain slot counts as owned when its reward is owned or when the game's own item for that slot
    // (fonte_slots.txt) is owned (the game hands that one out itself at the pickup).
    static bool SlotOwned(Game g, int slot)
    {
        if (slot < 1 || slot > 16) return false;
        if (g.HasItem(RewardIds[slot])) return true;
        int known; return gameSlotItem.TryGetValue(slot, out known) && known > 0 && g.HasItem(known);
    }
    // position idx of the item order is about to be handed out: if its item is owned, swap in the first later one
    // that is not (keeps "16 items, each once" intact)
    static void SkipOwned(Game g, State st, int idx)
    {
        try
        {
            int s = st.Order[idx];
            if (!SlotOwned(g, s)) return;
            for (int k = idx + 1; k < st.Order.Count; k++)
            {
                if (SlotOwned(g, st.Order[k])) continue;
                st.Order[idx] = st.Order[k]; st.Order[k] = s;
                Game.Log("{0} já está no inventário: no lugar dele vai {1}", ItemNames[s], ItemNames[st.Order[idx]]);
                return;
            }
            Game.Log("{0} já está no inventário e não sobrou item novo para trocar", ItemNames[s]);
        }
        catch (Exception e) { Game.Log("itens: {0}", e.Message); }
    }
    // v2.14: an item the player already picked up in this run (in Collected, within the share earned by the current
    // dead count) but missing from the inventory came from a save taken before the pickup (e.g. Reminiscence exit or a
    // reload): give it again, one per 10 s check, at most once per 2 min per item, never during cutscenes and only
    // after a real game state was seen (the title screen has an empty inventory).
    static bool gameLoaded;
    static DateTime lastCatchUpCheck;
    static readonly Dictionary<int, DateTime> catchUpAt = new Dictionary<int, DateTime>();
    static void CatchUpItems(Game g, State st, int deadCount)
    {
        if (holding || g.Outstanding > 0 || deadCount < 1 || (DateTime.Now - lastCatchUpCheck).TotalSeconds < 10) return;
        lastCatchUpCheck = DateTime.Now;
        int n = Math.Min(st.Given, Math.Min(16, deadCount + 1));
        for (int i = 0; i < n && i < st.Order.Count; i++)
        {
            int s = st.Order[i];
            if (s < 1 || s > 16 || !st.Collected.Contains(s) || st.Pending.Contains(s)) continue;
            int id = RewardIds[s];
            if (g.HasItem(id)) continue;
            DateTime t; if (catchUpAt.TryGetValue(id, out t) && (DateTime.Now - t).TotalSeconds < 120) continue;
            catchUpAt[id] = DateTime.Now;
            Game.Log("{0} já foi pego nesta partida mas não está no inventário (save de antes da entrega?): GiveItem {1}", ItemNames[s], id);
            g.RequestGive(id); recentGives[id] = DateTime.Now;
            return;
        }
    }
    // itens dados há pouco: o bit é conferido de novo por um tempo (um save recarregado pode ter vindo de antes)
    static readonly Dictionary<int, DateTime> recentGives = new Dictionary<int, DateTime>();
    static void KeepItems(Game g)
    {
        if (holding || g.Outstanding > 0) return;
        foreach (var kv in recentGives.ToList())
        {
            if ((DateTime.Now - kv.Value).TotalSeconds > 180) { recentGives.Remove(kv.Key); continue; }
            if ((DateTime.Now - kv.Value).TotalSeconds < 10) continue;   // the GiveItem request may still be on its way
            try { if (!g.HasItem(kv.Key)) { Game.Log("item {0} sumiu do inventário (save recarregado?): pedindo GiveItem de novo", kv.Key); g.RequestGive(kv.Key); recentGives[kv.Key] = DateTime.Now; } } catch { }
        }
    }

    // once: quit when the game quits (the launcher shell starts the loop this way)
    public static void Loop(bool once)
    {
        Directory.CreateDirectory(Dir);
        Game.Log("randomizador v2.17 iniciado ({0}); esperando o jogo (bb-probe em {1})...", ModeName(), PortDir);
        while (true)
        {
            Process p = null;
            while ((p = Game.Find()) == null) Thread.Sleep(2000);
            try { Session(p); }
            catch (Exception e) { Game.Log("sessão encerrada: {0}", e.Message); }
            if (once) return;
            Thread.Sleep(3000);
        }
    }

    static string ModeName()
    {
        var m = new List<string>();
        if (Items) m.Add("itens"); if (Colossi) m.Add("colossos"); if (Coins) m.Add("mapa das moedas"); if (Music) m.Add("músicas do Zelda"); if (Hud) m.Add("corações do Zelda"); if (Watermark) m.Add("marca d'água");
        return string.Join(" + ", m);
    }

    // vigia de crash (crashwatch.exe ao lado deste exe): gruda no jogo como depurador e, se ele cair, grava
    // registradores e a pilha de chamadas com endereços relativos ao eboot em mod_backup\crashwatch.log
    // (o .dmp do loader não traz a pilha das threads do jogo). Termina sozinho quando o jogo fecha.
    // DESLIGADO por padrão: com o depurador grudado o loader trava as threads e o jogo fecha com erro
    // 0xff em ~20 s (2026-10-27, três vezes seguidas). Só roda se existir o arquivo mod_backup\crashwatch.on.
    static void StartCrashWatch()
    {
        try
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crashwatch.exe");
            if (!File.Exists(Path.Combine(Dir, "crashwatch.on"))) return;
            if (!File.Exists(exe) || Process.GetProcessesByName("crashwatch").Length > 0) return;
            Process.Start(new ProcessStartInfo(exe, "\"" + Dir + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            Game.Log("vigia de crash iniciado (mod_backup\\crashwatch.log)");
        }
        catch (Exception e) { Game.Log("vigia de crash: {0}", e.Message); }
    }

    static void Session(Process p)
    {
        var g = new Game(p);
        Game.Log("jogo encontrado (pid {0}); procurando o executável na memória...", p.Id);
        while (!g.FindBase()) { if (p.HasExited) return; Thread.Sleep(3000); }
        Game.Log("base {0:X}", g.Base);
        if (Watermark) { try { Marca.Begin(g, p); } catch (Exception e) { Game.Log("marca d'água: {0}", e.Message); } }
        if (!Items && !Colossi && !Coins && !Music && !Hud)
        {
            // só mods de arquivo (roupa, som): nada a instalar no jogo, só a marca d'água até o jogo fechar
            while (!p.HasExited) Thread.Sleep(1000);
            Game.Log("jogo fechado");
            Marca.End();
            return;
        }
        if (Coins)
        {
            var coins = LoadCoins();
            allCoins = coins; LoadCollected(); lastVisibleKey = null; nearSince.Clear();
            g.WriteCoins(new List<float[]>());   // nothing until Wander's position is known; UpdateCoins fills the list
            Game.Log("mapa das moedas: {0} moedas lidas (mod_backup\\moedas.txt), {1} já coletadas; só as moedas a até {2:0} m aparecem{3}",
                coins.Count, collectedCoins.Count, COIN_RADIUS, coins.Count > Game.COIN_MAX ? " — só as " + Game.COIN_MAX + " primeiras cabem" : "");
        }
        if (!g.Install(Colossi, Coins)) { Game.Log("não consegui instalar os patches; nada feito"); return; }
        Game.Log("patches instalados (proteção + gancho de flags + contador de mortes{0}{1})", Colossi ? " + ordem dos colossos + cena de chegada pela contagem de mortes" : "",
            Coins ? " + moedas no mapa do mundo (no lugar das árvores frutíferas)" : "");
        StartCrashWatch();
        if (Music) { try { ZeldaMusic.Begin(g, p); } catch (Exception e) { Game.Log("músicas: {0}", e.Message); } }
        if (Hud) { try { ZeldaHud.Begin(g, p); } catch (Exception e) { Game.Log("corações: {0}", e.Message); } }
        var st = State.Load(StatePath);
        var rnd = new Random();
        LoadSlots();
        if (st.Order.Count == 0) st.Order = Enumerable.Range(1, 16).OrderBy(x => rnd.Next()).ToList();
        if (Items) Game.Log("itens da partida em curso, na ordem da fonte ({0} já dados): {1}", st.Given, ItemOrderText(st.Order));
        if (st.MapVersion < 2)
        {
            // v2.6 numbers the fountain slots like the game (HOME_ITEMn); older state used another order: translate
            // every slot through the item id so the items already given keep their fountain slot
            Func<int, int> mv = s => { int id = s >= 1 && s <= 16 ? OldRewardIds[s] : -1; int ns = SlotOfItem(id); return ns > 0 ? ns : s; };
            st.Order = st.Order.Select(mv).ToList(); st.Pending = st.Pending.Select(mv).ToList(); st.Collected = st.Collected.Select(mv).ToList();
            if (st.Order.Count != 16 || st.Order.Distinct().Count() != 16) st.Order = Enumerable.Range(1, 16).OrderBy(x => rnd.Next()).ToList();
            st.MapVersion = 2;
            Game.Log("estado migrado para os slots da fonte do jogo (itens já dados: {0})", string.Join(", ", st.Collected.Select(s => ItemNames[s])));
        }
        if (Colossi)
        {
            if (st.COrder.Count != 16) { st.COrder = Enumerable.Range(0, 16).OrderBy(x => rnd.Next()).ToList(); st.Kills.Clear(); Game.Log("ordem dos colossos sorteada: {0}", OrderText(st)); }
            g.WriteOrder(st.COrder);
            Game.Log("ordem dos colossos em uso: {0}", OrderText(st));
            // the order of the next new game is prepared now; cave 2 swaps it in when "Novo jogo" starts
            if (st.CNext.Count != 16) st.CNext = NewOrder(rnd);
            g.WriteNext(st.CNext); g.AckNewGame();
            Game.Log("ordem da próxima partida nova: {0}", OrderText(st.CNext));
        }
        st.Save(StatePath);
        foreach (var slot in st.Pending) st.MarkLit(slot);   // give lit slots time before judging them picked up
        bool[] lastDead = null;
        int lastCurrent = -2;
        gameLoaded = false; inSpecial = false; lastSpecialSeen = DateTime.MinValue; catchUpAt.Clear();
        lastKillSlot = -1; lastKillCount = 0; demoLogPending = false;
        uint seenKills = 0;   // KILLCOUNT value whose kill has been handled
        while (!p.HasExited)
        {
            Thread.Sleep(500);
            byte[] fb; uint killCount; long killEntry;
            // flags first, then the counter: cave 3 bumps the counter before it clears the DEAD flag, so a flip
            // seen here always comes with its counter increment
            try { fb = g.FlagBytes(); g.ReadKill(out killCount, out killEntry); } catch { break; }
            try { prevInv = lastInv; lastInv = g.ReadInv(); } catch { }
            int mode = 0;
            try { mode = g.SpecialMode(); } catch { }
            if (mode != 0) { lastSpecialSeen = DateTime.Now; specialKind = mode; }
            // inside Time Attack / Reminiscence, and 8 s after it (the real progress is restored right after the exit)
            bool special = mode != 0 || (DateTime.Now - lastSpecialSeen).TotalSeconds < 8;
            UpdateHold(g);
            if (special) g.Hold = true;   // nothing goes to the mailbox: the flags in there belong to the throwaway state
            if (Coins) { try { UpdateCoins(g); } catch (Exception e) { Game.Log("moedas: {0}", e.Message); } }
            if (Colossi) WatchDemo(g, lastKillSlot, lastKillCount);
            try { g.Pump(); } catch (Exception e) { Game.Log("mailbox: {0}", e.Message); }
            if (Colossi && special)
            {
                // not seen on 2026-10-28, but if the special mode ever goes through the "first colossus of a new game"
                // path, cave 2 swapped NEXT_ORDER into ORDER: put this run's order back and re-arm the next one
                try { if (g.NewGameStarted()) { g.WriteOrder(st.COrder); g.WriteNext(st.CNext); g.AckNewGame(); Game.Log("novo jogo sinalizado dentro do {0}: ignorado, ordem da partida restaurada", SpecialName(specialKind)); } } catch (Exception e) { Game.Log("novo jogo: {0}", e.Message); }
            }
            if (Colossi && !special) { try { if (g.NewGameStarted()) { OnNewGame(g, st, rnd); StartHold("novo jogo", 20, 180); } } catch (Exception e) { Game.Log("novo jogo: {0}", e.Message); } }
            Func<int, bool> flag = i => (fb[i / 8] >> (i % 8) & 1) != 0;
            if (Colossi) WatchNextFlags(flag);
            if (horsePending && !holding)
            {
                // Malus early: the bit was cleared directly at the kill; if the game set it again, ask properly now
                horsePending = false;
                if (flag(28)) { g.Request(28, true); Game.Log("SAVE_HORSE_NO_EXIST voltou a ligar: pedindo FlagClear(28)"); }
            }
            // DEAD_<boss> bit: 1 while the colossus is alive, 0 once defeated
            var dead = Enumerable.Range(40, 16).Select(i => !flag(i)).ToArray();
            int deadCount = dead.Count(d => d);
            var deadSlots = Enumerable.Range(0, 16).Where(s => !flag(DeadFlagOfSlot[s])).ToList();
            int cur = -2;
            try { cur = g.CurrentSlot(); } catch { }
            if (Music) { try { ZeldaMusic.Tick(g, flag, special, holding); } catch (Exception e) { Game.Log("músicas: {0}", e.Message); } }
            if (lastDead == null)
            {
                // the title screen shows every colossus alive; the real state arrives when a save loads
                lastDead = dead; lastCurrent = cur; seenKills = killCount;
                Game.Log("colossos mortos agora: {0}; itens já dados: {1}; pendentes: {2}; colosso atual: {3}", deadCount, st.Given, string.Join(",", st.Pending), SlotName(cur));
                StartHold("início da sessão", 20, 180);   // title screen / first load: cutscenes may be playing
                continue;
            }
            if (special)
            {
                // 2026-10-28 22:08: a Reminiscence fight (Valus) put the game in a fresh state (0 dead); its kill looked
                // like "the first kill of a new run": the randomizer reshuffled items and colossi, lit 2 fountain slots
                // and later gave an item the player already had (the game froze right after). Now: just follow along.
                if (!inSpecial)
                {
                    inSpecial = true;
                    Game.Log("{0} começou (colossos mortos lá dentro: {1}): o randomizer fica parado até a luta acabar; nada muda na partida", SpecialName(specialKind), deadCount);
                    pickups.Clear(); litSeen.Clear();
                }
                lastDead = dead; seenKills = killCount; lastCurrent = cur;
                continue;
            }
            if (inSpecial)
            {
                inSpecial = false; gameLoaded = true;
                lastDead = dead; st.DeadCount = deadCount; seenKills = killCount; lastCurrent = cur;
                relit.Clear(); litSeen.Clear();
                Game.Log("{0} terminou: partida de volta com {1} colossos mortos, colosso atual {2}", SpecialName(specialKind), deadCount, SlotName(cur));
                StartHold("fim do " + SpecialName(specialKind), 20, 180);
                continue;
            }
            if (cur != lastCurrent)
            {
                lastCurrent = cur;
                Game.Log("colosso atual do jogo: {0}", SlotName(cur));
            }
            // (EnsureLit is no longer called: the game turns the slot's LIGHT off once the item is presented, and
            // relighting it put a phantom third item in the fountain on the 2026-10-27 14:03 test)
            if (Items) { try { Deliver(g, st); KeepItems(g); if (gameLoaded) CatchUpItems(g, st, deadCount); } catch (Exception e) { Game.Log("itens: {0}", e.Message); } }
            int changed = Enumerable.Range(0, 16).Count(i => dead[i] != lastDead[i]);
            if (changed > 1) gameLoaded = true;
            if (changed > 1)
            {
                // several bits at once: a save being loaded or a level reset, not a kill
                lastDead = dead; st.DeadCount = deadCount; st.Save(StatePath); seenKills = killCount;
                g.ClearQueue();   // item requests from the previous game state must not land in this one
                Game.Log("save carregado: {0} colossos mortos", deadCount);
                relit.Clear(); litSeen.Clear();
                StartHold("save carregado", 20, 180);
                continue;
            }
            for (int i = 0; i < 16; i++)
            {
                if (dead[i] && !lastDead[i])
                {
                    int slot = SlotOfDeadFlag(40 + i);
                    // a real kill goes through the game's KillBoss (cave 3 counts it and records the entry);
                    // a loaded save with one more kill flips the same bit without touching the counter
                    // (a bit flipping within a minute of a Time Attack / Reminiscence is its state being restored)
                    if (killCount == seenKills || g.SlotOfEntry(killEntry) != slot || (DateTime.Now - lastSpecialSeen).TotalSeconds < 60)
                    {
                        gameLoaded = true;
                        Game.Log("save carregado ({0} morto, atual {1})", Bosses[slot], SlotName(cur));
                        relit.Clear(); litSeen.Clear();
                        StartHold("save carregado", 20, 180);
                        continue;
                    }
                    seenKills = killCount; gameLoaded = true;
                    Game.Log("colosso derrotado: {0} ({1}º da campanha); mortos: {2}/16", Bosses[slot], slot + 1, deadCount);
                    if (Music) ZeldaMusic.OnKill(deadCount);
                    // death cinematic + return to the shrine + arrival cutscene: no flag requests until it is over.
                    // The arrival routine ends by setting <boss>_HOME_END of the entry it used: the k-th campaign
                    // colossus with cave 4 (colossi mode), the colossus itself otherwise. When that flag is known
                    // and still clear, the hold waits for it instead of a fixed 60 s (Malus / 16th kill have none).
                    lastKillSlot = slot; lastKillCount = deadCount;
                    int arrivalEntry = Colossi ? deadCount - 1 : slot, homeEnd = 0;
                    try { homeEnd = g.HomeEndFlagOfSlot(arrivalEntry); } catch (Exception e) { Game.Log("HOME_END: {0}", e.Message); }
                    if (homeEnd >= 540 && homeEnd <= 554 && !flag(homeEnd)) StartHold("colosso derrotado", 20, 300, homeEnd, Bosses[arrivalEntry] + "_HOME_END (flag " + homeEnd + ")");
                    else StartHold("colosso derrotado", 60, 300);
                    if (Colossi) { try { g.ResetDemoEntry(); demoLogPending = true; } catch { } }
                    if (deadCount == 1)
                    {
                        // first kill of a run: a previous run's progress means this is a new game
                        if (Items && st.Given > 0) NewItemRun(g, st, rnd, "primeiro colosso de uma partida nova");
                        if (Colossi && st.Kills.Count > 0) NewColossusRun(st, rnd, slot, "primeiro colosso de uma partida nova");
                    }
                    if (Colossi) OnKill(g, st, slot, deadCount, deadSlots);
                    // 16 items over 15 useful kills: the first colossus gives two, the 2nd..15th one each, the
                    // 16th none (there is no gameplay after it). Catches up if an earlier award was missed.
                    if (Items)
                    {
                        int target = Math.Min(16, deadCount + 1);
                        var slots = new List<int>();
                        while (st.Given < target && st.Given < st.Order.Count) { SkipOwned(g, st, st.Given); slots.Add(st.Order[st.Given]); st.Given++; }
                        if (slots.Count > 0) Award(g, st, slots);
                    }
                }
            }
            lastDead = dead; st.DeadCount = deadCount;
            if (Colossi && !holding) { try { KeepNextFlags(g, cur, flag); } catch (Exception e) { Game.Log("flags NEXT: {0}", e.Message); } }
            if (killCount != seenKills)
            {
                // KillBoss ran but no flag flipped: the colossus was already dead (the game re-registers the last
                // kill when a save taken right after it is loaded). A flip still in flight is kept for next tick.
                int ks = g.SlotOfEntry(killEntry);
                if (ks < 0 || deadSlots.Contains(ks))
                {
                    seenKills = killCount;
                    Game.Log("o jogo registrou de novo a morte de {0} (save recarregado)", ks < 0 ? "?" : Bosses[ks]);
                }
            }
            // a lit slot whose LIGHT went out (after having been seen on) was picked up: give the item now, drop its
            // START_CAM so the camera does not replay
            foreach (var slot in st.Pending.ToList())
            {
                if (slot < 1 || slot > 16) { st.Pending.Remove(slot); continue; }
                if (flag(164 + slot)) { litSeen.Add(slot); continue; }
                if (!litSeen.Contains(slot))
                {
                    // never seen lit this session: the request may have been lost (reload, crash); ask again, once per 2 min
                    if (relightAllowed.Contains(slot) && !holding && g.Outstanding == 0 && !st.JustLit(slot)) { Game.Log("slot {0} da fonte ({1}) ainda apagado: pedindo a luz de novo", slot, ItemNames[slot]); LightSlot(g, st, slot); }
                    continue;
                }
                litSeen.Remove(slot);
                Game.Log("slot {0} da fonte apagou (pego); conferindo o que o jogo entregou", slot);
                if (Music) ZeldaMusic.OnFountain(g);
                pickups.Add(new Pickup { Slot = slot, InvBefore = prevInv ?? lastInv, At = DateTime.Now });
                g.Request(190 + slot, true);
                st.Pending.Remove(slot);
            }
            // 2.5 s after a pickup: did the game hand something out? Then that is the slot's item (learned); if not,
            // the slot is invisible for the game and the randomizer gives the drawn item itself
            foreach (var pk in pickups.ToList())
            {
                if ((DateTime.Now - pk.At).TotalSeconds < 2.5) continue;
                pickups.Remove(pk);
                var gained = new List<int>();
                if (pk.InvBefore != null && lastInv != null)
                    for (int id = 0; id < 40; id++) if ((lastInv[id / 8] >> (id % 8) & 1) != 0 && (pk.InvBefore[id / 8] >> (id % 8) & 1) == 0) gained.Add(id);
                int slot = pk.Slot;
                int recvSlot = slot;   // reward slot of the item the player really got (v2.14 bookkeeping)
                if (gained.Count > 0)
                {
                    gameSlotItem[slot] = gained[0]; SaveSlots();
                    Game.Log("o jogo entregou {0} no slot {1} (anotado em fonte_slots.txt); o randomizer não dá outro item", string.Join(" + ", gained.Select(ItemName)), slot);
                    Popup("Item obtido", string.Join(" + ", gained.Select(ItemName)));
                    // the game handed out another reward than the drawn one: swap the two in the item order so the
                    // drawn one can still come later and the received one is not drawn again
                    int s2 = SlotOfItem(gained[0]);
                    if (s2 > 0 && s2 != slot)
                    {
                        int pa = st.Order.IndexOf(slot), pb = st.Order.IndexOf(s2);
                        if (pa >= 0 && pb >= st.Given) { st.Order[pa] = s2; st.Order[pb] = slot; Game.Log("ordem dos itens ajustada: {0} conta como dado; {1} volta para o sorteio", ItemNames[s2], ItemNames[slot]); }
                        recvSlot = s2;
                    }
                }
                else
                {
                    if (!gameSlotItem.ContainsKey(slot)) { gameSlotItem[slot] = 0; SaveSlots(); }
                    int giveSlot = slot;
                    bool owned = true;
                    try { owned = g.HasItem(RewardIds[slot]); } catch { }
                    if (owned)
                    {
                        // the drawn item is already in the inventory: hand out one not yet given instead (swapped in the
                        // order so it is not drawn again later); never GiveItem an owned item
                        giveSlot = -1;
                        int pos = st.Order.IndexOf(slot);
                        try
                        {
                            for (int k = st.Given; k < st.Order.Count; k++)
                                if (!g.HasItem(RewardIds[st.Order[k]])) { giveSlot = st.Order[k]; if (pos >= 0) { st.Order[k] = slot; st.Order[pos] = giveSlot; } break; }
                        }
                        catch { giveSlot = -1; }
                        if (giveSlot < 0) Game.Log("o jogo não entregou nada no slot {0}; {1} já está no inventário e não sobrou item novo: nada dado", slot, ItemNames[slot]);
                        else Game.Log("o jogo não entregou nada no slot {0}; {1} já está no inventário: no lugar dele vai {2}", slot, ItemNames[slot], ItemNames[giveSlot]);
                    }
                    if (giveSlot > 0)
                    {
                        // v2.18: the item notice cutscene of GiveItem on an invisible slot ran while the fountain camera was
                        // still active (crash 2026-10-30 00:48): wait for a calm moment first (see delayedGives)
                        Game.Log("o jogo não entregou nada no slot {0} (invisível): GiveItem {1} ({2}) assim que não houver cutscene", slot, RewardIds[giveSlot], ItemNames[giveSlot]);
                        delayedGives.Add(new DelayedGive { Item = RewardIds[giveSlot], Name = ItemNames[giveSlot], At = DateTime.Now });
                        recentGives[RewardIds[giveSlot]] = DateTime.Now;
                        Popup("Item obtido", ItemNames[giveSlot]);
                        recvSlot = giveSlot;
                    }
                }
                if (!st.Collected.Contains(recvSlot)) st.Collected.Add(recvSlot);
            }
            // delayed GiveItem: no cutscene camera for 4 s and at least 6 s after the pickup (30 s limit)
            {
                int cutsNow = -1;
                try { cutsNow = g.ActiveCuts(); } catch { }
                if (cutsNow != 0) lastCutAny = DateTime.Now;
                foreach (var dg in delayedGives.ToList())
                {
                    double age = (DateTime.Now - dg.At).TotalSeconds, calmFor = (DateTime.Now - lastCutAny).TotalSeconds;
                    if (age < 6 || (calmFor < 4 && age < 30)) continue;
                    delayedGives.Remove(dg);
                    Game.Log("GiveItem {0} ({1}) após {2:0} s ({3:0} s sem cutscene)", dg.Item, dg.Name, age, calmFor);
                    g.RequestGive(dg.Item);
                }
            }
            st.Save(StatePath);
        }
        Game.Log("jogo fechado");
        if (Music) ZeldaMusic.End();
        if (Hud) ZeldaHud.End();
        if (Watermark) Marca.End();
    }

    class DelayedGive { public int Item; public string Name; public DateTime At; }
    static readonly List<DelayedGive> delayedGives = new List<DelayedGive>();
    static DateTime lastCutAny = DateTime.MinValue;
    static int lastKillSlot = -1, lastKillCount;   // last kill seen this session (for the arrival-scene log)
    // Time Attack / Reminiscence tracking (v2.14)
    static DateTime lastSpecialSeen = DateTime.MinValue;
    static int specialKind;
    static bool inSpecial;
    static string SpecialName(int kind) { return kind == 2 ? "modo Reminiscência (luta de memória)" : "modo Time Attack"; }
    static string OrderText(State st) { return OrderText(st.COrder); }
    static string ItemOrderText(IList<int> order) { return string.Join(" > ", order.Select(s => s >= 1 && s <= 16 ? ItemNames[s] : "?")); }
    static string OrderText(IList<int> order) { return string.Join(" > ", order.Select(s => s >= 0 && s < 16 ? Bosses[s] : "?")); }
    static List<int> NewOrder(Random rnd) { return Enumerable.Range(0, 16).OrderBy(x => rnd.Next()).ToList(); }

    // cave 2 moved NEXT_ORDER into ORDER because a new game started: adopt it and prepare the following one
    static void OnNewGame(Game g, State st, Random rnd)
    {
        var order = g.ReadOrder();
        if (State.ValidOrder(order)) st.COrder = order; else { st.COrder = NewOrder(rnd); g.WriteOrder(st.COrder); }
        st.Kills.Clear();
        st.CNext = NewOrder(rnd);
        g.WriteNext(st.CNext); g.AckNewGame();
        g.ClearQueue(); st.Pending.Clear();
        if (Items && st.Given > 0) NewItemRun(g, st, rnd, "novo jogo");
        st.Save(StatePath);
        Game.Log("novo jogo: ordem dos colossos desta partida: {0}", OrderText(st));
        Game.Log("ordem da próxima partida nova: {0}", OrderText(st.CNext));
    }
    static string SlotName(int slot) { return slot >= 0 && slot < 16 ? string.Format("{0} ({1}º da campanha)", Bosses[slot], slot + 1) : "nenhum (" + slot + ")"; }

    static void NewItemRun(Game g, State st, Random rnd, string why)
    {
        Game.Log("nova partida ({0}): sorteando a ordem dos itens", why);
        // fountain slots still lit from the previous run would hand out that run's items: put them out
        foreach (var slot in st.Pending.Where(s => s >= 1 && s <= 16 && litSeen.Contains(s)).ToList())
        {
            Game.Log("slot {0} da fonte ({1}) vinha da partida anterior: apagando", slot, ItemNames[slot]);
            g.Request(164 + slot, true); g.Request(190 + slot, true);
        }
        st.Order = Enumerable.Range(1, 16).OrderBy(x => rnd.Next()).ToList();
        st.Given = 0; st.Pending.Clear(); st.Collected.Clear(); st.ToGive.Clear(); litSeen.Clear(); relightAllowed.Clear();
        Game.Log("itens desta partida, na ordem em que vão para a fonte (2 na primeira morte, 1 nas seguintes): {0}", ItemOrderText(st.Order));
    }

    // Detected at the first kill of a new game: that colossus was chosen with the previous order, so the new
    // order keeps it first and shuffles the rest (the game picks the next one only when the shrine loads).
    static void NewColossusRun(State st, Random rnd, int first, string why)
    {
        var rest = Enumerable.Range(0, 16).Where(s => s != first).OrderBy(x => rnd.Next()).ToList();
        st.COrder = new List<int> { first }.Concat(rest).ToList();
        st.Kills.Clear();
        Game.Log("nova partida ({0}): ordem dos colossos sorteada: {1}", why, OrderText(st));
    }

    static void OnKill(Game g, State st, int slot, int deadCount, List<int> deadSlots)
    {
        // kills the randomizer did not see (it was not running, or an older version missed them) are caught up
        foreach (var s in deadSlots) if (s != slot && !st.Kills.Contains(s)) st.Kills.Add(s);
        if (!st.Kills.Contains(slot)) st.Kills.Add(slot);
        g.WriteOrder(st.COrder);   // the game asks for the next colossus when the shrine loads
        bool anyLeft = deadCount < 16;
        if (slot == 15 && anyLeft)
        {
            // Malus before the end: Agro was lost at the bridge; let the shrine bring the horse back. The bit is
            // cleared directly (the death cinematic has just begun, the game only touches flags 24..31 again at the
            // autosave) so it is in place when the shrine loads; FlagClear via the mailbox would wait for the hold.
            try { g.ClearFlagDirect(28); } catch (Exception e) { Game.Log("flag 28: {0}", e.Message); }   // SAVE_HORSE_NO_EXIST off
            horsePending = true;
            Game.Log("Malus derrotado antes do fim: SAVE_HORSE_NO_EXIST apagada para o Agro voltar no santuário");
        }
        Popup(string.Format("Colosso {0} de 16 derrotado", deadCount), Bosses[slot]);
        var next = st.COrder.FirstOrDefault(s => s != slot && !st.Kills.Contains(s));
        if (!anyLeft) Game.Log("todos os colossos derrotados: o final deve começar");
        else Game.Log("próximo pela ordem sorteada: {0}", SlotName(next));
    }

    // the item goes straight to the inventory: the randomizer sets the item bit itself (same bit GiveItem
    // 0x7DBF60 sets); the fountain pickup only works for rewards the Time Attack counter has unlocked, so a lit
    // fountain slot showed an invisible, unobtainable item. The bit is re-checked for 2 min (KeepItems) so the
    // post-kill autosave and any reload of it keep the item.
    // v2.7: the item is drawn at the kill but handed out only after the cutscenes (Deliver), through the game's own
    // GiveItem (0x7DBF60 via the mailbox): GiveItem = inventory bit + 0x7837F0, the remake's item notice. v2.4..v2.6
    // wrote the bit directly and every live return to the shrine crashed in the fountain presentation (cameraman
    // thread, eboot+0x295B43: the remake side never learned of the item, so the camera followed a missing object),
    // while reloading the autosave (remake state rebuilt from the bits) always worked and v2.3 (GiveItem) worked too.
    // v2.10: the slot is lit right at the kill by direct bit writes (LIGHT 164+n, START_CAM 190+n: plain flags, no
    // code runs in the game), so the shrine finds them on arrival and plays the fountain camera like after a Time
    // Attack. The item itself is given at the pickup (Pending loop). The popup names nothing: the fountain is the reveal.
    static void Award(Game g, State st, List<int> slots)
    {
        foreach (var slot in slots)
        {
            int known; string gives = gameSlotItem.TryGetValue(slot, out known) ? (known == 0 ? "o jogo não entrega nada aí, o randomizer dá" : "o jogo entrega " + ItemName(known)) : "ainda não se sabe o que o jogo entrega aí";
            Game.Log("item sorteado: {0} (slot da fonte {1}, item {2}); slot aceso; {3}", ItemNames[slot], slot, RewardIds[slot], gives);
            bool ok = false;
            try { g.SetFlagDirect(164 + slot); g.SetFlagDirect(190 + slot); ok = true; } catch (Exception e) { Game.Log("fonte slot {0}: {1}", slot, e.Message); }
            if (!ok && !st.ToGive.Contains(slot)) st.ToGive.Add(slot);   // lit later through the mailbox
            st.MarkLit(slot); relightAllowed.Add(slot);
            if (!st.Pending.Contains(slot)) st.Pending.Add(slot);
        }
        st.Save(StatePath);
        Popup(slots.Count > 1 ? "Novos itens na fonte do santuário" : "Novo item na fonte do santuário", slots.Count > 1 ? slots.Count + " itens esperam na fonte" : "1 item espera na fonte");
    }
    // slots lit in this session may be re-requested if the game never shows them on; slots inherited from the state
    // file are only watched (a stale one from an older run was relit in a new game on 2026-10-27 14:28)
    static readonly HashSet<int> relightAllowed = new HashSet<int>();
    // v2.9 ("descoberta"): once no cutscene is running the drawn slots are only LIT in the fountain (LIGHT + START_CAM
    // through the mailbox); the item itself is given when the player picks it up (see the Pending loop: the game turns
    // the slot's LIGHT off at the pickup, the randomizer then runs GiveItem). The fountain camera plays on the next
    // shrine entry (the game evaluates START_CAM when the shrine loads).
    static void Deliver(Game g, State st)
    {
        if (st.ToGive.Count == 0 || holding || g.Outstanding > 0) return;
        var slots = st.ToGive.Where(s => s >= 1 && s <= 16).ToList();
        foreach (var slot in slots)
        {
            Game.Log("acendendo o slot {0} da fonte para {1} (item {2} entra ao pegar)", slot, ItemNames[slot], RewardIds[slot]);
            LightSlot(g, st, slot); relightAllowed.Add(slot);
        }
        st.ToGive.Clear();
        st.Save(StatePath);
    }
    // LIGHT of a pending slot seen on in this session: only then can "LIGHT off" mean the item was picked up
    static readonly HashSet<int> litSeen = new HashSet<int>();

    // fountain light (LAYOUT_HOME_ITEMn_LIGHT 164+n) and camera (HOME_ITEMn_START_CAM 190+n) through FlagSet, as v2.3 did
    static void LightSlot(Game g, State st, int slot)
    {
        g.Request(164 + slot, false); g.Request(190 + slot, false);
        st.MarkLit(slot);
        if (!st.Pending.Contains(slot)) st.Pending.Add(slot);
    }
    // an item of ours already in the inventory whose fountain slot is dark (older version): light it, once per slot
    // per session (reset when a save loads), never during cutscenes
    static readonly HashSet<int> relit = new HashSet<int>();
    static void EnsureLit(Game g, State st, Func<int, bool> flag)
    {
        if (holding || g.Outstanding > 0) return;
        foreach (var slot in st.Collected.ToList())
        {
            if (slot < 1 || slot > 16 || relit.Contains(slot) || flag(164 + slot)) continue;
            relit.Add(slot);
            bool owned; try { owned = g.HasItem(RewardIds[slot]); } catch { continue; }
            if (!owned) continue;
            Game.Log("item {0} no inventário com o slot {1} da fonte apagado: acendendo", ItemNames[slot], slot);
            LightSlot(g, st, slot);
        }
    }

    static void Popup(string title, string text)
    {
        var t = new Thread(() =>
        {
            var f = new Form { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Color.FromArgb(20, 20, 24), StartPosition = FormStartPosition.Manual, Size = new Size(520, 110) };
            var scr = Screen.PrimaryScreen.Bounds;
            f.Location = new Point(scr.Width - f.Width - 40, 60);
            var l1 = new Label { Text = title, ForeColor = Color.FromArgb(200, 200, 210), Font = new Font("Segoe UI", 11), AutoSize = false, Location = new Point(20, 14), Size = new Size(480, 24) };
            var l2 = new Label { Text = text, ForeColor = Color.FromArgb(240, 220, 160), Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = false, Location = new Point(20, 42), Size = new Size(480, 50) };
            f.Controls.Add(l1); f.Controls.Add(l2);
            var timer = new System.Windows.Forms.Timer { Interval = 8000 };
            timer.Tick += (s, e) => f.Close();
            f.Load += (s, e) => timer.Start();
            Application.Run(f);
        });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
    }
}

// ---- Músicas do Zelda (v2.15): com a opção "Skins do Zelda" o lançador passa --music e o randomizer toca MP3s
// (mod_backup\musicas_zelda, configuração em musicas.txt) pelo MCI do Windows, com fade, conforme o estado do jogo:
//   batalha      flags G_FLAG_BGM_BATTLE_<boss> 381..396 / G_FLAG_BGM_ENCOUNT_<boss> 397..412 de um colosso vivo perto
//                do Wander, ou dentro da Reminiscência / Time Attack
//   carregamento da morte de um colosso até o fim da chegada no santuário (mesma espera dos pedidos de flag)
//   menu         do início da sessão até o jogo carregar um save (events.log do port: "Carregando um save") ou o Wander existir
//   jardim       bem acima do salão do santuário; templo = no salão (perto do altar da Mono ou da fonte)
//   deserto / floresta  pela célula do mapa do jogo (grade A..K x 0..8) em que o Wander está. As células vêm das listas de
//                ambiência do próprio jogo: registros ENV_<célula> no eboot (0x163D980.., nomes 0x102A683..) apontam para as
//                ambiências da célula: Desert_amb17 em E1 E6 F0 F1 F2 G1, Forest_s_amb31 / Forest_L_amb33 em E5 G2 G5 G6
//                (conferido: lagos E2/H4 = Mount_lake, mar C7/D8/H8 = Sea_gake, F8 = Evis_amb35 do Malus).
// Posição do Wander (Game.PlayerPos) -> pixel do mapa 1920x1080 com a calibração das moedas (mundo = -lista):
// px = (-x - bx)/ax, py = (-z - bz)/az. O salão e a fonte se calibram sozinhos (posição na chegada depois de um colosso e
// na hora de pegar um item da fonte), gravados em musicas_calib.txt; antes disso só valem os valores do log de 2026-10-28.
static class ZeldaMusic
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool mciGetErrorString(int err, StringBuilder buf, int len);

    class Track { public string Key, File; public int Volume; public double Level; public bool Open, Failed; public int LastVol = -1; }
    static readonly Dictionary<string, Track> tracks = new Dictionary<string, Track>();
    static readonly object gate = new object();
    static string wanted;                  // faixa que deve soar (null = silêncio)
    static Thread worker; static volatile bool running;
    static double fadeSec = 1.5, zoneStableSec = 2, battleDist = 450;
    static HashSet<string> desertCells = new HashSet<string> { "E1", "E6", "F0", "F1", "G1" };
    static HashSet<string> forestCells = new HashSet<string> { "E5", "G2", "G5", "G6" };
    static double ax = 4.5688, bx = -4325.51, az = -4.88476, bz = 2628.37;
    static double gridX0 = 297, gridW = 129.6, gridY0 = 65, gridH = 119.3;
    static double hallR = 35, fountR = 30, hallBelow = 8, hallAbove = 25, gardenAbove = 60, gardenR = 150, uncalR = 60;
    static float[] hall, fountain;                                 // calibrados (x, y, z do mundo)
    static readonly float[] defaultFountain = { -301f, 0f, -382f };   // fonte: log 2026-10-28 22:05 (item pego), sem altura
    static string Dir { get { return Path.Combine(Randomizer.Dir, "musicas_zelda"); } }
    static string ConfigPath { get { return Path.Combine(Dir, "musicas.txt"); } }
    static string CalibPath { get { return Path.Combine(Dir, "musicas_calib.txt"); } }
    static string LogPath { get { return Path.Combine(Randomizer.Dir, "musicas.log"); } }

    public static void Log(string fmt, params object[] a)
    {
        var line = DateTime.Now.ToString("HH:mm:ss ") + string.Format(fmt, a);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
    static string Mci(string cmd)
    {
        var sb = new StringBuilder(256);
        int e = mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
        if (e == 0) return null;
        var eb = new StringBuilder(256); mciGetErrorString(e, eb, eb.Capacity);
        return string.Format("erro MCI {0} ({1}) em '{2}'", e, eb, cmd);
    }

    static readonly string[] DefaultConfig = {
        "# Músicas do mod do Zelda (opção \"Skins do Zelda\" do lançador). Arquivos nesta pasta.",
        "# faixa  arquivo  volume (0 a 100)",
        "templo       templo.mp3        8",
        "carregamento carregamento.mp3  10",
        "floresta     floresta.mp3      9",
        "deserto      deserto.mp3       9",
        "menu         menu.mp3          10",
        "jardim       jardim.mp3        9",
        "batalha      batalha.mp3       12",
        "# células do mapa do jogo (colunas A..K, linhas 0..8) que contam como deserto / floresta",
        "deserto_celulas  E1 E6 F0 F1 G1",
        "floresta_celulas E5 G2 G5 G6",
        "# fade entre faixas (s); tempo numa zona nova antes de trocar (s); distância máxima da arena para a música de batalha (m)",
        "fade 1.5",
        "troca 2",
        "batalha_distancia 450",
        "# santuário (m): raio do salão em volta do altar, raio em volta da fonte, faixa de altura do salão (abaixo / acima do piso),",
        "# jardim secreto = mais de N m acima do piso do salão e a menos de R m do altar; raio usado antes da calibração",
        "salao_raio 35",
        "fonte_raio 30",
        "salao_altura 8 25",
        "jardim_zona 60 150",
        "salao_raio_sem_calibracao 60",
    };
    static void LoadConfig()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(ConfigPath)) File.WriteAllLines(ConfigPath, DefaultConfig, new UTF8Encoding(false));
        tracks.Clear();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in File.ReadAllLines(ConfigPath))
        {
            var line = raw.Split('#')[0].Trim(); if (line == "") continue;
            var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (t[0])
                {
                    case "deserto_celulas": desertCells = new HashSet<string>(t.Skip(1).Select(s => s.ToUpperInvariant())); break;
                    case "floresta_celulas": forestCells = new HashSet<string>(t.Skip(1).Select(s => s.ToUpperInvariant())); break;
                    case "fade": fadeSec = Math.Max(0.1, double.Parse(t[1], inv)); break;
                    case "troca": zoneStableSec = double.Parse(t[1], inv); break;
                    case "batalha_distancia": battleDist = double.Parse(t[1], inv); break;
                    case "salao_raio": hallR = double.Parse(t[1], inv); break;
                    case "fonte_raio": fountR = double.Parse(t[1], inv); break;
                    case "salao_altura": hallBelow = double.Parse(t[1], inv); hallAbove = double.Parse(t[2], inv); break;
                    case "jardim_zona": gardenAbove = double.Parse(t[1], inv); gardenR = double.Parse(t[2], inv); break;
                    case "salao_raio_sem_calibracao": uncalR = double.Parse(t[1], inv); break;
                    case "calib": ax = double.Parse(t[1], inv); bx = double.Parse(t[2], inv); az = double.Parse(t[3], inv); bz = double.Parse(t[4], inv); break;
                    default:
                        if (t.Length >= 3) tracks[t[0]] = new Track { Key = t[0], File = Path.Combine(Dir, t[1]), Volume = Math.Max(0, Math.Min(100, int.Parse(t[2]))) };
                        break;
                }
            }
            catch (Exception e) { Log("musicas.txt: linha ignorada '{0}' ({1})", line, e.Message); }
        }
        hall = fountain = null;
        try
        {
            if (File.Exists(CalibPath))
                foreach (var raw in File.ReadAllLines(CalibPath))
                {
                    var t = raw.Split('#')[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length < 4) continue;
                    float x, y, z;
                    if (!float.TryParse(t[1], System.Globalization.NumberStyles.Float, inv, out x) || !float.TryParse(t[2], System.Globalization.NumberStyles.Float, inv, out y) || !float.TryParse(t[3], System.Globalization.NumberStyles.Float, inv, out z)) continue;
                    var v = new[] { x, y, z };
                    if (t[0] == "salao") hall = v; else if (t[0] == "fonte") fountain = v;
                }
        }
        catch (Exception e) { Log("musicas_calib.txt: {0}", e.Message); }
        foreach (var tr in tracks.Values) if (!File.Exists(tr.File)) Log("faixa {0}: arquivo {1} não existe", tr.Key, tr.File);
        Log("faixas: {0}; deserto {1}; floresta {2}; salão {3}; fonte {4}", string.Join(", ", tracks.Values.Select(x => x.Key + " " + x.Volume + "%")),
            string.Join(" ", desertCells), string.Join(" ", forestCells), hall == null ? "não calibrado" : P3(hall), fountain == null ? "não calibrada" : P3(fountain));
    }
    static void SaveCalib()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var lines = new List<string> { "# gravado sozinho pelo randomizer (posição do Wander na chegada ao santuário / ao pegar item da fonte); apague para recalibrar" };
        if (hall != null) lines.Add(string.Format(inv, "salao {0:0.0} {1:0.0} {2:0.0}", hall[0], hall[1], hall[2]));
        if (fountain != null) lines.Add(string.Format(inv, "fonte {0:0.0} {1:0.0} {2:0.0}", fountain[0], fountain[1], fountain[2]));
        try { File.WriteAllLines(CalibPath, lines.ToArray(), new UTF8Encoding(false)); } catch (Exception e) { Log("musicas_calib.txt: {0}", e.Message); }
    }
    static string P3(float[] p) { return string.Format("({0:0.0}, {1:0.0}, {2:0.0})", p[0], p[1], p[2]); }

    static void Work()
    {
        var last = DateTime.Now;
        while (running)
        {
            Thread.Sleep(50);
            var now = DateTime.Now; double dt = (now - last).TotalSeconds; last = now;
            string w; lock (gate) w = wanted;
            foreach (var t in tracks.Values)
            {
                double target = t.Key == w && !t.Failed ? 1 : 0;
                if (t.Level < target) t.Level = Math.Min(target, t.Level + dt / fadeSec);
                else if (t.Level > target) t.Level = Math.Max(target, t.Level - dt / fadeSec);
                string a = "zm_" + t.Key;
                if (t.Level > 0 && !t.Open)
                {
                    string err = Mci("open \"" + t.File + "\" type mpegvideo alias " + a);
                    if (err == null) err = Mci("setaudio " + a + " volume to 0");
                    if (err == null) err = Mci("play " + a + " repeat");
                    if (err != null) { Log("faixa {0} não toca: {1}", t.Key, err); Mci("close " + a); t.Failed = true; t.Level = 0; continue; }
                    t.Open = true; t.LastVol = 0;
                }
                if (!t.Open) continue;
                int vol = (int)Math.Round(t.Volume * 10 * t.Level);
                if (vol != t.LastVol) { Mci("setaudio " + a + " volume to " + vol); t.LastVol = vol; }
                if (t.Level <= 0) { Mci("stop " + a); Mci("close " + a); t.Open = false; t.LastVol = -1; }
            }
        }
        foreach (var t in tracks.Values) if (t.Open) { Mci("stop zm_" + t.Key); Mci("close zm_" + t.Key); t.Open = false; t.Level = 0; }
    }

    static void Want(string key, string why)
    {
        lock (gate)
        {
            if (wanted == key) return;
            wanted = key;
        }
        Log("música: {0} ({1})", key ?? "silêncio", why);
    }

    // ---- estado da sessão
    static bool menu, fairy, fairySawHold;
    static DateTime fairySince, sessionStart, lastEventsCheck, candSince, lastPosLog;
    const string NoCand = "\u0001"; static string cand = NoCand;
    static float[][] arenas = new float[16][];
    static readonly int[] DeadFlagOfSlot = { 45, 44, 40, 48, 41, 46, 54, 53, 42, 51, 49, 50, 43, 52, 47, 55 };
    // nomes internos na ordem das flags DEAD_* (40..55) e na ordem das flags BGM (381.. / 397..)
    static readonly string[] DeadOrder = { "KNIGHT", "BIRD", "KAME", "SNAKE_C", "MAMMOTH", "MINOTOUR_A", "MINOTOUR_B", "MINOTOUR_C", "KIRIN", "LEO", "POSEIDON", "NARGA", "CERBERUS", "YAMORI_B", "EEL", "EVIS" };
    static readonly string[] BgmOrder = { "MINOTOUR_A", "MAMMOTH", "KIRIN", "EEL", "KNIGHT", "SNAKE_C", "YAMORI_B", "BIRD", "KAME", "LEO", "MINOTOUR_B", "NARGA", "POSEIDON", "CERBERUS", "MINOTOUR_C", "EVIS" };
    static ulong lastWatch = ulong.MaxValue, lastWatch2 = ulong.MaxValue;

    public static void Begin(Game g, Process p)
    {
        LoadConfig();
        try { sessionStart = p.StartTime; } catch { sessionStart = DateTime.Now; }
        menu = true; fairy = false; cand = NoCand; lastWatch = lastWatch2 = ulong.MaxValue;
        // arena de cada colosso: posição da luz da espada (entrada +8 da tabela), que está no sistema da tabela = -mundo em x e z
        for (int s = 0; s < 16; s++)
        {
            try { var b = g.Read(Game.ENTRIES + s * Game.ENTRY_SIZE + 8, 12); arenas[s] = new[] { -BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), -BitConverter.ToSingle(b, 8) }; }
            catch { arenas[s] = null; }
        }
        if (!running) { running = true; worker = new Thread(Work) { IsBackground = true, Name = "musicas" }; worker.SetApartmentState(ApartmentState.STA); worker.Start(); }   // DirectShow (mpegvideo) só abre em thread STA
        Log("sessão do jogo: músicas do Zelda ligadas");
        Want(tracks.ContainsKey("menu") ? "menu" : null, "menu do jogo");
    }
    public static void End()
    {
        Want(null, "jogo fechado");
        running = false;
        try { if (worker != null) worker.Join(3000); } catch { }
        worker = null;
    }
    public static void OnKill(int deadCount)
    {
        if (deadCount >= 16) { fairy = false; Want(null, "último colosso: o final do jogo fica com a música original"); return; }
        fairy = true; fairySawHold = false; fairySince = DateTime.Now;
        Want("carregamento", "colosso derrotado");
    }
    public static void OnFountain(Game g)
    {
        float[] p = null; try { p = g.PlayerPos(); } catch { }
        if (p == null || Horiz(p, defaultFountain) > 150) return;
        fountain = p; SaveCalib();
        Log("fonte calibrada em {0} (item pego)", P3(p));
    }

    static double SegDist(float[] p, float[] a, float[] b)
    {
        double vx = b[0] - a[0], vz = b[2] - a[2], len2 = vx * vx + vz * vz;
        double t = len2 > 0 ? ((p[0] - a[0]) * vx + (p[2] - a[2]) * vz) / len2 : 0;
        t = Math.Max(0, Math.Min(1, t));
        double dx = p[0] - (a[0] + t * vx), dz = p[2] - (a[2] + t * vz);
        return Math.Sqrt(dx * dx + dz * dz);
    }
    static double Horiz(float[] a, float[] b) { double dx = a[0] - b[0], dz = a[2] - b[2]; return Math.Sqrt(dx * dx + dz * dz); }
    public static string Cell(float x, float z)
    {
        double px = (-x - bx) / ax, py = (-z - bz) / az;
        int col = (int)Math.Floor((px - gridX0) / gridW);
        int row = py < gridY0 ? 0 : (int)Math.Floor((py - gridY0) / gridH) + 1;
        if (col < 0 || col > 10 || row > 9) return "?";
        return ((char)('A' + col)).ToString() + row;
    }
    static string Zone(float[] p, out string where)
    {
        string c = Cell(p[0], p[2]);
        where = "célula " + c;
        if (hall != null)
        {
            double dh = Horiz(p, hall), dy = p[1] - hall[1];
            where += string.Format(", {0:0} m do altar, {1:+0;-0} m de altura", dh, dy);
            if (dy > gardenAbove && dh < gardenR) return "jardim";
            // the hall runs from Mono's altar to the fountain (2026-10-28: altar (-300, 11, -278), fountain (-301, 10, -382)):
            // a strip of half-width hallR along that segment, plus the circles around both ends
            bool inHall = dh < hallR || (fountain != null && (Horiz(p, fountain) < fountR || SegDist(p, hall, fountain) < hallR));
            if (dy > -hallBelow && dy < hallAbove && inHall) return "templo";
        }
        else
        {
            var f = fountain ?? defaultFountain;
            double df = Horiz(p, f);
            where += string.Format(", {0:0} m da fonte (salão ainda não calibrado)", df);
            if (df < uncalR) return "templo";
        }
        if (desertCells.Contains(c)) return "deserto";
        if (forestCells.Contains(c)) return "floresta";
        return null;
    }
    static bool Battle(Func<int, bool> flag, float[] p, out string who)
    {
        who = null;
        for (int j = 0; j < 16; j++)
        {
            if (!flag(381 + j) && !flag(397 + j)) continue;
            int deadFlag = 40 + Array.IndexOf(DeadOrder, BgmOrder[j]);
            if (!flag(deadFlag)) continue;                       // DEAD_* = 1 enquanto vivo
            int slot = Array.IndexOf(DeadFlagOfSlot, deadFlag);
            if (p != null && slot >= 0 && arenas[slot] != null && Horiz(p, arenas[slot]) > battleDist) continue;
            who = slot >= 0 ? Randomizer.Bosses[slot] : BgmOrder[j];
            return true;
        }
        return false;
    }
    // flags de BOOT (66..81) e de música (381..428): cada mudança vai para musicas.log (para entender o que o jogo usa)
    static void WatchFlags(Func<int, bool> flag)
    {
        ulong a = 0, b = 0;
        for (int i = 0; i < 16; i++) if (flag(66 + i)) a |= 1UL << i;
        for (int i = 0; i < 48; i++) if (flag(381 + i)) b |= 1UL << i;
        if (lastWatch != ulong.MaxValue)
        {
            for (int i = 0; i < 16; i++) if (((a ^ lastWatch) >> i & 1) != 0) Log("flag BOOT {0} ({1}) {2}", 66 + i, DeadOrder[i < 16 ? i : 0], (a >> i & 1) != 0 ? "ligada" : "apagada");
            for (int i = 0; i < 48; i++) if (((b ^ lastWatch2) >> i & 1) != 0) Log("flag {0} ({1}_{2}) {3}", 381 + i, i < 16 ? "BGM_BATTLE" : i < 32 ? "BGM_ENCOUNT" : "BGM_RETRY_BOOT", BgmOrder[i % 16], (b >> i & 1) != 0 ? "ligada" : "apagada");
        }
        lastWatch = a; lastWatch2 = b;
    }

    // chamado a cada volta do laço do randomizer (500 ms)
    public static void Tick(Game g, Func<int, bool> flag, bool special, bool holding)
    {
        var now = DateTime.Now;
        try { WatchFlags(flag); } catch { }
        float[] p = null; try { p = g.PlayerPos(); } catch { }
        if (menu)
        {
            // the title screen (raven / moon) is a live 3D scene: Wander already exists there, off the map (column A), so
            // only the port's "Carregando um save" ends the menu
            if ((now - lastEventsCheck).TotalSeconds >= 2)
            {
                lastEventsCheck = now;
                try
                {
                    var ev = Path.Combine(Randomizer.PortDir, "shadow", "events.log");
                    if (File.Exists(ev) && File.GetLastWriteTime(ev) >= sessionStart.AddSeconds(-5))
                        using (var fs = new FileStream(ev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs))
                            if (sr.ReadToEnd().Contains("Carregando um save")) { menu = false; Log("o jogo começou a carregar um save: fim do menu"); }
                }
                catch { }
            }
        }
        if (fairy)
        {
            if (holding) fairySawHold = true;
            bool done = (fairySawHold && !holding) || (now - fairySince).TotalSeconds > 420;
            if (done)
            {
                fairy = false;
                if (p != null && Horiz(p, fountain ?? defaultFountain) < 200)
                {
                    hall = p; SaveCalib();
                    Log("salão calibrado em {0} (Wander na chegada ao santuário)", P3(p));
                }
                Log("chegada ao santuário concluída: fim da música de carregamento");
            }
        }
        if (p != null && (now - lastPosLog).TotalSeconds >= 10)
        {
            lastPosLog = now; string w0; string z0 = Zone(p, out w0);
            Log("Wander em {0}; {1}; zona {2}", P3(p), w0, z0 ?? "nenhuma");
        }
        string who;
        // Wander in column A of the map grid = the title scene (column A is all clouds): menu music, also after "Sair para o título"
        bool title = p != null && Cell(p[0], p[2]).StartsWith("A");
        if (title && !menu) { menu = true; Log("Wander em {0}, fora do mapa (coluna A): tela de título", P3(p)); }
        if (special && p != null) { cand = NoCand; Want("batalha", "Reminiscência / Time Attack"); return; }
        if (fairy) { cand = NoCand; Want("carregamento", "depois do colosso, até chegar no santuário"); return; }
        if (menu) { cand = NoCand; Want("menu", "menu do jogo"); return; }
        if (Battle(flag, p, out who)) { cand = NoCand; Want("batalha", "batalha: " + who); return; }
        if (p == null) return;                                   // carregamento / cutscene: fica o que está tocando
        string where; string z = Zone(p, out where);
        string current; lock (gate) current = wanted;
        if (z == current) { cand = NoCand; return; }
        if (z != cand) { cand = z; candSince = now; return; }
        if ((now - candSince).TotalSeconds >= zoneStableSec) { Want(z, where); cand = NoCand; }
    }
}

static class StateExt
{
    // slots lit in the last 3 min: their LIGHT may not be visible yet (the flag is set while far away / queued)
    static readonly Dictionary<int, DateTime> lit = new Dictionary<int, DateTime>();
    public static void MarkLit(this State s, int slot) { lit[slot] = DateTime.Now; }
    public static bool JustLit(this State s, int slot) { DateTime t; return lit.TryGetValue(slot, out t) && (DateTime.Now - t).TotalSeconds < 120; }
}
