// SotC-Randomizer-Patch.exe: instala o mod Randomizer no lançador do port SotC-PC (D:\SotC-PC por padrão).
// A pessoa aponta o SotC.exe do lançador; o programa guarda o lançador original como SotC.orig.exe, põe a
// casca (SotC.exe do mod) no lugar e copia os arquivos do mod para <port>\mod_backup. Funciona com qualquer
// versão do lançador (o mod é aplicado em memória pelo launcher_mod.py) e pode ser rodado de novo depois de
// cada atualização. "Remover patch" devolve o lançador original.
// Compilação: csc /target:winexe /resource:files\SotC.exe,shell.exe /resource:... patcher.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// o que o patch instala (caixas do patcher = linhas de mod_backup\randomizer_mods.txt = opções do lançador)
class Mods
{
    // Zelda = pacote inteiro: skins + Master Sword, músicas, corações e fôlego
    public bool Items = true, Colossi = true, Coins = true, Roupas = true, Calango = true, Zelda = true;
    public bool Any { get { return Items || Colossi || Coins || Roupas || Calango || Zelda; } }
    public string Text
    {
        get
        {
            var on = new List<string>();
            if (Items) on.Add("itens"); if (Colossi) on.Add("colossos"); if (Coins) on.Add("mapa das moedas"); if (Roupas) on.Add("roupa da Mono"); if (Calango) on.Add("som do calango");
            if (Zelda) on.Add("Zelda of the Colossus");
            return on.Count == 0 ? "nada" : string.Join(" + ", on);
        }
    }
    // linhas do randomizer_mods.txt (nome, ligado) e a opção do lançador de cada uma
    public string[][] Keys
    {
        get
        {
            return new[] {
                new[] { "items", Items ? "1" : "0", "randomizer" }, new[] { "colossi", Colossi ? "1" : "0", "randomizer_colossi" },
                new[] { "coins", Coins ? "1" : "0", "randomizer_coins" }, new[] { "roupas", Roupas ? "1" : "0", "mod_roupas" },
                new[] { "calango", Calango ? "1" : "0", "mod_calango" }, new[] { "zelda", Zelda ? "1" : "0", "mod_zelda" },
            };
        }
    }
}

static class Patcher
{
    static readonly byte[] MeiCookie = { (byte)'M', (byte)'E', (byte)'I', 0x0C, 0x0B, 0x0A, 0x0B, 0x0E };
    // recurso embutido -> destino (relativo à pasta do port)
    static readonly string[][] Files = {
        new[] { "shell.exe", "SotC.exe" },
        new[] { "launcher_mod.py", @"mod_backup\launcher_mod.py" },
        new[] { "SotC-Randomizer.exe", @"mod_backup\SotC-Randomizer.exe" },
        new[] { "psarc.exe", @"mod_backup\psarc.exe" },
        new[] { "crashwatch.exe", @"mod_backup\crashwatch.exe" },
        new[] { "RANDOMIZADOR.bat", @"mod_backup\RANDOMIZADOR.bat" },
        new[] { "TUTORIAL-CLAUDE.md", @"mod_backup\TUTORIAL-CLAUDE.md" },
        new[] { "moedas.txt", @"mod_backup\moedas.txt" },
        new[] { "SotC-Trainer.exe", @"mod_backup\trainer\SotC-Trainer.exe" },
    };
    // mods de arquivo: mods.zip -> mod_backup\mods\<mod>\<caminho dentro do psarc>; o lançador aplica/desfaz ao JOGAR.
    // As skins do Zelda (mods\zelda_skins, 380 MB) e as músicas (musicas_zelda) não vão no patch: ficam no mod_backup.
    const string ModsZip = "mods.zip";
    static readonly string[] Archives = { "cachedFiles.psarc", "pak0.psarc", "pakN.psarc" };
    // pastas (linhas de <psarc>.mods) de cada mod de arquivo, pelo nome no randomizer_mods.txt
    static readonly Dictionary<string, string[]> FileModFolders = new Dictionary<string, string[]> {
        { "roupas", new[] { "roupa_mono" } },
        { "calango", new[] { "som_calango" } },
        { "coins", new[] { "mapa_moedas/cachedFiles", "mapa_moedas/pakN" } },
        { "zelda", new[] { "zelda_skins/cachedFiles", "zelda_skins/pak0", "zelda_skins/pakN" } },
    };
    public static bool HasZelda(string dir) { return Directory.Exists(Path.Combine(dir, @"mod_backup\mods\zelda_skins\pakN")); }
    // cachedFiles.psarc: MD5 do original e da cópia inteira com o mod de roupas (forma antiga, APLICAR_MOD_ROUPAS.bat)
    const string CachedOrigMd5 = "0BE74F946186436CEAF87C16D3E96D8A", CachedRoupasMd5 = "9EED33EA29113ECD0D09B3AC22BBE176";

