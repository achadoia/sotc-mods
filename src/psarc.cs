// psarc tool: list / extract files from Sony PSARC v1.4 (zlib) archives
// usage:
//   psarc list <archive>                     -> prints index, size, name
//   psarc extract <archive> <outdir> <regex> -> extracts entries whose name matches regex
//   psarc info <archive>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

class Entry { public byte[] Md5; public uint Block; public long Size; public long Offset; public string Name; }

class Psarc
{
    public string Path; public uint TocLen, EntrySize, NumFiles, BlockSize, Flags;
    public List<Entry> Entries = new List<Entry>();
    public int[] ZSizes; public int ZBytes; public int Matched, Unmatched;

    static uint BE32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
    static long BE40(byte[] b, int o) { return ((long)b[o] << 32) | ((long)b[o + 1] << 24) | ((long)b[o + 2] << 16) | ((long)b[o + 3] << 8) | b[o + 4]; }

    public Psarc(string path)
    {
        Path = path;
        using (var fs = File.OpenRead(path))
        {
            var h = new byte[32]; fs.Read(h, 0, 32);
            TocLen = BE32(h, 12); EntrySize = BE32(h, 16); NumFiles = BE32(h, 20); BlockSize = BE32(h, 24); Flags = BE32(h, 28);
            var toc = new byte[TocLen - 32]; fs.Read(toc, 0, toc.Length);
            for (int i = 0; i < NumFiles; i++)
            {
                int o = i * (int)EntrySize;
                var e = new Entry();
                e.Md5 = new byte[16]; Array.Copy(toc, o, e.Md5, 0, 16);
                e.Block = BE32(toc, o + 16); e.Size = BE40(toc, o + 20); e.Offset = BE40(toc, o + 25);
                Entries.Add(e);
            }
            ZBytes = BlockSize <= 0x10000 ? 2 : (BlockSize <= 0x1000000 ? 3 : 4);
            int zstart = (int)(NumFiles * EntrySize);
            int nz = (toc.Length - zstart) / ZBytes;
            ZSizes = new int[nz];
            for (int i = 0; i < nz; i++)
            {
                int v = 0; for (int k = 0; k < ZBytes; k++) v = (v << 8) | toc[zstart + i * ZBytes + k];
                ZSizes[i] = v;
            }
        }
        var man = Encoding.UTF8.GetString(Read(Entries[0]));
        var names = man.Split(new[] { '\n', ' ', '\0', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        Entries[0].Name = "<manifest>";
        // entries are matched to names by the MD5 of the path; try the usual path spellings
        var byHash = new Dictionary<string, Entry>();
        foreach (var e in Entries) byHash[BitConverter.ToString(e.Md5)] = e;
        var md5 = System.Security.Cryptography.MD5.Create();
        int matched = 0, unmatched = 0;
        foreach (var n in names)
        {
            Entry hit = null;
            foreach (var v in new[] { n, "/" + n, n.ToUpperInvariant(), "/" + n.ToUpperInvariant(), n.ToLowerInvariant() })
            {
                var key = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(v)));
                if (byHash.TryGetValue(key, out hit)) break;
            }
            if (hit != null && hit.Name == null) { hit.Name = n; matched++; } else unmatched++;
        }
        for (int i = 1; i < Entries.Count; i++) if (Entries[i].Name == null) Entries[i].Name = "?unnamed_" + i;
        Matched = matched; Unmatched = unmatched;
    }

    public byte[] Read(Entry e)
    {
        var outp = new MemoryStream();
        using (var fs = File.OpenRead(Path))
        {
            fs.Position = e.Offset;
            long remaining = e.Size; int blk = (int)e.Block;
            while (remaining > 0)
            {
                int z = ZSizes[blk++];
                int want = (int)Math.Min(remaining, BlockSize);
                if (z == 0) z = (int)BlockSize;
                var buf = new byte[z]; ReadFull(fs, buf);
                // stored when the block is as big as its output, zlib otherwise
                if (z == want) outp.Write(buf, 0, buf.Length);
                else if (!TryInflate(buf, want, outp)) throw new InvalidDataException("bad zlib block " + (blk - 1));
                remaining -= want;
            }
        }
        return outp.ToArray();
    }

