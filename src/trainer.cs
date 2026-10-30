// SotC-Trainer.exe v2.2: trainer para o port PC de Shadow of the Colossus (bb-probe).
// NUM 1 colosso morre com 1 golpe | NUM 2 flecha mata com 1 tiro
// NUM 3 Wander mais rápido, a pé e nadando (NUM 4 menos / NUM 5 mais) | NUM 6 imortal (HP cheio + stamina infinita) | NUM 7 pulo infinito (NUM 8 menos / NUM 9 mais força)
// v2.2: o imortal também liga shadow_unlimitedGrip (a stamina não gasta); o trainer confere a cada 1,2 s se o jogo
// desfez alguma opção ligada e reaplica (registra "reaplicado" no trainer.log); conexão só vale depois do snapshot das cvars.
// NUM + entrega todos os itens do Time Attack (16: 8 do normal + 8 do difícil) | NUM 0 desliga tudo.
// Os atalhos funcionam com o jogo em foco (Num Lock ligado).
// Encontra o jogo sozinho, acha a base do eboot pelos bytes-âncora e escreve nas cvars/código em memória.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class Mem
{
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI info, IntPtr len);
    [DllImport("kernel32", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint prot, out uint old);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public uint pad0; public IntPtr RegionSize; public uint State, Protect, Type, pad1; }

    public const long ANCHOR = 0x577967;
    static readonly byte[] AnchorBytes = Hex("4889FB488B43304885C0740D4889DFFFD048C7433000000000");

    public Process Proc; IntPtr h; public long Base;

    public static byte[] Hex(string s) { var b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16); return b; }
    public static byte[] Rel32(long next, long dst) { return BitConverter.GetBytes((int)(dst - next)); }

    public static Process Find()
    {
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.ProcessName.Equals("bb-probe", StringComparison.OrdinalIgnoreCase)) return p; } catch { }
        }
        return null;
    }

    public Mem(Process p)
    {
        Proc = p;
        h = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, p.Id);
        if (h == IntPtr.Zero) throw new Exception("não consegui abrir o processo do jogo (rode como administrador)");
    }
    public void Close() { if (h != IntPtr.Zero) CloseHandle(h); h = IntPtr.Zero; }

    public byte[] Read(long va, int n)
    {
        var b = new byte[n]; IntPtr rd;
        if (!ReadProcessMemory(h, new IntPtr(Base + va), b, new IntPtr(n), out rd) || (int)rd != n) throw new Exception(string.Format("leitura em {0:X} falhou", va));
        return b;
    }
    public byte[] ReadAbs(long abs, int n)
    {
        var b = new byte[n]; IntPtr rd;
        if (!ReadProcessMemory(h, new IntPtr(abs), b, new IntPtr(n), out rd) || (int)rd != n) throw new Exception(string.Format("leitura em {0:X} falhou", abs));
        return b;
    }
    public void Write(long va, byte[] b, bool code = false) { WriteAbs(Base + va, b, code); }
    public void WriteAbs(long abs, byte[] b, bool code)
    {
        uint old = 0; IntPtr wr;
        if (code && !VirtualProtectEx(h, new IntPtr(abs), new IntPtr(b.Length), 0x40, out old) && !VirtualProtectEx(h, new IntPtr(abs), new IntPtr(b.Length), 0x80, out old))
            throw new Exception(string.Format("proteção em {0:X} falhou ({1})", abs, Marshal.GetLastWin32Error()));
        bool ok = WriteProcessMemory(h, new IntPtr(abs), b, new IntPtr(b.Length), out wr);
        if (code) { uint tmp; VirtualProtectEx(h, new IntPtr(abs), new IntPtr(b.Length), old, out tmp); }
        if (!ok) throw new Exception(string.Format("escrita em {0:X} falhou", abs));
    }

    // cvar do motor: objeto fixo no .data; +0x60 aponta para o valor atual
    public long CvarValueAddr(long obj) { return BitConverter.ToInt64(Read(obj + 0x60, 8), 0); }
    public float GetF(long obj) { return BitConverter.ToSingle(ReadAbs(CvarValueAddr(obj), 4), 0); }
    public void SetF(long obj, float v) { WriteAbs(CvarValueAddr(obj), BitConverter.GetBytes(v), false); }
    public byte GetB(long obj) { return ReadAbs(CvarValueAddr(obj), 1)[0]; }
    public void SetB(long obj, byte v) { WriteAbs(CvarValueAddr(obj), new[] { v }, false); }

    // objeto do Wander: handle em [[eboot+0x15FE570]+0x228]; a tabela de handles [eboot+0x2F3B8C0] + (handle & 0x7fffffff)
    // dá a entrada {objeto, ..., +0x18 flag, +0x20 próxima}; o objeto válido tem tipo 9 em +0xC
    public const long PLAYER_HANDLE_GLOBALS = 0x15FE570, HANDLE_TABLE = 0x2F3B8C0;
    public const int POS = 0x130, VEL = 0x150, AIRBORNE = 0x153F, GROUND_PTR = 0x1E0;
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

    public bool FindBase()
    {
        long addr = 0; MBI m; var buf = new byte[1 << 22];
        while (VirtualQueryEx(h, new IntPtr(addr), out m, new IntPtr(Marshal.SizeOf(typeof(MBI)))) != IntPtr.Zero)
        {
            long size = m.RegionSize.ToInt64();
            if (m.State == 0x1000 && (m.Protect & 0x100) == 0 && (m.Protect & 0xF0) != 0)
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
}

// um patch de código: bytes originais -> bytes novos, verificados antes de escrever
class CodePatch
{
    public long Va; public byte[] Old, New; public string Name;
    public CodePatch(long va, string oldHex, string newHex, string name) { Va = va; Old = Mem.Hex(oldHex); New = Mem.Hex(newHex); Name = name; }
    public void Apply(Mem m, bool on)
    {
        var cur = m.Read(Va, Old.Length);
        var want = on ? New : Old;
        if (cur.SequenceEqual(want)) return;
        if (!cur.SequenceEqual(on ? Old : New)) throw new Exception(Name + ": bytes inesperados em " + Va.ToString("X") + " (" + BitConverter.ToString(cur) + ")");
        m.Write(Va, want, true);
    }
}

// botão de pulo: Espaço no teclado ou Y / A do controle (XInput)
static class Input
{
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)] struct XGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] struct XState { public uint Packet; public XGamepad Pad; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern uint XInputGetState14(uint user, out XState st);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern uint XInputGetState910(uint user, out XState st);
    static bool noXInput;

    public static bool JumpPressed()
    {
        if ((GetAsyncKeyState(0x20) & 0x8000) != 0) return true;   // Espaço
        if (noXInput) return false;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                XState st;
                uint rc;
                try { rc = XInputGetState14(i, out st); } catch (DllNotFoundException) { rc = XInputGetState910(i, out st); }
                if (rc == 0 && (st.Pad.Buttons & 0x9000) != 0) return true;   // Y (0x8000) ou A (0x1000)
            }
        }
        catch { noXInput = true; }
        return false;
    }

    static bool Key(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    // direção pedida: x = -1 esquerda .. +1 direita, y = -1 trás .. +1 frente (WASD / setas / analógico esquerdo)
    public static void Move(out float x, out float y)
    {
        x = 0; y = 0;
        if (Key(0x57) || Key(0x26)) y += 1;   // W / seta cima
        if (Key(0x53) || Key(0x28)) y -= 1;   // S / seta baixo
        if (Key(0x41) || Key(0x25)) x -= 1;   // A / seta esquerda
        if (Key(0x44) || Key(0x27)) x += 1;   // D / seta direita
        if (noXInput || x != 0 || y != 0) return;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                XState st; uint rc;
                try { rc = XInputGetState14(i, out st); } catch (DllNotFoundException) { rc = XInputGetState910(i, out st); }
                if (rc != 0) continue;
                float lx = st.Pad.LX / 32767f, ly = st.Pad.LY / 32767f;
                if (Math.Abs(lx) > 0.35f || Math.Abs(ly) > 0.35f) { x = lx; y = ly; return; }
                if ((st.Pad.Buttons & 0x0001) != 0) y += 1;   // d-pad
                if ((st.Pad.Buttons & 0x0002) != 0) y -= 1;
                if ((st.Pad.Buttons & 0x0004) != 0) x -= 1;
                if ((st.Pad.Buttons & 0x0008) != 0) x += 1;
                if (x != 0 || y != 0) return;
            }
        }
        catch { noXInput = true; }
    }
}