    [STAThread]
    static int Main(string[] args)
    {
        // modo sem janela (para scripts): --apply <SotC.exe> [--no-items] [--no-colossi] [--no-coins] [--no-roupas]
        // [--no-calango] [--no-zelda] | --remove <SotC.exe>; o resultado vai para patch.log
        if (args.Length >= 2 && (args[0] == "--apply" || args[0] == "--remove"))
        {
            var lines = new List<string>();
            int rc = 0;
            var m = new Mods { Items = !args.Contains("--no-items"), Colossi = !args.Contains("--no-colossi"), Coins = !args.Contains("--no-coins"), Roupas = !args.Contains("--no-roupas"), Calango = !args.Contains("--no-calango"), Zelda = !args.Contains("--no-zelda") };
            try { if (args[0] == "--apply") Apply(args[1], m, lines.Add); else Remove(args[1], lines.Add); }
            catch (Exception e) { lines.Add("ERRO: " + e.Message); rc = 1; }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "patch.log"), lines.ToArray(), Encoding.UTF8);
            return rc;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    public static byte[] Resource(string name)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new Exception("recurso embutido ausente: " + name);
            var b = new byte[s.Length]; int n = 0;
            while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
            return b;
        }
    }

    public static bool IsOriginalLauncher(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 200000) return false;
            // o cookie do PyInstaller fica no fim do arquivo
            int start = Math.Max(0, b.Length - 4096);
            for (int i = b.Length - MeiCookie.Length; i >= start; i--)
            {
                int k = 0; while (k < MeiCookie.Length && b[i + k] == MeiCookie[k]) k++;
                if (k == MeiCookie.Length) return true;
            }
            return false;
        }
        catch { return false; }
    }

    public static bool IsShell(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length > 2000000) return false;
            var marker = Encoding.Unicode.GetBytes("SotC.orig.exe");   // string .NET (UTF-16) da casca
            for (int i = 0; i + marker.Length <= b.Length; i++)
            {
                int k = 0; while (k < marker.Length && b[i + k] == marker[k]) k++;
                if (k == marker.Length) return true;
            }
            return false;
        }
        catch { return false; }
    }

    public static string FindDefaultLauncher()
    {
        var here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        var candidates = new List<string> {
            Path.Combine(here, "SotC.exe"),
            Path.Combine(Path.GetDirectoryName(here) ?? here, "SotC.exe"),
            @"D:\SotC-PC\SotC.exe", @"C:\SotC-PC\SotC.exe", @"E:\SotC-PC\SotC.exe",
        };
        foreach (var d in DriveInfo.GetDrives()) if (d.DriveType == DriveType.Fixed) candidates.Add(Path.Combine(d.Name, @"SotC-PC\SotC.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    // processos que precisam estar fechados (jogo, lançador); o randomizer antigo é encerrado
    public static string Running(string portDir)
    {
        var prefix = portDir.TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcesses())
        {
            string name, file;
            try { name = p.ProcessName; file = p.MainModule.FileName; } catch { continue; }
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("SotC-Randomizer", StringComparison.OrdinalIgnoreCase)) { try { p.Kill(); p.WaitForExit(3000); } catch { } continue; }
            if (name.Equals("bb-probe", StringComparison.OrdinalIgnoreCase)) return "O jogo está aberto (bb-probe.exe). Feche o jogo e tente de novo.";
            if (name.Equals("SotC", StringComparison.OrdinalIgnoreCase) || name.Equals("SotC.orig", StringComparison.OrdinalIgnoreCase))
                return "O lançador está aberto. Feche o lançador e tente de novo.";
        }
        return null;
    }

    // quais randomizers ficam disponíveis no lançador: mod_backup\randomizer_mods.txt (lido pelo launcher_mod.py)
    public const string ModsFile = @"mod_backup\randomizer_mods.txt";

    // sem o arquivo: null (instalação nova). Linhas ausentes seguem o launcher_mod.py: zelda ligado quando a
    // pasta das skins existe, as outras desligadas
    public static Mods ReadMods(string dir)
    {
        var f = Path.Combine(dir, ModsFile);
        if (!File.Exists(f)) return null;
        var m = new Mods { Coins = false, Roupas = false, Calango = false, Zelda = HasZelda(dir) };
        try
        {
            foreach (var line in File.ReadAllLines(f))
            {
                var kv = line.Split('='); if (kv.Length != 2) continue;
                bool on = kv[1].Trim() == "1";
                switch (kv[0].Trim())
                {
                    case "items": m.Items = on; break;
                    case "colossi": m.Colossi = on; break;
                    case "coins": m.Coins = on; break;
                    case "roupas": m.Roupas = on; break;
                    case "calango": m.Calango = on; break;
                    case "zelda": m.Zelda = on && HasZelda(dir); break;
                }
            }
        }
        catch { }
        return m;
    }

    // pasta do jogo (user\launcher_settings.json "game_dir"; senão a padrão do port)
    static string GameDir(string dir)
    {
        try
        {
            var f = Path.Combine(dir, @"user\launcher_settings.json");
            if (File.Exists(f))
            {
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(f), "\"game_dir\"\\s*:\\s*\"([^\"]*)\"");
                if (m.Success)
                {
                    var g = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                    if (Directory.Exists(g)) return g;
                }
            }
        }
        catch { }
        return Path.Combine(dir, @"shadow\EP9000-CUSA08809_00-SOTC0000000000EU");
    }

    static string Md5(string file)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        using (var s = File.OpenRead(file)) return BitConverter.ToString(md5.ComputeHash(s)).Replace("-", "");
    }

    static int RunPsarc(string mb, Action<string> log, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(mb, "psarc.exe"), string.Join(" ", args.Select(a => "\"" + a + "\"")))
        { WorkingDirectory = mb, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using (var p = Process.Start(psi))
        {
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode != 0 && outp != "") log("psarc: " + outp);
            return p.ExitCode;
        }
    }

    // devolve um psarc do jogo ao original (tocbak presente) e apaga tocbak + <psarc>.mods; os mods que
    // continuarem ligados no lançador são aplicados de novo no próximo JOGAR
    static void RestoreArchive(string dir, string mb, string name, string why, Action<string> log)
    {
        var archive = Path.Combine(GameDir(dir), name);
        var tocbak = Path.Combine(mb, name + ".tocbak");
        if (!File.Exists(tocbak) || !File.Exists(archive) || !File.Exists(Path.Combine(mb, "psarc.exe"))) return;
        if (RunPsarc(mb, log, "restore", archive, mb) == 0)
        {
            File.Delete(tocbak);
            try { File.Delete(Path.Combine(mb, name + ".mods")); } catch { }
            log(name + " restaurado ao original (" + why + ").");
        }
        else log("Aviso: não consegui restaurar " + name + " (" + why + ").");
    }

    static void RestoreFileMods(string dir, string mb, Action<string> log)
    {
        foreach (var a in Archives) RestoreArchive(dir, mb, a, "patch removido", log);
    }

    // pastas aplicadas num psarc (mod_backup\<psarc>.mods, uma por linha)
    static string[] AppliedFolders(string mb, string archive)
    {
        try { return File.ReadAllLines(Path.Combine(mb, archive + ".mods")).Select(l => l.Trim()).Where(l => l != "").ToArray(); }
        catch { return new string[0]; }
    }

    // forma antiga do mod de roupas (cachedFiles.psarc inteiro trocado): devolve o original guardado em mod_backup
    static void MigrateOldClothes(string dir, string mb, Action<string> log)
    {
        try
        {
            var game = Path.Combine(GameDir(dir), "cachedFiles.psarc");
            var orig = Path.Combine(mb, "cachedFiles.psarc");
            if (!File.Exists(game) || !File.Exists(orig) || File.Exists(Path.Combine(mb, "cachedFiles.psarc.tocbak"))) return;
            if (Md5(game) != CachedRoupasMd5) return;
            if (Md5(orig) != CachedOrigMd5) { log("Aviso: cachedFiles.psarc do jogo é a cópia antiga do mod de roupas, mas mod_backup\\cachedFiles.psarc não é o original; deixei como está."); return; }
            File.Copy(orig, game, true);
            log("cachedFiles.psarc: cópia antiga do mod de roupas trocada pelo original (o mod agora é ligado pelo lançador).");
        }
        catch (Exception e) { log("Aviso: migração do mod de roupas: " + e.Message); }
    }

    static void ExtractMods(string mb, Action<string> log)
    {
        var root = Path.Combine(mb, "mods");
        int n = 0;
        using (var za = new ZipArchive(new MemoryStream(Resource(ModsZip)), ZipArchiveMode.Read))
            foreach (var e in za.Entries)
            {
                var rel = e.FullName.Replace('/', '\\').TrimStart('\\');
                if (rel == "" || rel.EndsWith("\\") || rel.Contains("..")) continue;
                var dst = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                using (var s = e.Open()) using (var f = File.Create(dst)) s.CopyTo(f);
                n++;
            }
        log("Gravado mod_backup\\mods\\ (" + n + " arquivos: roupa da Mono, som do calango, ícone de moeda do mapa)");
    }

    // liga/desliga as opções salvas do lançador (user\launcher_settings.json) conforme a escolha; sem BOM,
    // porque o lançador trata um JSON com BOM como configuração ausente (volta para a tela de instalação)
    static void SetLauncherSetting(string dir, string key, bool value, Action<string> log)
    {
        var f = Path.Combine(dir, @"user\launcher_settings.json");
        if (!File.Exists(f)) return;
        try
        {
            var text = File.ReadAllText(f, new UTF8Encoding(false)).TrimStart('﻿');
            var val = value ? "true" : "false";
            var re = new System.Text.RegularExpressions.Regex("\"" + key + "\"\\s*:\\s*(true|false)");
            if (re.IsMatch(text)) text = re.Replace(text, "\"" + key + "\": " + val, 1);
            else
            {
                int end = text.LastIndexOf('}');
                if (end < 0) return;
                var before = text.Substring(0, end).TrimEnd();
                text = before + (before.EndsWith("{") ? "" : ",") + "\r\n  \"" + key + "\": " + val + "\r\n}\r\n";
            }
            File.WriteAllText(f, text, new UTF8Encoding(false));
            log("Opção \"" + key + "\" do lançador: " + (value ? "ligada" : "desligada"));
        }
        catch (Exception e) { log("Aviso: não ajustei launcher_settings.json (" + e.Message + ")"); }
    }

    public static void Apply(string launcher, Mods m, Action<string> log)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(launcher));
        var shell = Path.Combine(dir, "SotC.exe");
        var orig = Path.Combine(dir, "SotC.orig.exe");
        var mb = Path.Combine(dir, "mod_backup");
        if (!m.Any) throw new Exception("Marque pelo menos um mod para instalar.");
        if (!File.Exists(launcher)) throw new Exception("Arquivo não encontrado: " + launcher);
        if (!Path.GetFileName(launcher).Equals("SotC.exe", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Escolha o SotC.exe do lançador (o arquivo que abre o port).");
        if (!Directory.Exists(Path.Combine(dir, "bin")) && !File.Exists(Path.Combine(dir, "sotc.py")) && !Directory.Exists(Path.Combine(dir, "_internal")))
            log("Aviso: a pasta não parece a do port (sem bin\\ nem sotc.py); seguindo mesmo assim.");
        var busy = Running(dir);
        if (busy != null) throw new Exception(busy);
        Directory.CreateDirectory(mb);

        if (IsOriginalLauncher(shell))
        {
            if (File.Exists(orig))
            {
                var prev = Path.Combine(mb, "SotC.orig.anterior.exe");
                File.Copy(orig, prev, true); File.Delete(orig);
                log("Lançador anterior guardado em mod_backup\\SotC.orig.anterior.exe");
            }
            File.Move(shell, orig);
            log("Lançador original guardado como SotC.orig.exe");
        }
        else if (IsShell(shell))
        {
            if (!File.Exists(orig) || !IsOriginalLauncher(orig))
                throw new Exception("Este SotC.exe já é a casca do mod, mas o lançador original (SotC.orig.exe) não está ao lado dele.\nReinstale o port (ou baixe o lançador de novo) e rode o patch outra vez.");
            log("A casca do mod já estava instalada; atualizando os arquivos do mod.");
            File.Delete(shell);
        }
        else
            throw new Exception("Este SotC.exe não parece o lançador do port SotC-PC (nem a casca do mod).");

        if (m.Zelda && !HasZelda(dir))
        {
            m.Zelda = false;
            log("Aviso: Zelda não instalado: falta a pasta mod_backup\\mods\\zelda_skins (as skins têm 380 MB e não vão dentro do patch).");
        }
        var before = ReadMods(dir);   // null = instalação nova
        foreach (var f in Files)
        {
            var dst = Path.Combine(dir, f[1]);
            var data = Resource(f[0]);
            try { Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.WriteAllBytes(dst, data); }
            catch (Exception e) { if (f[0] == "SotC-Trainer.exe") { log("Aviso: trainer não atualizado (" + e.Message + "); feche o trainer e aplique de novo."); continue; } throw; }
            log("Gravado " + f[1] + " (" + (data.Length / 1024) + " KB)");
        }
        ExtractMods(mb, log);
        MigrateOldClothes(dir, mb, log);
        // mod de arquivo desmarcado no patch: se estiver aplicado no jogo, desfaz agora (o lançador não o mostra mais)
        foreach (var k in m.Keys)
        {
            string[] folders;
            if (k[1] == "1" || !FileModFolders.TryGetValue(k[0], out folders)) continue;
            foreach (var a in Archives)
                if (AppliedFolders(mb, a).Any(folders.Contains)) RestoreArchive(dir, mb, a, k[0] + " desinstalado", log);
        }
        File.WriteAllText(Path.Combine(dir, ModsFile), string.Concat(m.Keys.Select(k => k[0] + "=" + k[1] + "\r\n")), new UTF8Encoding(false));
        log("Instalado: " + m.Text);
        // a opção do lançador só muda quando a caixa muda (reaplicar o patch não religa o que a pessoa desligou no lançador)
        var was = before == null ? null : before.Keys.ToDictionary(k => k[0], k => k[1]);
        foreach (var k in m.Keys)
            if (was == null || !was.ContainsKey(k[0]) || was[k[0]] != k[1]) SetLauncherSetting(dir, k[2], k[1] == "1", log);
        if (!m.Zelda) SetLauncherSetting(dir, "mod_coracoes", false, log);   // caixa antiga (v2.16), agora parte do Zelda
        log("Patch aplicado. Os mods de arquivo (roupa, som, Zelda, ícone das moedas) entram ou saem do jogo ao clicar JOGAR, conforme as caixas do lançador.");
    }

    public static void Remove(string launcher, Action<string> log)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(launcher));
        var shell = Path.Combine(dir, "SotC.exe");
        var orig = Path.Combine(dir, "SotC.orig.exe");
        var busy = Running(dir);
        if (busy != null) throw new Exception(busy);
        if (IsOriginalLauncher(shell)) { log("O SotC.exe já é o lançador original; nada a remover."); }
        else if (IsShell(shell))
        {
            if (!File.Exists(orig) || !IsOriginalLauncher(orig)) throw new Exception("SotC.orig.exe (o lançador original) não está na pasta; não dá para restaurar.");
            File.Delete(shell); File.Move(orig, shell);
            log("Lançador original restaurado como SotC.exe.");
        }
        else throw new Exception("Este SotC.exe não parece o lançador nem a casca do mod.");
        var mod = Path.Combine(dir, @"mod_backup\launcher_mod.py");
        if (File.Exists(mod)) { File.Delete(mod); log("mod_backup\\launcher_mod.py removido (o restante de mod_backup fica, com o estado e o log)."); }
        RestoreFileMods(dir, Path.Combine(dir, "mod_backup"), log);
        var mods = Path.Combine(dir, ModsFile);
        if (File.Exists(mods)) File.Delete(mods);
        foreach (var k in new Mods().Keys) SetLauncherSetting(dir, k[2], false, log);
        log("Patch removido.");
    }
}