    static void ReadFull(Stream s, byte[] b) { int o = 0; while (o < b.Length) { int r = s.Read(b, o, b.Length - o); if (r <= 0) throw new EndOfStreamException(); o += r; } }

    static bool TryInflate(byte[] buf, int want, MemoryStream outp)
    {
        try
        {
            using (var ds = new DeflateStream(new MemoryStream(buf, 2, buf.Length - 2), CompressionMode.Decompress))
            {
                var tmp = new byte[want]; int o = 0;
                while (o < want) { int r = ds.Read(tmp, o, want - o); if (r <= 0) break; o += r; }
                if (o != want) return false;
                outp.Write(tmp, 0, want); return true;
            }
        }
        catch { return false; }
    }
}

class Program
{
    static string BackupPath(string archive, string dir) { return System.IO.Path.Combine(dir, System.IO.Path.GetFileName(archive) + ".tocbak"); }
    static void ReadAll(Stream s, byte[] b) { int o = 0; while (o < b.Length) { int r = s.Read(b, o, b.Length - o); if (r <= 0) throw new EndOfStreamException(); o += r; } }
    static void PutBE(byte[] b, int o, uint v, int n) { for (int i = 0; i < n; i++) b[o + i] = (byte)(v >> (8 * (n - 1 - i))); }
    static void PutBE40(byte[] b, int o, long v) { for (int i = 0; i < 5; i++) b[o + i] = (byte)(v >> (8 * (4 - i))); }

    // "@list.txt" anywhere in the arguments = one argument per non-empty line of that file (long replace lists)
    static string[] ExpandArgs(string[] a)
    {
        var l = new List<string>();
        foreach (var x in a)
        {
            if (x.StartsWith("@") && File.Exists(x.Substring(1)))
            {
                foreach (var line in File.ReadAllLines(x.Substring(1))) if (line.Trim().Length > 0) l.Add(line.Trim());
            }
            else l.Add(x);
        }
        return l.ToArray();
    }