static class Cheats
{
    // pulo infinito: a cada apertar do botão de pulo com o Wander no ar, escreve a velocidade vertical de novo.
    // Controle no ar: o jogo não deixa guiar o Wander no ar, então o trainer usa o rumo da última velocidade
    // horizontal (o objeto não tem matriz de rotação em +0x100): frente empurra no rumo, esquerda/direita viram o rumo, trás freia.
    static Thread jumpThread; static volatile bool jumpOn; public static float JumpVel = 4.5f; public static int Jumps;
    public const float AIR_SPEED = 4.5f, AIR_TURN = 3.5f;   // m/s e rad/s
    public static void MoonJump(Mem m, bool on)
    {
        jumpOn = on;
        if (!on || (jumpThread != null && jumpThread.IsAlive)) return;
        jumpThread = new Thread(() =>
        {
            bool was = false; long obj = 0; int tick = 0; float hx = 0, hz = 1; var sw = Stopwatch.StartNew(); double last = 0;
            while (jumpOn)
            {
                try
                {
                    if (tick++ % 60 == 0 || obj == 0) obj = m.PlayerObject();   // o objeto muda ao carregar save
                    double t = sw.Elapsed.TotalSeconds; float dt = (float)Math.Min(0.05, t - last); last = t;
                    bool now = Input.JumpPressed();
                    if (obj != 0)
                    {
                        bool air = m.ReadAbs(obj + Mem.AIRBORNE, 1)[0] != 0;
                        var v = m.ReadAbs(obj + Mem.VEL, 12);
                        float vx = BitConverter.ToSingle(v, 0), vz = BitConverter.ToSingle(v, 8);
                        float sp = (float)Math.Sqrt(vx * vx + vz * vz);
                        if (sp > 0.5f) { hx = vx / sp; hz = vz / sp; }   // rumo = direção em que ele se move
                        if (now && !was && air)
                        {
                            m.WriteAbs(obj + Mem.VEL + 4, BitConverter.GetBytes(JumpVel), false);
                            Jumps++;
                        }
                        if (air)
                        {
                            float ix, iy; Input.Move(out ix, out iy);
                            if (ix != 0)
                            {
                                double ang = Math.Atan2(hx, hz) + ix * AIR_TURN * dt;
                                hx = (float)Math.Sin(ang); hz = (float)Math.Cos(ang);
                            }
                            if (ix != 0 || iy != 0)
                            {
                                float s = iy >= 0 ? AIR_SPEED * Math.Max(0.6f, Math.Abs(iy) + Math.Abs(ix)) : -AIR_SPEED * 0.5f;
                                if (s > AIR_SPEED) s = AIR_SPEED;
                                var b = new byte[12];
                                BitConverter.GetBytes(hx * s).CopyTo(b, 0); BitConverter.GetBytes(hz * s).CopyTo(b, 8);
                                m.WriteAbs(obj + Mem.VEL, new[] { b[0], b[1], b[2], b[3] }, false);
                                m.WriteAbs(obj + Mem.VEL + 8, new[] { b[8], b[9], b[10], b[11] }, false);
                            }
                        }
                    }
                    was = now;
                }
                catch { obj = 0; }
                Thread.Sleep(8);
            }
        }) { IsBackground = true };
        jumpThread.Start();
    }

    // cvars (objetos no .data do eboot)
    public const long CV_INSTANT_KILL = 0x323A508;       // shadow_InstantKillPercentage (0.05): abaixo dessa fração de HP a próxima estocada mata
    public const long CV_MULT_QUEEN = 0x323A0D0, CV_MULT_SUN = 0x323A1A8, CV_MULT_LIFE = 0x323A280, CV_MULT_DORMIN = 0x323A358, CV_MULT_BOW = 0x323A430;
    public const long CV_SWIM_ENABLE = 0x324BBA8, CV_SWIM_DEBUG = 0x324BC80, CV_SWIM_CHANGE = 0x324BD58, CV_SWIM_ROOTVEL = 0x324BE30, CV_SWIM_ANIM = 0x324BF08;
    public const long CV_NO_DAMAGE = 0x3265A20;          // shadow_noDamage (bool): SetPlayerHP 0x7ECDE0 / AddPlayerHP 0x7ECE70 põem o HP no máximo (var 11) quando ligada
    public const long CV_UNLIMITED_GRIP = 0x324C408;     // shadow_unlimitedGrip (bool, registrada em 0x729720): o gasto de stamina (grip, var 12 = máximo) é pulado em 0x71E995
    // HP máximo = var 11 (100..570) e stamina máxima = var 12 (100..700) só mudam por fruta/lagarto (0x7146F9), morte de colosso (+4/+8, 0x747DCA),
    // carga de save e Time Attack; o trainer nunca mexe nelas.
    public const float KILL_MULT = 1000f;

    // o "boost de nado" só liga em duas áreas (comparação do nível em 0x713581) e cresce com o tempo;
    // os patches ligam em todo lugar e usam o valor da cvar direto como escala de velocidade.
    // Na prática a escala entra na velocidade de raiz do Wander em qualquer locomoção (andar, correr, nadar);
    // o Agro tem movimento próprio e não é afetado.
    public static readonly CodePatch[] SwimPatches = {
        new CodePatch(0x713587, "744B", "EB4B", "nado: área"),                 // je -> jmp
        new CodePatch(0x7136EE, "C5DA5908", "C5FA1008", "nado: escala 1"),     // vmulss xmm1,xmm4,[rax] -> vmovss xmm1,[rax]
        new CodePatch(0x7136FB, "C5DA5908", "C5FA1008", "nado: escala 2"),
    };

    public class Saved { public float InstantKill, Queen, Sun, Life, Dormin, Bow, SwimRootVel, SwimAnim; public byte SwimEnable, NoDamage, UnlimitedGrip; }

    public static Saved Snapshot(Mem m)
    {
        return new Saved {
            InstantKill = m.GetF(CV_INSTANT_KILL), Queen = m.GetF(CV_MULT_QUEEN), Sun = m.GetF(CV_MULT_SUN), Life = m.GetF(CV_MULT_LIFE), Dormin = m.GetF(CV_MULT_DORMIN), Bow = m.GetF(CV_MULT_BOW),
            SwimRootVel = m.GetF(CV_SWIM_ROOTVEL), SwimAnim = m.GetF(CV_SWIM_ANIM), SwimEnable = m.GetB(CV_SWIM_ENABLE), NoDamage = m.GetB(CV_NO_DAMAGE), UnlimitedGrip = m.GetB(CV_UNLIMITED_GRIP)
        };
    }