class MainForm : Form
{
    TextBox path; TextBox log; Button apply, remove; CheckBox cbItems, cbColossi, cbCoins, cbRoupas, cbCalango, cbZelda;

    // layout neobrutalista: tudo desenhado em OnPaint (faixa, cartão, molduras) e os controles por cima
    Rectangle bannerR = new Rectangle(16, 16, 640, 64), pathR, cardR = new Rectangle(16, 262, 640, 200), logR = new Rectangle(16, 482, 640, 104);

    static NeoCheck Check(string text, int x, int y, int w, Color bg)
    {
        return new NeoCheck { Text = text, Checked = true, Location = new Point(x, y), Size = new Size(w, 28), BackColor = bg };
    }

    public MainForm()
    {
        Text = "SotC Randomizer — patch do lançador";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        ClientSize = new Size(680, 668);
        DoubleBuffered = true;
        Font = Neo.Text(14);
        BackColor = Neo.Bg; ForeColor = Neo.Ink;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var intro = new Label
        {
            Text = "Aponte o SotC.exe do lançador do port (a pasta onde o port foi instalado). O patch guarda o lançador original como " +
                   "SotC.orig.exe, coloca a casca do mod no lugar e copia os mods para mod_backup. Depois, na tela Jogar do lançador, " +
                   "em \"Ajustes rápidos\", aparecem as caixas dos mods escolhidos.\n" +
                   "Atualizou o lançador pelo GitHub? Rode este patch de novo: serve para qualquer versão.",
            Font = Neo.Text(13), ForeColor = Neo.Ink, BackColor = Neo.Bg, Location = new Point(16, 98), Size = new Size(648, 84)
        };
        var lbl = new Label { Text = "SOTC.EXE DO LANÇADOR", Font = Neo.Black(12), ForeColor = Neo.Ink, BackColor = Neo.Bg, AutoSize = true, Location = new Point(16, 192) };
        pathR = new Rectangle(16, 214, 520, 36);
        path = new TextBox { Location = new Point(pathR.X + 8, pathR.Y + 8), Size = new Size(pathR.Width - 16, 22), BackColor = Neo.White, ForeColor = Neo.Ink, BorderStyle = BorderStyle.None, Font = Neo.Mono(14) };
        path.Text = Patcher.FindDefaultLauncher();
        var browse = new NeoButton { Text = "Procurar", Fill = Neo.Cyan, Hover = Neo.White, Font = Neo.Black(13), Location = new Point(pathR.Right + 12, pathR.Y), Size = new Size(98 + Neo.Shadow, 36 + Neo.Shadow) };
        browse.Click += (s, e) =>
        {
            using (var d = new OpenFileDialog { Title = "Escolha o SotC.exe do lançador", Filter = "Lançador do port (SotC.exe)|SotC.exe|Executáveis|*.exe", FileName = "SotC.exe" })
            {
                try { if (path.Text != "" && File.Exists(path.Text)) d.InitialDirectory = Path.GetDirectoryName(path.Text); } catch { }
                if (d.ShowDialog(this) == DialogResult.OK) path.Text = d.FileName;
            }
        };
        var lblMods = new Label { Text = "O QUE INSTALAR", Font = Neo.Black(14), ForeColor = Neo.Ink, BackColor = Neo.White, AutoSize = true, Location = new Point(cardR.X + 16, cardR.Y + 12) };
        var lblSub = new Label { Text = "cada item vira uma opção no lançador", Font = Neo.Text(12), ForeColor = Color.FromArgb(90, 86, 78), BackColor = Neo.White, AutoSize = true, Location = new Point(cardR.X + 190, cardR.Y + 15) };
        int cx = cardR.X + 14, c2 = cardR.X + 300, y0 = cardR.Y + 46;
        cbItems = Check("Randomizer de itens", cx, y0, 280, Neo.White);
        cbColossi = Check("Randomizer de colossos (ordem sorteada)", c2, y0, 330, Neo.White);
        cbRoupas = Check("Wander com roupa da Mono", cx, y0 + 34, 280, Neo.White);
        cbCalango = Check("Som do calanguinho (cauda)", c2, y0 + 34, 330, Neo.White);
        cbCoins = Check("Mapa das 78 moedas (no lugar das árvores frutíferas)", cx, y0 + 68, 610, Neo.White);
        cbZelda = Check("Zelda of the Colossus (skins, Master Sword, músicas, corações)", cx, y0 + 102, 610, Neo.White);
        log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(logR.X + 8, logR.Y + 8), Size = new Size(logR.Width - 16, logR.Height - 16), BackColor = Neo.Ink, ForeColor = Neo.Lime, BorderStyle = BorderStyle.None, Font = Neo.Mono(13) };
        int by = 606, bh = 44 + Neo.Shadow;
        apply = new NeoButton { Text = "Aplicar patch", Fill = Neo.Orange, Hover = Neo.Lime, Location = new Point(16, by), Size = new Size(210 + Neo.Shadow, bh) };
        remove = new NeoButton { Text = "Remover patch", Fill = Neo.Terra, Hover = Neo.White, Location = new Point(244, by), Size = new Size(190 + Neo.Shadow, bh) };
        var close = new NeoButton { Text = "Fechar", Fill = Neo.White, Hover = Neo.Yellow, Location = new Point(560, by), Size = new Size(96 + Neo.Shadow, bh) };
        close.Click += (s, e) => Close();
        apply.Click += (s, e) => Run(true);
        remove.Click += (s, e) => Run(false);
        Controls.AddRange(new Control[] { intro, lbl, path, browse, lblMods, lblSub, cbItems, cbColossi, cbRoupas, cbCalango, cbCoins, cbZelda, log, apply, remove, close });
        Describe();
        path.TextChanged += (s, e) => Describe();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Neo.Box(g, bannerR, Neo.Orange);
        Neo.Label(g, "SOTC RANDOMIZER", Neo.Black(30), Neo.Ink, new Rectangle(bannerR.X + 18, bannerR.Y + 2, 420, 38));
        Neo.Label(g, "achadoia.com", Neo.Bold(14), Neo.Ink, new Rectangle(bannerR.X + 20, bannerR.Y + 38, 300, 20));
        var tag = new Rectangle(bannerR.Right - 196, bannerR.Y + 14, 176, 36);
        Neo.Box(g, tag, Neo.Bg, 3, 3);
        Neo.Label(g, "MOD BY @ACHADOIA", Neo.Black(13), Neo.Ink, tag, ContentAlignment.MiddleCenter);
        Neo.Box(g, pathR, Neo.White, 4);
        Neo.Box(g, cardR, Neo.White);
        Neo.Box(g, logR, Neo.Ink, 4);
    }

    void Describe()
    {
        log.Clear();
        var p = path.Text.Trim();
        if (p == "" || !File.Exists(p)) { Log("Escolha o SotC.exe do lançador (normalmente D:\\SotC-PC\\SotC.exe)."); return; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(p));
        bool zelda = Patcher.HasZelda(dir);
        cbZelda.Enabled = zelda;
        if (!zelda) cbZelda.Checked = false;
        if (Patcher.IsOriginalLauncher(p)) Log("Lançador original encontrado (sem o mod). Clique em \"Aplicar patch\".");
        else if (Patcher.IsShell(p))
        {
            var orig = Path.Combine(dir, "SotC.orig.exe");
            Log(File.Exists(orig) ? "O mod já está instalado aqui (SotC.exe é a casca; SotC.orig.exe é o lançador). \"Aplicar patch\" atualiza os arquivos do mod."
                                  : "SotC.exe é a casca do mod, mas SotC.orig.exe não existe: reinstale o lançador e rode o patch de novo.");
            var m = Patcher.ReadMods(dir);
            if (m != null)
            {
                cbItems.Checked = m.Items; cbColossi.Checked = m.Colossi; cbCoins.Checked = m.Coins; cbRoupas.Checked = m.Roupas; cbCalango.Checked = m.Calango; cbZelda.Checked = m.Zelda;
                Log("Instalado agora: " + m.Text + ". Mude as caixas acima e aplique de novo para trocar.");
            }
        }
        else Log("Este arquivo não parece o lançador do port SotC-PC.");
        if (!zelda) Log("Zelda indisponível: falta mod_backup\\mods\\zelda_skins (380 MB, não vem dentro do patch).");
    }

    void Log(string s) { log.AppendText(s + "\r\n"); }

    void Run(bool install)
    {
        log.Clear();
        apply.Enabled = remove.Enabled = false;
        try
        {
            if (install) Patcher.Apply(path.Text.Trim(), new Mods { Items = cbItems.Checked, Colossi = cbColossi.Checked, Coins = cbCoins.Checked, Roupas = cbRoupas.Checked, Calango = cbCalango.Checked, Zelda = cbZelda.Checked }, Log); else Patcher.Remove(path.Text.Trim(), Log);
            if (install)
            {
                Log("");
                Log("Abra o lançador (SotC.exe): na tela Jogar > Ajustes rápidos os mods escolhidos já vêm ligados; desligue ali quando quiser jogar sem eles.");
                if (MessageBox.Show(this, "Patch aplicado.\n\nAbrir o lançador agora?", "SotC Randomizer", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    var shell = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path.Text.Trim())), "SotC.exe");
                    try { Process.Start(new ProcessStartInfo(shell) { WorkingDirectory = Path.GetDirectoryName(shell), UseShellExecute = true }); } catch (Exception ex) { Log("Não abriu: " + ex.Message); }
                }
            }
            else MessageBox.Show(this, "Patch removido. O lançador original está de volta.", "SotC Randomizer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERRO: " + ex.Message);
            MessageBox.Show(this, ex.Message, "SotC Randomizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        apply.Enabled = remove.Enabled = true;
    }
}
