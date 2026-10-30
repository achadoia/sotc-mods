// SotC.exe (mod): a casca sobre o lançador do port. O lançador original fica ao lado como SotC.orig.exe.
// Com mod_backup\launcher_mod.py presente, abre o lançador original por ele (o Python embutido do
// próprio lançador roda o script), e o lançador ganha a opção "Randomizer de itens (mod)" na tela
// Jogar; o script é quem inicia o randomizador junto com o jogo.
// Sem o script (forma antiga): se as "Variáveis extras" (Avançado) contêm RANDOMIZER=1, o randomizador
// roda numa thread deste processo; senão é só um repasse ao lançador original.
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Shell
{
    [STAThread]
    static int Main(string[] args)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        var orig = Path.Combine(dir, "SotC.orig.exe");
        if (!File.Exists(orig))
        {
            MessageBox.Show("SotC.orig.exe não encontrado ao lado deste SotC.exe.\n\nInstalação do mod: renomeie o SotC.exe original para SotC.orig.exe e copie este SotC.exe para a pasta do port (ou rode APLICAR-RANDOMIZER.bat).",
                "SotC Randomizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var quoted = string.Join(" ", Array.ConvertAll(args, a => a.Contains(" ") ? "\"" + a + "\"" : a));
        var mod = Path.Combine(dir, "mod_backup", "launcher_mod.py");
        if (File.Exists(mod))
        {
            var p = Process.Start(new ProcessStartInfo(orig, "--script \"" + mod + "\" " + quoted) { WorkingDirectory = dir, UseShellExecute = false });
            p.WaitForExit();
            return p.ExitCode;
        }
        return Legacy(dir, orig, quoted);
    }

    static int Legacy(string dir, string orig, string quoted)
    {
        bool enabled = false;
        try
        {
            var settings = File.ReadAllText(Path.Combine(dir, "user", "launcher_settings.json"));
            var m = Regex.Match(settings, "\"extra_env\"\\s*:\\s*\"([^\"]*)\"");
            enabled = m.Success && Regex.IsMatch(m.Groups[1].Value, @"(^|[\s,;])RANDOMIZER\s*=\s*1\b", RegexOptions.IgnoreCase);
        }
        catch { }
        Thread worker = null;
        if (enabled)
        {
            Randomizer.PortDir = dir;
            worker = new Thread(() => { try { Randomizer.Loop(true); } catch (Exception e) { Game.Log("randomizador parou: {0}", e.Message); } });
            worker.IsBackground = true;
            worker.Start();
        }
        var p = Process.Start(new ProcessStartInfo(orig, quoted) { WorkingDirectory = dir, UseShellExecute = false });
        p.WaitForExit();
        // the launcher may close before the game: keep the randomizer alive while the game runs
        if (worker != null)
        {
            while (worker.IsAlive && Game.Find() != null) Thread.Sleep(2000);
            Thread.Sleep(2500);
        }
        return p.ExitCode;
    }
}