    public static void God(Mem m, Saved s, bool on)
    {
        m.SetB(CV_NO_DAMAGE, on ? (byte)1 : s.NoDamage);
        m.SetB(CV_UNLIMITED_GRIP, on ? (byte)1 : s.UnlimitedGrip);
    }

    // true se o jogo está com os valores que o cheat ligado pede (usado pela conferência periódica)
    public static bool Holds(Mem m, Cheat c, Engine e)
    {
        if (c == e.Kill) return m.GetF(CV_INSTANT_KILL) == 2f && m.GetF(CV_MULT_QUEEN) == KILL_MULT && m.GetF(CV_MULT_LIFE) == KILL_MULT;
        if (c == e.Bow) return m.GetF(CV_MULT_BOW) == KILL_MULT;
        if (c == e.God) return m.GetB(CV_NO_DAMAGE) == 1 && m.GetB(CV_UNLIMITED_GRIP) == 1;
        if (c == e.Swim)
        {
            if (m.GetB(CV_SWIM_ENABLE) != 1 || m.GetF(CV_SWIM_ROOTVEL) != (float)e.SwimFactor - 1f) return false;
            foreach (var p in SwimPatches) if (!m.Read(p.Va, p.New.Length).SequenceEqual(p.New)) return false;
            return true;
        }
        return true;   // pulo infinito é uma thread do trainer, não depende de valor no jogo
    }

    public static void OneHitKill(Mem m, Saved s, bool on)
    {
        m.SetF(CV_INSTANT_KILL, on ? 2f : s.InstantKill);
        m.SetF(CV_MULT_QUEEN, on ? KILL_MULT : s.Queen);
        m.SetF(CV_MULT_SUN, on ? KILL_MULT : s.Sun);
        m.SetF(CV_MULT_LIFE, on ? KILL_MULT : s.Life);
        m.SetF(CV_MULT_DORMIN, on ? KILL_MULT : s.Dormin);
    }
    public static void BowKill(Mem m, Saved s, bool on) { m.SetF(CV_MULT_BOW, on ? KILL_MULT : s.Bow); }

    public static void Swim(Mem m, Saved s, bool on, float factor)
    {
        if (on)
        {
            m.SetB(CV_SWIM_ENABLE, 1);
            m.SetF(CV_SWIM_ROOTVEL, factor - 1f);   // escala somada à velocidade normal (0.3 = +30% no jogo original)
            foreach (var p in SwimPatches) p.Apply(m, true);
        }
        else
        {
            foreach (var p in SwimPatches) p.Apply(m, false);
            m.SetF(CV_SWIM_ROOTVEL, s.SwimRootVel);
            m.SetB(CV_SWIM_ENABLE, s.SwimEnable);
        }
    }
}

// ======================================================================
// Itens do Time Attack: entrega os 16 prêmios da fonte (8 do normal + 8 do difícil) do mesmo jeito que o
// comando de depuração do jogo shadow_timeAttackSetRetroactiveCompletionCount (0x7FD590): para cada prêmio
// da tabela [0x1BBBF60+0x20] (17 linhas de 0x40; item normal em +0, item do difícil em +0x1C; o slot da
// fonte vem das listas 0x128C70C normal / 0x128C74C difícil) liga HOME_ITEMn_START_CAM (190+n) e
// HOME_ITEMn_GET_EVENT (216+n) por FlagSet e chama GiveItem(id). Essas funções têm de rodar na thread do
// jogo (GiveItem termina na cena "Esse é o <item>…" e FlagSet notifica o lado do remake), então o trainer
// instala uma cave própria no gancho de FlagGet (0x7DBBA0) com uma caixa de correio, igual à cave 1 do
// randomizer; se a cave 1 estiver viva, a nossa entra na frente dela e salta para ela no fim. O gancho só
// fica instalado durante a entrega e é desfeito depois (o randomizer confere os bytes de FlagGet ao instalar).
// ======================================================================
static class TaUnlock
{
    public const long FLAGGET = 0x7DBBA0, FLAGSET = 0x7DBB00, FLAGCLEAR = 0x7DBB50, GIVEITEM = 0x7DBF60, FLAGS = 0x325F3B0, ITEMS = 0x325F7A2;
    public const long CAVE1 = 0x1593980;                      // cave 1 do randomizer (gancho dele em FlagGet)
    public const long CAVE = 0x1593D00, MAILBOX = 0x3956F00;  // zona livre depois da cave 4 do randomizer (0x1593BC0+75); a página de código acaba em 0x1594000
    public const long TABLE_PTR = 0x1BBBF80, LIST_NORMAL = 0x128C70C, LIST_HARD = 0x128C74C; public const int ROWS = 17;
    public const long CAM_SLOTS = 0x2FD8C70, CAM_SLOT_SIZE = 0x138; public const int CAM_SLOT_COUNT = 10;
    static readonly byte[] FlagGetOld = Mem.Hex("89F8488D0D0738A802");   // mov eax,edi; lea rcx,[flags]
    public static volatile bool Cancel;

    static byte[] Entry(long cave) { return new byte[] { 0xE9 }.Concat(Mem.Rel32(FLAGGET + 5, cave)).Concat(new byte[] { 0x90, 0x90, 0x90, 0x90 }).ToArray(); }

    // mesma cave 1 do randomizer: pedido = flag+1 (bit 16: FlagClear; bit 17: GiveItem(id)); muda só a cauda
    static byte[] Build(bool chain)
    {
        var c = new List<byte>(); long C = CAVE;
        Action<byte[]> add = bs => c.AddRange(bs);
        add(new byte[] { 0x57 });                                                           // push rdi
        add(new byte[] { 0x8B, 0x05 }); add(Mem.Rel32(C + c.Count + 4, MAILBOX));           // mov eax,[mailbox]
        add(new byte[] { 0x85, 0xC0 });                                                     // test eax,eax
        int jz = c.Count; add(new byte[] { 0x74, 0x00 });                                   // jz done
        add(new byte[] { 0xC7, 0x05 }); add(Mem.Rel32(C + c.Count + 8, MAILBOX)); add(new byte[4]);   // mov dword [mailbox],0
        add(Mem.Hex("56525141504151415241534883EC08"));                                     // push rsi,rdx,rcx,r8-r11; sub rsp,8
        add(Mem.Hex("8D78FF81E7FFFF0000"));                                                 // lea edi,[rax-1]; and edi,0xFFFF
        add(Mem.Hex("A900000200"));                                                         // test eax,0x20000 (item)
        add(new byte[] { 0x74, 0x07 });                                                     // jz flags
        add(new byte[] { 0xE8 }); add(Mem.Rel32(C + c.Count + 4, GIVEITEM));                // call GiveItem(edi)
        add(new byte[] { 0xEB, 0x13 });                                                     // jmp after
        add(Mem.Hex("A900000100"));                                                         // flags: test eax,0x10000
        add(new byte[] { 0x75, 0x07 });                                                     // jnz clear
        add(new byte[] { 0xE8 }); add(Mem.Rel32(C + c.Count + 4, FLAGSET));                 // call FlagSet(edi)
        add(new byte[] { 0xEB, 0x05 });                                                     // jmp after
        add(new byte[] { 0xE8 }); add(Mem.Rel32(C + c.Count + 4, FLAGCLEAR));               // clear: call FlagClear(edi)
        add(Mem.Hex("4883C408415B415A41594158595A5E"));                                     // after: add rsp,8; pops
        c[jz + 1] = (byte)(c.Count - (jz + 2));
        add(new byte[] { 0x5F });                                                           // done: pop rdi
        if (chain) { add(new byte[] { 0xE9 }); add(Mem.Rel32(C + c.Count + 4, CAVE1)); }   // a cave 1 executa o prólogo original
        else
        {
            add(new byte[] { 0x89, 0xF8 });                                                 // mov eax,edi
            add(new byte[] { 0x48, 0x8D, 0x0D }); add(Mem.Rel32(C + c.Count + 4, FLAGS));   // lea rcx,[flags]
            add(new byte[] { 0xE9 }); add(Mem.Rel32(C + c.Count + 4, FLAGGET + 9));         // jmp FlagGet+9
        }
        return c.ToArray();
    }