    static int Main(string[] a)
    {
        a = ExpandArgs(a);
        if (a.Length == 0) { Console.WriteLine("psarc list|info|extract|replace|restore|redirect ... (ver cabecalho do psarc.cs; @arquivo = lista de argumentos)"); return 1; }
        if (a[0] == "restore")
        {
            var bak = BackupPath(a[1], a[2]);
            using (var br = new BinaryReader(File.OpenRead(bak)))
            using (var fs = new FileStream(a[1], FileMode.Open, FileAccess.ReadWrite))
            {
                long len = br.ReadInt64(); int hl = br.ReadInt32(); var head = br.ReadBytes(hl); fs.Position = 0; fs.Write(head, 0, hl);
                // in-place replacements saved the overwritten bytes as (offset, length, bytes) records; undo them newest first
                var patches = new List<KeyValuePair<long, byte[]>>();
                if (File.Exists(bak + ".patch"))
                {
                    using (var pr = new BinaryReader(File.OpenRead(bak + ".patch")))
                        while (pr.BaseStream.Position < pr.BaseStream.Length) { long off = pr.ReadInt64(); int n = pr.ReadInt32(); patches.Add(new KeyValuePair<long, byte[]>(off, pr.ReadBytes(n))); }
                    for (int i = patches.Count - 1; i >= 0; i--) { fs.Position = patches[i].Key; fs.Write(patches[i].Value, 0, patches[i].Value.Length); }
                    File.Delete(bak + ".patch");
                }
                fs.SetLength(len);
            }
            Console.WriteLine("restored " + a[1]); return 0;
        }
        var p = new Psarc(a[1]);
        if (a[0] == "info")
        {
            Console.WriteLine("toc={0} entsz={1} files={2} block={3} flags={4:X} zbytes={5} nz={6} matched={7} unmatched={8}", p.TocLen, p.EntrySize, p.NumFiles, p.BlockSize, p.Flags, p.ZBytes, p.ZSizes.Length, p.Matched, p.Unmatched);
            for (int i = 0; i < Math.Min(5, p.Entries.Count); i++) { var e = p.Entries[i]; Console.WriteLine("{0} blk={1} size={2} off={3} {4}", i, e.Block, e.Size, e.Offset, e.Name); }
        }
        else if (a[0] == "list")
        {
            for (int i = 1; i < p.Entries.Count; i++) { var e = p.Entries[i]; Console.WriteLine("{0}\t{1}\t{2}", i, e.Size, e.Name); }
        }
        else if (a[0] == "redirect")
        {
            // redirect <archive> target=source ... : target entries read source's data (resolved from the original TOC)
            var byName = new Dictionary<string, int>();
            for (int i = 1; i < p.Entries.Count; i++) byName[p.Entries[i].Name] = i;
            var plan = new List<int[]>();
            for (int k = 2; k < a.Length; k++)
            {
                var kv = a[k].Split('=');
                int ti, si;
                if (!byName.TryGetValue(kv[0], out ti) || !byName.TryGetValue(kv[1], out si)) { Console.WriteLine("not found: " + a[k]); return 1; }
                plan.Add(new[] { ti, si });
            }
            using (var fs = new FileStream(a[1], FileMode.Open, FileAccess.ReadWrite))
            {
                foreach (var ts in plan)
                {
                    var src = p.Entries[ts[1]];
                    var rec = new byte[14];
                    rec[0] = (byte)(src.Block >> 24); rec[1] = (byte)(src.Block >> 16); rec[2] = (byte)(src.Block >> 8); rec[3] = (byte)src.Block;
                    for (int b = 0; b < 5; b++) rec[4 + b] = (byte)(src.Size >> (8 * (4 - b)));
                    for (int b = 0; b < 5; b++) rec[9 + b] = (byte)(src.Offset >> (8 * (4 - b)));
                    fs.Position = 32 + ts[0] * p.EntrySize + 16;
                    fs.Write(rec, 0, 14);
                    Console.WriteLine("{0} <- {1}", p.Entries[ts[0]].Name, src.Name);
                }
            }
        }
        else if (a[0] == "replace")
        {
            // replace <archive> <backupdir> name=file ... : appends the files as stored blocks and points the entries at them
            // --inplace: when the new data fits in the entry's current slot (stored bytes and block count), overwrite it
            // there instead of appending; the overwritten bytes go to <backup>.patch so restore can undo them
            bool inplace = false;
            { var al = new List<string>(); foreach (var x in a) { if (x == "--inplace") inplace = true; else al.Add(x); } a = al.ToArray(); }
            var bak = BackupPath(a[1], a[2]);
            long minData = long.MaxValue;
            foreach (var e in p.Entries) if (e.Size > 0 && e.Offset < minData) minData = e.Offset;
            var byName = new Dictionary<string, int>();
            for (int i = 1; i < p.Entries.Count; i++) byName[p.Entries[i].Name] = i;
            var zs = new List<int>(p.ZSizes);
            Func<Entry, long> storedOf = e => { long t = 0; int nb = (int)((e.Size + p.BlockSize - 1) / p.BlockSize); for (int b = 0; b < nb; b++) { int z = zs[(int)e.Block + b]; t += z == 0 ? (long)p.BlockSize : (long)z; } return t; };
            Func<Entry, long, bool> fitsInPlace = (e, sz) => inplace && e.Size > 0 && sz <= storedOf(e) && (sz + p.BlockSize - 1) / p.BlockSize <= (e.Size + p.BlockSize - 1) / p.BlockSize;
            long extra = 0;
            int bad = 0;
            for (int k = 3; k < a.Length; k++)
            {
                int eqi = a[k].IndexOf('=');
                if (eqi < 0 || !byName.ContainsKey(a[k].Substring(0, eqi))) { Console.WriteLine("not found: " + a[k]); bad++; continue; }
                if (!File.Exists(a[k].Substring(eqi + 1))) { Console.WriteLine("missing file: " + a[k]); bad++; continue; }
                long sz = new FileInfo(a[k].Substring(eqi + 1)).Length;
                if (!fitsInPlace(p.Entries[byName[a[k].Substring(0, eqi)]], sz)) extra += (sz + p.BlockSize - 1) / p.BlockSize;
            }
            if (bad > 0) { Console.WriteLine(bad + " entries not found; nothing changed"); return 1; }
            // Not enough room between the TOC and the manifest? The game wants the manifest right after the TOC,
            // so: move the entries stored just after the manifest to the end of the file until there is room,
            // then shift the manifest forward to the end of the new TOC. The backup must cover everything up to
            // the new end of the manifest.
            var moves = new List<int>();
            long needMin = minData;
            long newTocLen = 32 + p.NumFiles * p.EntrySize + (zs.Count + extra) * p.ZBytes;
            bool shiftManifest = false; long manStored = 0, newManOff = 0;
            if (newTocLen > minData)
            {
                var man = p.Entries[0];
                if (man.Size == 0 || man.Offset != minData) { Console.WriteLine("not enough room after the TOC and the manifest is not the first entry; nothing changed"); return 1; }
                int nb0 = (int)((man.Size + p.BlockSize - 1) / p.BlockSize);
                for (int b = 0; b < nb0; b++) { int z = zs[(int)man.Block + b]; manStored += z == 0 ? (long)p.BlockSize : (long)z; }
                // keep the archive's own placement style: manifest exactly at the TOC end, or at the next
                // block boundary when the original was block-aligned (pakN: 64 KB)
                bool alignedStyle = (minData % p.BlockSize == 0) && (minData != p.TocLen);
                newManOff = alignedStyle ? ((newTocLen + p.BlockSize - 1) / p.BlockSize) * p.BlockSize : newTocLen;
                long needEnd = newManOff + manStored;
                for (int i = 1; i < p.Entries.Count; i++)
                {
                    var e = p.Entries[i];
                    if (e.Size > 0 && e.Offset >= minData && e.Offset < needEnd) moves.Add(i);
                }
                shiftManifest = true; needMin = needEnd;
            }
            long haveHead = 0;
            if (File.Exists(bak))
            {
                using (var br = new BinaryReader(File.OpenRead(bak))) { br.ReadInt64(); haveHead = br.ReadInt32(); }
            }
            if (!File.Exists(bak) || haveHead < needMin)
            {
                // create the backup, or extend it: the bytes between the old head and needMin are still pristine
                // (earlier replaces only rewrote the TOC region and appended at the end)
                long origLen; byte[] oldHead = new byte[0];
                if (File.Exists(bak))
                {
                    using (var br = new BinaryReader(File.OpenRead(bak))) { origLen = br.ReadInt64(); int hl = br.ReadInt32(); oldHead = br.ReadBytes(hl); }
                }
                else origLen = new FileInfo(a[1]).Length;
                using (var fs = File.OpenRead(a[1]))
                using (var bw = new BinaryWriter(File.Create(bak)))
                {
                    var head = new byte[needMin];
                    Array.Copy(oldHead, head, oldHead.Length);
                    fs.Position = oldHead.Length; var rest = new byte[needMin - oldHead.Length]; ReadAll(fs, rest);
                    Array.Copy(rest, 0, head, oldHead.Length, rest.Length);
                    bw.Write(origLen); bw.Write(head.Length); bw.Write(head);
                }
                Console.WriteLine((oldHead.Length > 0 ? "backup extended: " : "backup: ") + bak + " (" + needMin + " bytes)");
            }
            using (var fs = new FileStream(a[1], FileMode.Open, FileAccess.ReadWrite))
            {
                foreach (int fi in moves)
                {
                    var fe = p.Entries[fi];
                    long stored = 0; int nblk = (int)((fe.Size + p.BlockSize - 1) / p.BlockSize);
                    for (int b = 0; b < nblk; b++) { int z = zs[(int)fe.Block + b]; stored += z == 0 ? (long)p.BlockSize : (long)z; }
                    var data = new byte[stored]; fs.Position = fe.Offset; ReadAll(fs, data);
                    long noff = (fs.Length + 15) & ~15L;
                    fs.Position = noff; fs.Write(data, 0, data.Length);
                    var rec = new byte[5]; PutBE40(rec, 0, noff);
                    fs.Position = 32 + fi * p.EntrySize + 16 + 9; fs.Write(rec, 0, 5);
                    Console.WriteLine("moved " + fe.Name + " (" + stored + " bytes, was at " + fe.Offset + ") to " + noff + " to make room for the TOC");
                    fe.Offset = noff;
                }
                if (shiftManifest)
                {
                    var man = p.Entries[0];
                    var data = new byte[manStored]; fs.Position = man.Offset; ReadAll(fs, data);
                    fs.Position = newManOff; fs.Write(data, 0, data.Length);
                    var rec = new byte[5]; PutBE40(rec, 0, newManOff);
                    fs.Position = 32 + 0 * p.EntrySize + 16 + 9; fs.Write(rec, 0, 5);
                    Console.WriteLine("manifest shifted from " + man.Offset + " to " + newManOff + " (" + manStored + " bytes), " + moves.Count + " entries moved to the end");
                    man.Offset = newManOff;
                    minData = newManOff;
                }
                for (int k = 3; k < a.Length; k++)
                {
                    int eq = a[k].IndexOf('=');
                    string name = a[k].Substring(0, eq), file = a[k].Substring(eq + 1);
                    int idx;
                    if (!byName.TryGetValue(name, out idx)) { Console.WriteLine("not found: " + name); return 1; }
                    var data = File.ReadAllBytes(file);
                    long off; int first;
                    var ent = p.Entries[idx];
                    if (fitsInPlace(ent, data.Length))
                    {
                        long oldStored = storedOf(ent);
                        var old = new byte[oldStored]; fs.Position = ent.Offset; ReadAll(fs, old);
                        using (var pw = new BinaryWriter(new FileStream(bak + ".patch", FileMode.Append, FileAccess.Write))) { pw.Write((long)ent.Offset); pw.Write(old.Length); pw.Write(old); }
                        off = ent.Offset; first = (int)ent.Block;
                        fs.Position = off;
                        int bi = first;
                        for (int o = 0; o < data.Length; o += (int)p.BlockSize)
                        {
                            int len = (int)Math.Min(p.BlockSize, data.Length - o);
                            fs.Write(data, o, len);
                            zs[bi++] = len == p.BlockSize ? 0 : len;
                        }
                        Console.WriteLine("in place at " + off + " (slot " + oldStored + " bytes)");
                    }
                    else
                    {
                        off = (fs.Length + 15) & ~15L;
                        fs.Position = off;
                        first = zs.Count;
                        for (int o = 0; o < data.Length; o += (int)p.BlockSize)
                        {
                            int len = (int)Math.Min(p.BlockSize, data.Length - o);
                            fs.Write(data, o, len);
                            zs.Add(len == p.BlockSize ? 0 : len);   // stored blocks: 0 = full block, else the raw length
                        }
                    }
                    ent.Size = data.Length; ent.Offset = off; ent.Block = (uint)first;
                    var rec = new byte[14];
                    PutBE(rec, 0, (uint)first, 4); PutBE40(rec, 4, data.Length); PutBE40(rec, 9, off);
                    fs.Position = 32 + idx * p.EntrySize + 16; fs.Write(rec, 0, 14);
                    Console.WriteLine("{0} <- {1} ({2} bytes)", name, file, data.Length);
                }
                long newToc = 32 + p.NumFiles * p.EntrySize + zs.Count * p.ZBytes;
                if (newToc > minData) { Console.WriteLine("TOC would overlap data, aborting (restore from backup)"); return 1; }
                fs.Position = 32 + p.NumFiles * p.EntrySize;
                var zb = new byte[zs.Count * p.ZBytes];
                for (int i = 0; i < zs.Count; i++) PutBE(zb, i * p.ZBytes, (uint)zs[i], p.ZBytes);
                fs.Write(zb, 0, zb.Length);
                var tl = new byte[4]; PutBE(tl, 0, (uint)newToc, 4);
                fs.Position = 12; fs.Write(tl, 0, 4);
            }
        }
        else if (a[0] == "extract")
        {
            var rx = new Regex(a[3], RegexOptions.IgnoreCase); int n = 0;
            for (int i = 1; i < p.Entries.Count; i++)
            {
                var e = p.Entries[i]; if (!rx.IsMatch(e.Name)) continue;
                var rel = e.Name.TrimStart('/', '$').Replace('/', '\\');
                var dst = System.IO.Path.Combine(a[2], rel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                File.WriteAllBytes(dst, p.Read(e)); n++;
            }
            Console.WriteLine("extracted " + n);
        }
        return 0;
    }
}

