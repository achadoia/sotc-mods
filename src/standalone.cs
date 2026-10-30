// SotC-Randomizer.exe: the randomizer on its own. Lives in <port>\mod_backup.
//   --once      quit when the game quits (the launcher uses this)
//   --items     item randomizer (default when no mode is given)
//   --colossi   colossus order randomizer
//   --music     Zelda music (mod_backup\musicas_zelda), on with the launcher's "Skins do Zelda"
//   --hud       Zelda hearts + stamina wheel instead of the game's life bar and grip meter (zeldahud.cs)
//   --marca     "Mod by: @AchadoIA" watermark on the game's menus (marca.cs); the launcher passes it with any mod on
//   cave <file> / cave2 <file>   write the code cave bytes (debugging)
using System;
using System.IO;
using System.Linq;

static class Standalone
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "cave") { File.WriteAllText(args[1], BitConverter.ToString(Game.CaveBytes()).Replace("-", "")); return; }
        if (args.Length > 1 && args[0] == "cave2") { File.WriteAllText(args[1], BitConverter.ToString(Game.Cave2Bytes()).Replace("-", "")); return; }
        if (args.Length > 1 && args[0] == "cave3") { File.WriteAllText(args[1], BitConverter.ToString(Game.Cave3Bytes()).Replace("-", "")); return; }
        if (args.Length > 1 && args[0] == "cave5") { File.WriteAllText(args[1], BitConverter.ToString(Game.Cave5Bytes()).Replace("-", "")); return; }
        var here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        Randomizer.PortDir = Path.GetFileName(here).Equals("mod_backup", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(here) : here;
        bool items = args.Contains("--items"), colossi = args.Contains("--colossi"), coins = args.Contains("--coins"), music = args.Contains("--music"), hud = args.Contains("--hud"), marca = args.Contains("--marca");
        Randomizer.Items = items || (!colossi && !coins && !music && !hud && !marca);
        Randomizer.Watermark = marca;
        Randomizer.Hud = hud;
        Randomizer.Colossi = colossi;
        Randomizer.Coins = coins;
        Randomizer.Music = music;
        Randomizer.Loop(args.Contains("--once"));
    }
}