    static byte[] prev;   // bytes de FlagGet antes do nosso gancho
    static void Hook(Mem m)
    {
        var cur = m.Read(FLAGGET, 9); var mine = Entry(CAVE);
        bool chain;
        if (cur.SequenceEqual(FlagGetOld)) chain = false;
        else if (cur.SequenceEqual(Entry(CAVE1)) && m.Read(CAVE1, 1)[0] == 0x57) chain = true;
        else if (cur.SequenceEqual(mine))
        {
            // sobrou de uma entrega interrompida: a cauda da cave diz para onde ela volta
            var withChain = Build(true);
            chain = m.Read(CAVE, withChain.Length).SequenceEqual(withChain);
            cur = chain ? Entry(CAVE1) : FlagGetOld;
        }
        else throw new Exception("FlagGet está com um gancho desconhecido (" + BitConverter.ToString(cur) + ")");
        prev = cur;
        m.Write(MAILBOX, new byte[4]);
        m.Write(CAVE, Build(chain), true);
        m.Write(FLAGGET, mine, true);
        Engine.Log("gancho do Time Attack instalado em FlagGet (" + (chain ? "na frente da cave 1 do randomizer" : "sozinho") + ")");
    }
    static void Unhook(Mem m)
    {
        if (prev == null) return;
        try { m.Write(FLAGGET, prev, true); Thread.Sleep(150); m.Write(MAILBOX, new byte[4]); }
        finally { prev = null; }
        Engine.Log("gancho do Time Attack removido");
    }

    // um pedido por vez: a thread do jogo consome a caixa na próxima chamada de FlagGet (várias por quadro)
    static void Send(Mem m, uint req, string what)
    {
        m.Write(MAILBOX, BitConverter.GetBytes(req));
        var sw = Stopwatch.StartNew();
        while (BitConverter.ToUInt32(m.Read(MAILBOX, 4), 0) != 0)
        {
            if (sw.ElapsedMilliseconds > 15000) { m.Write(MAILBOX, new byte[4]); throw new Exception("o jogo não atendeu o pedido (" + what + ") em 15 s; está em pausa ou carregando?"); }
            Thread.Sleep(4);
        }
    }

    public static int ActiveCuts(Mem m)
    {
        var b = m.Read(CAM_SLOTS, (int)(CAM_SLOT_SIZE * CAM_SLOT_COUNT)); int n = 0;
        for (int i = 0; i < CAM_SLOT_COUNT; i++) if (BitConverter.ToInt64(b, (int)(i * CAM_SLOT_SIZE)) != 0) n++;
        return n;
    }
    public static bool HasItem(Mem m, int id) { return (m.Read(ITEMS + (id >> 3), 1)[0] & (1 << (id & 7))) != 0; }

    public class Reward { public int Item, Slot; public bool Hard; }
    public static List<Reward> ReadRewards(Mem m)
    {
        long table = BitConverter.ToInt64(m.Read(TABLE_PTR, 8), 0);
        if (table == 0) throw new Exception("a tabela de prêmios ainda não foi carregada pelo jogo");
        var rows = m.ReadAbs(table, ROWS * 0x40);
        var ln = m.Read(LIST_NORMAL, ROWS * 4); var lh = m.Read(LIST_HARD, ROWS * 4);
        var list = new List<Reward>();
        for (int i = 0; i < ROWS; i++)
        {
            int n = BitConverter.ToInt32(rows, i * 0x40), h = BitConverter.ToInt32(rows, i * 0x40 + 0x1C);
            if (n > 0 && n < 40) list.Add(new Reward { Item = n, Slot = BitConverter.ToInt32(ln, i * 4), Hard = false });
            if (h > 0 && h < 40) list.Add(new Reward { Item = h, Slot = BitConverter.ToInt32(lh, i * 4), Hard = true });
        }
        // sanidade: a tabela conhecida tem 16 prêmios, entre eles a Flecha assobiante (19), e slots 1..16
        if (list.Count < 8 || list.Count > 32 || !list.Any(r => r.Item == 19) || list.Any(r => r.Slot < 1 || r.Slot > 16))
            throw new Exception("tabela de prêmios do Time Attack não reconhecida (" + list.Count + " prêmios)");
        return list;
    }

    public static string ItemName(int id)
    {
        switch (id)
        {
            case 1: return "Máscara do Xamã"; case 2: return "Máscara de Força"; case 3: return "Máscara de Poder";
            case 5: return "Capa do Xamã"; case 6: return "Capa de Força"; case 7: return "Capa de Dissimulação";
            case 18: return "Flecha de Luz"; case 19: return "Flecha Assobiante"; case 20: return "Arpão do Trovão";
            case 22: return "Espada do Sol"; case 23: return "Espada da Rainha"; case 29: return "Agro Branco";
            case 34: return "Wander Sombrio"; case 35: return "Pano do Desespero"; case 37: return "Mapa das Frutas"; case 38: return "Pedra dos Lagartos";
            default: return "item " + id;
        }
    }

    static void WaitNoCuts(Mem m, Action<string> progress)
    {
        var sw = Stopwatch.StartNew(); bool said = false;
        while (ActiveCuts(m) > 0 && !Cancel)
        {
            if (!said) { progress("Esperando a cena acabar..."); said = true; }
            if (sw.ElapsedMilliseconds > 90000) throw new Exception("uma cena não acabou em 90 s; tente de novo depois");
            Thread.Sleep(200);
        }
    }

    // devolve quantos itens novos foram entregues (os que já estavam no inventário só ganham as flags)
    public static int Run(Mem m, Action<string> progress)
    {
        if (m.PlayerObject() == 0) throw new Exception("entre no jogo primeiro (Wander não encontrado)");
        if (ActiveCuts(m) > 0) throw new Exception("espere a cena acabar e tente de novo");
        var rewards = ReadRewards(m);
        int todo = rewards.Count(r => !HasItem(m, r.Item)), given = 0;
        Engine.Log("Time Attack: " + rewards.Count + " prêmios na tabela, " + todo + " faltando no inventário");
        Hook(m);
        try
        {
            foreach (var r in rewards)
            {
                if (Cancel) break;
                WaitNoCuts(m, progress);
                bool give = !HasItem(m, r.Item);
                if (give) progress("Entregando " + ItemName(r.Item) + " (" + (given + 1) + "/" + todo + ")...");
                Send(m, (uint)(190 + r.Slot + 1), "START_CAM " + r.Slot);
                Send(m, (uint)(216 + r.Slot + 1), "GET_EVENT " + r.Slot);
                if (give) { Send(m, (uint)(r.Item + 1) | 0x20000u, "GiveItem " + r.Item); given++; }
                Engine.Log("  " + (r.Hard ? "difícil" : "normal") + " slot " + r.Slot + ": " + ItemName(r.Item) + (give ? " entregue" : " (já tinha)"));
            }
        }
        finally { Unhook(m); }
        return given;
    }
}

// ======================================================================
// Motor: liga/desliga cada cheat, guarda o estado desejado mesmo sem o jogo aberto
// ======================================================================
class Cheat
{
    public string Name, Desc; public int Key; public bool On;
    public bool IsAction;   // ação de uma vez (Time Attack): On = em andamento
    public Cheat(string name, string desc, int key) { Name = name; Desc = desc; Key = key; }
    public string KeyLabel { get { return Key == 0x6B ? "NUM +" : Key == 0x6D ? "NUM −" : "NUM " + (Key - 0x60); } }
}

class Engine
{
    public Mem M; Cheats.Saved saved; int attachedPid;
    public List<Cheat> List = new List<Cheat>();
    public Cheat Kill, Bow, Swim, God, Jump, Ta;
    public decimal SwimFactor = 3.0m, JumpForce = 4.5m;
    public const decimal SwimMin = 1.0m, SwimMax = 20m, SwimStep = 0.5m, JumpMin = 1m, JumpMax = 30m, JumpStep = 0.5m;
    static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trainer.log");
    public event Action<string, bool> Status;   // (mensagem, é erro) vindo da thread da entrega do Time Attack
    Thread taThread;

    public Engine()
    {
        List.Add(Kill = new Cheat("Colosso morre com 1 golpe", "Uma estocada mata o colosso", 0x61));
        List.Add(Bow = new Cheat("Flecha mata com 1 tiro", "Uma flecha derruba o colosso", 0x62));
        List.Add(Swim = new Cheat("Wander mais rápido", "Mais rápido a pé e nadando", 0x63));
        List.Add(Jump = new Cheat("Pulo infinito", "Pula de novo no ar (Espaço ou A)", 0x67));
        List.Add(God = new Cheat("Imortal", "HP cheio e stamina infinita", 0x66));
        List.Add(Ta = new Cheat("Itens do Time Attack", "Dá os 16 prêmios da fonte", 0x6B) { IsAction = true });
    }

    public bool Attached { get { return M != null; } }
    public static void Log(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n"); } catch { } }

    // aplica um cheat no jogo (se aberto); o estado desejado fica guardado em c.On
    public void Apply(Cheat c, bool quiet = false)
    {
        if (M == null || c.IsAction) return;
        if (c == Kill) Cheats.OneHitKill(M, saved, c.On);
        else if (c == Bow) Cheats.BowKill(M, saved, c.On);
        else if (c == Swim) Cheats.Swim(M, saved, c.On, (float)SwimFactor);
        else if (c == God) Cheats.God(M, saved, c.On);
        else if (c == Jump) { Cheats.JumpVel = (float)JumpForce; Cheats.MoonJump(M, c.On); }
        if (!quiet) Log(c.Name + (c.On ? " ligado" : " desligado"));
    }

    // conferência periódica: se o jogo desfez um cheat ligado (reset de cvar ao carregar, por exemplo), reaplica e anota no log
    public int Keep()
    {
        if (M == null) return 0;
        int fixedCount = 0;
        foreach (var c in List)
        {
            if (!c.On || c.IsAction) continue;
            try
            {
                if (Cheats.Holds(M, c, this)) continue;
                Apply(c, true); fixedCount++;
                Log(c.Name + ": o jogo tinha desfeito, reaplicado");
            }
            catch (Exception ex) { Log(c.Name + ": conferência falhou: " + ex.Message); }
        }
        return fixedCount;
    }

    public void Toggle(Cheat c, bool on)
    {
        if (c.IsAction) { if (on) StartTa(); return; }
        bool before = c.On; c.On = on;
        try { Apply(c); } catch { c.On = before; throw; }
    }

    public bool TaRunning { get { return taThread != null && taThread.IsAlive; } }
    void Report(string s, bool err) { var h = Status; if (h != null) h(s, err); }
    // entrega dos itens do Time Attack numa thread própria (espera cenas e a thread do jogo consumir os pedidos)
    void StartTa()
    {
        if (M == null) throw new Exception("abra o jogo primeiro");
        if (TaRunning) return;
        var m = M; Ta.On = true; TaUnlock.Cancel = false;
        taThread = new Thread(() =>
        {
            try
            {
                int n = TaUnlock.Run(m, s => Report(s, false));
                Log("Time Attack: " + n + " item(ns) entregue(s)");
                Report(n > 0 ? n + " item(ns) do Time Attack entregue(s)! Veja no menu de itens" : "Você já tinha todos os itens do Time Attack", false);
            }
            catch (Exception ex) { Log("Time Attack: " + ex); Report("Erro: " + ex.Message, true); }
            finally { Ta.On = false; }
        }) { IsBackground = true };
        taThread.Start();
    }

    public void SetSwim(decimal v)
    {
        SwimFactor = Clamp(v, SwimMin, SwimMax, SwimStep);
        if (Swim.On) Apply(Swim);
    }
    public void SetJump(decimal v)
    {
        JumpForce = Clamp(v, JumpMin, JumpMax, JumpStep);
        Cheats.JumpVel = (float)JumpForce;
    }
    static decimal Clamp(decimal v, decimal min, decimal max, decimal step) { v = Math.Round(v / step) * step; return Math.Max(min, Math.Min(max, v)); }

    public void DisableAll()
    {
        Exception first = null;
        foreach (var c in List) { if (!c.On || c.IsAction) continue; try { Toggle(c, false); } catch (Exception ex) { c.On = false; if (first == null) first = ex; } }
        if (first != null) throw first;
    }

    // true = conectou agora; false = nada mudou. Lança exceção em erro real.
    public bool TryAttach()
    {
        if (M != null)
        {
            if (!M.Proc.HasExited) return false;
            Cheats.MoonJump(null, false); M.Close(); M = null; attachedPid = 0;
            throw new GameClosedException();
        }
        var p = Mem.Find();
        if (p == null || p.Id == attachedPid) return false;
        var m = new Mem(p);
        if (!m.FindBase()) { m.Close(); throw new LoadingException(); }
        Cheats.Saved snap;
        try { snap = Cheats.Snapshot(m); }                       // as cvars ainda podem não existir no começo da carga
        catch (Exception ex) { m.Close(); Log("snapshot das cvars falhou (jogo ainda carregando?): " + ex.Message); throw new LoadingException(); }
        M = m; attachedPid = p.Id; saved = snap;
        Log(string.Format("base {0:X}; instantKill={1} mult={2} swimRootVel={3} noDamage={4} unlimitedGrip={5}", m.Base, saved.InstantKill, saved.Life, saved.SwimRootVel, saved.NoDamage, saved.UnlimitedGrip));
        foreach (var c in List) if (c.On && !c.IsAction) { try { Apply(c); } catch (Exception ex) { Log(c.Name + ": " + ex); } }
        return true;
    }

    public void Shutdown()
    {
        // a entrega do Time Attack precisa desfazer o gancho de FlagGet antes de o processo ser fechado
        if (TaRunning) { TaUnlock.Cancel = true; try { taThread.Join(3000); } catch { } }
        try { Cheats.MoonJump(null, false); if (M != null) { Cheats.OneHitKill(M, saved, false); Cheats.BowKill(M, saved, false); Cheats.Swim(M, saved, false, 1); Cheats.God(M, saved, false); M.Close(); } } catch { }
        M = null;
    }
}
class GameClosedException : Exception { public GameClosedException() : base("Jogo fechado. Esperando abrir de novo...") { } }
class LoadingException : Exception { public LoadingException() : base("Jogo aberto, ainda carregando...") { } }

// ======================================================================
// Visual neobrutalista (cores do AchadoIA: creme, preto, laranja/terracota). Cada categoria tem a sua cor:
// rosa = combate, ciano = movimento, verde = sobrevivência, amarelo = itens.
// ======================================================================
static class Theme
{
    public const int S = 4;   // deslocamento da sombra dura
    public static readonly Color Bg = Neo.Bg;
    public static readonly Color Text = Neo.Ink;
    public static readonly Color Muted = Color.FromArgb(96, 92, 84);
    public static readonly Color Green = Color.FromArgb(18, 130, 60);
    public static readonly Color Amber = Color.FromArgb(190, 95, 0);
    public static readonly Color Red = Color.FromArgb(205, 30, 55);
    public static readonly Color Gold = Color.FromArgb(150, 70, 10);

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}

class CheatRow : Control
{
    public Cheat Cheat;
    public float Anim;
    public bool Live;
    public Color Accent = Neo.Orange;
    bool hover, down;
    public event EventHandler Toggled;

    public CheatRow(Cheat c)
    {
        Cheat = c;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = Theme.Bg;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Toggled != null) Toggled(this, EventArgs.Empty); }

    public bool Step()
    {
        float target = Cheat.On ? 1f : 0f;
        if (Math.Abs(Anim - target) < 0.01f) { if (Anim != target) { Anim = target; return true; } return false; }
        Anim += (target - Anim) * 0.3f;
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.Clear(Theme.Bg);
        int s = Theme.S, o = down ? s - 1 : 0;
        Rectangle r = new Rectangle(o, o, Width - s, Height - s);
        Color baseC = !Live ? Color.FromArgb(240, 238, 230) : (hover ? Color.FromArgb(255, 246, 214) : Neo.White);
        Neo.Box(g, r, Theme.Lerp(baseC, Accent, Anim), down ? 1 : s);

        // tecla do numpad (selo preto)
        Rectangle key = new Rectangle(r.X + 10, r.Y + (r.Height - 26) / 2, 58, 26);
        using (SolidBrush b = new SolidBrush(Neo.Ink)) g.FillRectangle(b, key);
        Neo.Label(g, Cheat.KeyLabel, Neo.Mono(13), Neo.Bg, key, ContentAlignment.MiddleCenter);

        int rw = Cheat.IsAction ? 104 : 74;
        Rectangle ctl = new Rectangle(r.Right - rw - 10, r.Y + (r.Height - 28) / 2, rw, 28);
        int textW = ctl.X - (r.X + 78) - 8;
        Color txt = Live ? Neo.Ink : Color.FromArgb(120, 116, 108);
        Neo.Label(g, Cheat.Name, Neo.Bold(15), txt, new Rectangle(r.X + 78, r.Y + 3, textW, 20));
        Neo.Label(g, Cheat.Desc, Neo.Text(12), Live ? Color.FromArgb(70, 66, 60) : Color.FromArgb(130, 126, 118), new Rectangle(r.X + 78, r.Y + 23, textW, 16));

        if (Cheat.IsAction)
        {
            // botão de ação em vez do interruptor: "LIBERAR" / "ENTREGANDO" enquanto roda
            Neo.Box(g, ctl, Cheat.On ? Neo.Orange : Neo.Yellow, 2, 3);
            Neo.Label(g, Cheat.On ? "ENTREGANDO" : "LIBERAR", Neo.Black(11), Neo.Ink, ctl, ContentAlignment.MiddleCenter);
            return;
        }
        bool on = Cheat.On;
        Neo.Box(g, ctl, on ? Neo.Lime : Neo.White, 2, 3);
        Neo.Label(g, on ? "LIGADO" : "LIGAR", Neo.Black(11), Neo.Ink, ctl, ContentAlignment.MiddleCenter);
    }
}

// linha de valor: botões − / +, barra clicável, teclas NUM x / NUM y
class ValueRow : Control
{
    public string Title, Unit; public int KeyMinus, KeyPlus;
    public decimal Min, Max, Step, Value;
    public bool Live, Active;
    public Color Accent = Neo.Cyan;
    int hover = -1; // 0 = barra, 100 = menos, 101 = mais
    public event Action<decimal> Picked;

    public ValueRow(string title, string unit, int keyMinus, int keyPlus, decimal min, decimal max, decimal step, decimal value)
    {
        Title = title; Unit = unit; KeyMinus = keyMinus; KeyPlus = keyPlus; Min = min; Max = max; Step = step; Value = value;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    Rectangle MinusRect { get { return new Rectangle(12, 32, 26, 24); } }
    Rectangle PlusRect { get { return new Rectangle(Width - Theme.S - 12 - 26, 32, 26, 24); } }
    Rectangle BarRect { get { return new Rectangle(50, 35, Width - Theme.S - 100, 18); } }

    int Hit(Point pt)
    {
        if (MinusRect.Contains(pt)) return 100;
        if (PlusRect.Contains(pt)) return 101;
        Rectangle b = BarRect; b.Inflate(2, 7); if (b.Contains(pt)) return 0;
        return -1;
    }
    decimal ValueAt(float x)
    {
        Rectangle b = BarRect; float t = Math.Max(0, Math.Min(1, (x - b.X) / b.Width));
        return Min + Math.Round((decimal)t * (Max - Min) / Step) * Step;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
        if (h == 0 && e.Button == MouseButtons.Left && Picked != null) Picked(ValueAt(e.X));
    }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        if (h < 0 || Picked == null || e.Button != MouseButtons.Left) return;
        if (h == 100) Picked(Value - Step);
        else if (h == 101) Picked(Value + Step);
        else Picked(ValueAt(e.X));
    }
    protected override void OnMouseWheel(MouseEventArgs e) { if (Picked != null) Picked(Value + (e.Delta > 0 ? Step : -Step)); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.Clear(Theme.Bg);
        int s = Theme.S;
        Rectangle r = new Rectangle(0, 0, Width - s, Height - s);
        Neo.Box(g, r, Live ? (Active ? Color.FromArgb(214, 241, 250) : Neo.White) : Color.FromArgb(240, 238, 230), s);
        Color txt = Live ? Neo.Ink : Color.FromArgb(120, 116, 108);
        Neo.Label(g, Title, Neo.Bold(14), txt, new Rectangle(12, 3, 230, 18));
        Neo.Label(g, "NUM " + (KeyMinus - 0x60) + " diminui  ·  NUM " + (KeyPlus - 0x60) + " aumenta  ·  clique na barra", Neo.Text(11), Color.FromArgb(90, 86, 78), new Rectangle(12, 18, 300, 14));
        Neo.Label(g, Value.ToString("0.0") + Unit, Neo.Black(22), Active ? Neo.Ink : Color.FromArgb(110, 106, 98), new Rectangle(Width - s - 130, 2, 120, 30), ContentAlignment.MiddleRight);

        Neo.Box(g, MinusRect, hover == 100 ? Neo.Yellow : Neo.White, 2, 3);
        Neo.Label(g, "−", Neo.Black(16), Neo.Ink, MinusRect, ContentAlignment.MiddleCenter);
        Neo.Box(g, PlusRect, hover == 101 ? Neo.Yellow : Neo.White, 2, 3);
        Neo.Label(g, "+", Neo.Black(16), Neo.Ink, PlusRect, ContentAlignment.MiddleCenter);

        Rectangle bar = BarRect;
        Neo.Box(g, bar, hover == 0 ? Color.FromArgb(255, 246, 214) : Neo.White, 0, 3);
        float t = (float)((Value - Min) / (Max - Min));
        int fw = (int)Math.Round((bar.Width - 6) * t);
        if (fw > 0) using (SolidBrush b = new SolidBrush(Active ? Accent : Neo.Grey)) g.FillRectangle(b, bar.X + 3, bar.Y + 3, fw, bar.Height - 6);
        // marcador na ponta do valor
        int kx = bar.X + 3 + fw;
        using (SolidBrush b = new SolidBrush(Neo.Ink)) g.FillRectangle(b, Math.Min(kx, bar.Right - 7) - 2, bar.Y - 3, 5, bar.Height + 6);
    }
}

class FlatButton : Control
{
    bool hover, down;
    public FlatButton(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = Theme.Bg;
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.Clear(Theme.Bg);
        int s = Theme.S, o = down ? s - 1 : 0;
        Rectangle r = new Rectangle(o, o, Width - s, Height - s);
        Neo.Box(g, r, hover ? Neo.White : Neo.Pink, down ? 1 : s);
        Neo.Label(g, Text, Neo.Black(14), Neo.Ink, r, ContentAlignment.MiddleCenter);
    }
}

class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

    // a tela do usuário tem 720 px lógicos de altura: a janela precisa caber em ~650
    const int W = 440, TitleH = 34, HeaderH = 104, RowH = 46, RowStep = 48, ValH = 64, ValStep = 66, StatusH = 46;
    int H;
    public Engine eng = new Engine();
    List<CheatRow> rows = new List<CheatRow>();
    ValueRow swimRow, jumpRow;
    System.Windows.Forms.Timer tick, attachTimer;
    bool[] prevKeys = new bool[256];
    string status = "Procurando o jogo (bb-probe)...";
    Color statusColor = Theme.Muted;
    int hoverBtn = -1; // 0 fechar, 1 minimizar, 2 fixar
    float pulse;
    RectangleF pillRect;
    int lastJumps;

    public MainForm()
    {
        Text = "SotC Trainer";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int y = HeaderH + 8;
        foreach (Cheat c in eng.List)
        {
            CheatRow row = new CheatRow(c);
            row.Accent = (c == eng.Kill || c == eng.Bow) ? Neo.Pink : (c == eng.Swim || c == eng.Jump) ? Neo.Cyan : (c == eng.God) ? Neo.Lime : Neo.Yellow;
            row.SetBounds(16, y, W - 32, RowH);
            row.Toggled += delegate(object s, EventArgs e) { ToggleCheat(((CheatRow)s).Cheat, false); };
            Controls.Add(row); rows.Add(row);
            y += RowStep;
            if (c == eng.Swim)
            {
                swimRow = new ValueRow("Velocidade do Wander", "x", 0x64, 0x65, Engine.SwimMin, Engine.SwimMax, Engine.SwimStep, eng.SwimFactor);
                swimRow.SetBounds(16, y, W - 32, ValH);
                swimRow.Picked += delegate(decimal v) { PickSwim(v, false); };
                Controls.Add(swimRow); y += ValStep;
            }
            if (c == eng.Jump)
            {
                jumpRow = new ValueRow("Força do pulo no ar", "", 0x68, 0x69, Engine.JumpMin, Engine.JumpMax, Engine.JumpStep, eng.JumpForce);
                jumpRow.SetBounds(16, y, W - 32, ValH);
                jumpRow.Picked += delegate(decimal v) { PickJump(v, false); };
                Controls.Add(jumpRow); y += ValStep;
            }
        }
        eng.Status += delegate(string s, bool err)
        {
            try { BeginInvoke((Action)delegate { SetStatus(s, err ? Theme.Red : Theme.Gold); if (err) Beep(500, 150); }); } catch { }
        };
        y += 2;
        FlatButton off = new FlatButton("DESLIGAR TUDO  ·  NUM 0");
        off.SetBounds(16, y, W - 32, 44);
        off.Click += delegate { DisableAll(false); };
        Controls.Add(off);
        y += 44 + 6;
        H = y + StatusH;
        ClientSize = new Size(W, H);
        StartPosition = FormStartPosition.Manual;
        var scr = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(scr.Right - W - 20, Math.Max(scr.Top, Math.Min(scr.Top + 20, scr.Bottom - H)));

        tick = new System.Windows.Forms.Timer { Interval = 16 };
        tick.Tick += OnTick;
        tick.Start();
        attachTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        attachTimer.Tick += delegate { CheckGame(); KeepCheats(); };
        attachTimer.Start();
        CheckGame();
        SetLive();
    }

    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // sombra
    }

    void SetStatus(string s, Color c) { status = s; statusColor = c; Invalidate(new Rectangle(0, H - StatusH, W, StatusH)); }

    void SetLive()
    {
        foreach (CheatRow r in rows) { r.Live = eng.Attached; r.Invalidate(); }
        swimRow.Live = eng.Attached; swimRow.Active = eng.Swim.On; swimRow.Value = eng.SwimFactor; swimRow.Invalidate();
        jumpRow.Live = eng.Attached; jumpRow.Active = eng.Jump.On; jumpRow.Value = eng.JumpForce; jumpRow.Invalidate();
        Invalidate(new Rectangle(0, TitleH, W, HeaderH - TitleH));
    }

    void CheckGame()
    {
        try
        {
            if (eng.TryAttach())
            {
                SetLive();
                int on = eng.List.Count(c => c.On);
                SetStatus(on > 0 ? "Conectado ao jogo; " + on + " opção(ões) aplicada(s). Bom jogo!" : "Conectado ao jogo. Bom jogo!", Theme.Green);
            }
        }
        catch (GameClosedException ex) { SetLive(); SetStatus(ex.Message, Theme.Amber); }
        catch (LoadingException ex) { SetStatus(ex.Message, Theme.Amber); }
        catch (Exception ex) { SetLive(); SetStatus(ex.Message, Theme.Red); Engine.Log("attach: " + ex); }
    }

    // o jogo pode desfazer cvars (carga de save, troca de área): reaplica o que estiver ligado e avisa no rodapé
    void KeepCheats()
    {
        try
        {
            if (!eng.Attached || eng.M.Proc.HasExited) return;
            int n = eng.Keep();
            if (n > 0) SetStatus("O jogo tinha desligado " + n + " opção(ões); reapliquei", Theme.Amber);
        }
        catch { }
    }

    static void Beep(int freq, int ms) { ThreadPool.QueueUserWorkItem(delegate { try { Console.Beep(freq, ms); } catch { } }); }

    void ToggleCheat(Cheat c, bool fromKey)
    {
        if (c.IsAction)
        {
            try
            {
                if (c.On || eng.TaRunning) { SetStatus("A entrega dos itens já está em andamento...", Theme.Amber); return; }
                eng.Toggle(c, true);
                SetStatus("Entregando os itens do Time Attack... (com o Wander no controle, fora de cenas)", Theme.Gold);
                if (fromKey) Beep(1200, 110);
            }
            catch (Exception ex) { SetStatus("Erro: " + ex.Message, Theme.Red); Engine.Log(c.Name + ": " + ex); }
            return;
        }
        try
        {
            eng.Toggle(c, !c.On);
            string s = c.Name + (c.On ? " ativado" : " desativado");
            if (!eng.Attached) s += " (aplica quando o jogo abrir)";
            SetStatus(s, c.On ? Theme.Gold : Theme.Muted);
            if (fromKey) Beep(c.On ? 1200 : 500, 110);
        }
        catch (Exception ex) { SetStatus("Erro: " + ex.Message, Theme.Red); Engine.Log(c.Name + ": " + ex); }
        swimRow.Active = eng.Swim.On; swimRow.Invalidate();
        jumpRow.Active = eng.Jump.On; jumpRow.Invalidate();
    }

    void PickSwim(decimal v, bool fromKey)
    {
        try
        {
            eng.SetSwim(v);
            swimRow.Value = eng.SwimFactor; swimRow.Invalidate();
            SetStatus("Velocidade do Wander " + eng.SwimFactor.ToString("0.0") + "x" + (eng.Swim.On ? "" : " (ligue com NUM 3)"), eng.Swim.On ? Theme.Gold : Theme.Muted);
            if (fromKey) Beep(800 + (int)(eng.SwimFactor * 20), 70);
        }
        catch (Exception ex) { SetStatus("Erro: " + ex.Message, Theme.Red); Engine.Log("nado: " + ex); }
    }

    void PickJump(decimal v, bool fromKey)
    {
        eng.SetJump(v);
        jumpRow.Value = eng.JumpForce; jumpRow.Invalidate();
        SetStatus("Força do pulo " + eng.JumpForce.ToString("0.0") + (eng.Jump.On ? "" : " (ligue com NUM 7)"), eng.Jump.On ? Theme.Gold : Theme.Muted);
        if (fromKey) Beep(800 + (int)(eng.JumpForce * 15), 70);
    }

    void DisableAll(bool fromKey)
    {
        try
        {
            eng.DisableAll();
            SetStatus("Todas as opções desligadas", Theme.Muted);
            if (fromKey) Beep(400, 180);
        }
        catch (Exception ex) { SetStatus("Erro: " + ex.Message, Theme.Red); }
        swimRow.Active = false; swimRow.Invalidate();
        jumpRow.Active = false; jumpRow.Invalidate();
    }

    bool KeyPressed(int vk)
    {
        bool down = (Input.GetAsyncKeyState(vk) & 0x8000) != 0;
        bool edge = down && !prevKeys[vk];
        prevKeys[vk] = down;
        return edge;
    }

    void OnTick(object sender, EventArgs e)
    {
        foreach (Cheat c in eng.List) if (KeyPressed(c.Key)) ToggleCheat(c, true);
        if (KeyPressed(swimRow.KeyMinus)) PickSwim(eng.SwimFactor - Engine.SwimStep, true);
        if (KeyPressed(swimRow.KeyPlus)) PickSwim(eng.SwimFactor + Engine.SwimStep, true);
        if (KeyPressed(jumpRow.KeyMinus)) PickJump(eng.JumpForce - Engine.JumpStep, true);
        if (KeyPressed(jumpRow.KeyPlus)) PickJump(eng.JumpForce + Engine.JumpStep, true);
        if (KeyPressed(0x60)) DisableAll(true);

        foreach (CheatRow r in rows) if (r.Step()) r.Invalidate();
        pulse += 0.05f;
        if (eng.Attached) Invalidate(new Rectangle(W - 190, TitleH + 24, 150, 30));
        if (eng.Jump.On && Cheats.Jumps != lastJumps) { lastJumps = Cheats.Jumps; SetStatus("Pulo infinito: " + lastJumps + " pulos no ar", Theme.Gold); }
    }

    // ---------------- Janela / barra de título ----------------
    Rectangle BtnRect(int i) { return new Rectangle(W - 40 - i * 36, 4, 30, TitleH - 8); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < 3; i++) if (BtnRect(i).Contains(e.Location)) h = i;
        if (h != hoverBtn) { hoverBtn = h; Invalidate(new Rectangle(0, 0, W, TitleH)); }
    }
    protected override void OnMouseLeave(EventArgs e) { hoverBtn = -1; Invalidate(new Rectangle(0, 0, W, TitleH)); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (hoverBtn == 0) { Close(); return; }
        if (hoverBtn == 1) { WindowState = FormWindowState.Minimized; return; }
        if (hoverBtn == 2) { TopMost = !TopMost; Invalidate(new Rectangle(0, 0, W, TitleH)); return; }
        if (e.Y < HeaderH) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } // arrastar
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        eng.Shutdown();
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.Clear(Theme.Bg);
        int S = Theme.S;

        // Barra de título: nome, site e botões quadrados
        Neo.Label(g, "TRAINER  ·  v2.3", Neo.Mono(13), Neo.Ink, new Rectangle(16, 0, 150, TitleH));
        Neo.Label(g, "achadoia.com", Neo.Bold(13), Theme.Gold, new Rectangle(W - 3 * 36 - 8 - 120, 0, 118, TitleH), ContentAlignment.MiddleRight);
        string[] glyph = { "✕", "—", TopMost ? "◆" : "◇" };
        Color[] fills = { Neo.Pink, Neo.Yellow, Neo.Cyan };
        for (int i = 0; i < 3; i++)
        {
            Rectangle r = BtnRect(i);
            Neo.Box(g, r, hoverBtn == i ? Neo.White : fills[i], 0, 3);
            Neo.Label(g, glyph[i], Neo.Bold(14), Neo.Ink, r, ContentAlignment.MiddleCenter);
        }

        // Faixa laranja do AchadoIA: título, site, dica de uso e estado da conexão
        Rectangle banner = new Rectangle(16, TitleH + 4, W - 32 - S, 62);
        Neo.Box(g, banner, Neo.Orange, S);
        Neo.Label(g, "SHADOW OF THE COLOSSUS", Neo.Black(17), Neo.Ink, new Rectangle(banner.X + 12, banner.Y + 4, banner.Width - 20, 22));
        Neo.Label(g, "TRAINER  ·  achadoia.com", Neo.Bold(12), Neo.Ink, new Rectangle(banner.X + 12, banner.Y + 26, 190, 16));
        Neo.Label(g, "Aperte a tecla do numpad (Num Lock ligado) ou clique na linha", Neo.Text(11), Neo.Ink, new Rectangle(banner.X + 12, banner.Y + 44, banner.Width - 20, 14));
        string st = eng.Attached ? "CONECTADO" : "AGUARDANDO JOGO";
        Rectangle pill = new Rectangle(banner.Right - 154, banner.Y + 22, 144, 20);
        Neo.Box(g, pill, eng.Attached ? Neo.Lime : Neo.Yellow, 2, 3);
        float glow = eng.Attached ? (float)(0.45 + 0.55 * Math.Sin(pulse)) : 1f;
        using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0, Math.Min(1, glow))), Neo.Ink))) g.FillEllipse(b, pill.X + 9, pill.Y + 5, 10, 10);
        Neo.Label(g, st, Neo.Black(10), Neo.Ink, new Rectangle(pill.X + 24, pill.Y, pill.Width - 26, pill.Height));

        // Rodapé: caixa de estado (o que acabou de acontecer) + legenda de cores
        Rectangle sb = new Rectangle(16, H - StatusH + 2, W - 32 - S, 34);
        Neo.Box(g, sb, Neo.White, S);
        Neo.Label(g, status, Neo.Bold(13), statusColor, new Rectangle(sb.X + 10, sb.Y, sb.Width - 16, sb.Height));

        using (Pen pen = new Pen(Neo.Ink, 3) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset }) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var f = new MainForm();
        // argumentos: --kill  --bow  --jump  --swim [fator]   (já abrem com a opção ligada)
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--kill") f.eng.Kill.On = true;
            if (args[i] == "--bow") f.eng.Bow.On = true;
            if (args[i] == "--god") f.eng.God.On = true;
            if (args[i] == "--jump") f.eng.Jump.On = true;
            if (args[i] == "--swim")
            {
                decimal v;
                if (i + 1 < args.Length && decimal.TryParse(args[i + 1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v)) f.eng.SetSwim(v);
                f.eng.Swim.On = true;
            }
        }
        Application.Run(f);
    }
}
