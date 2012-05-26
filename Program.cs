using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Mono.Options;
using reWZ;
using reWZ.WZProperties;

namespace WZ2NX
{
    internal static class Program
    {
        private static readonly byte[] PKG2 = {0x50, 0x4B, 0x47, 0x32}; // PKG2

        private static void Main(string[] args)
        {
            string inWz = null, outPath = null;
            WZVariant wzVar = (WZVariant)255;
            bool dumpImg = false, dumpSnd = false, initialEnc = true;
            OptionSet oSet = new OptionSet();
            oSet.Add("in=", "Path to input WZ; required.", a => inWz = a);
            oSet.Add("out=", "Path to output NX; optional, defaults to <WZ file name>.nx in this directory", a => outPath = a);
            oSet.Add("wzv=", "WZ encryption key; required.", a => wzVar = (WZVariant)Enum.Parse(typeof(WZVariant), a, true));
            oSet.Add("Ds|dumpsound", "Set to include sound properties in the NX file.", a => dumpSnd = true);
            oSet.Add("Di|dumpimage", "Set to include canvas properties in the NX file.", a => dumpImg = true);
            oSet.Add("wzn", "Set if the WZ is not encrypted.", a => initialEnc = false);
            oSet.Parse(args);

            if (inWz == null || wzVar == (WZVariant)255) {
                oSet.WriteOptionDescriptions(Console.Out);
                return;
            }
            if (outPath == null)
                outPath = Path.GetFileNameWithoutExtension(inWz) + ".nx";
            Console.WriteLine("Input .wz: {0}{1}Output .nx: {2}", Path.GetFullPath(inWz), Environment.NewLine, Path.GetFullPath(outPath));
            Stopwatch sw2 = new Stopwatch();
            sw2.Start();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            using (WZFile inFile = new WZFile(inWz, wzVar, initialEnc, WZReadSelection.EagerParseStrings | WZReadSelection.EagerParseImage))
            using (FileStream outFile = File.Open(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None)) {
                sw.Stop();
                Console.WriteLine("Parsed input WZ in {0}", sw.Elapsed);
                sw.Reset();
                sw.Start();
                uint nodesCount = 0, stringsCount = 0, bitmapCount = 0, soundCount = 0;
                NodeCount(inFile.MainDirectory, ref nodesCount, ref stringsCount, ref bitmapCount, ref soundCount);
                sw.Stop();
                Console.WriteLine("N{0}, S{1}, B{2}, M{3}", nodesCount, stringsCount, bitmapCount, soundCount);
                Console.WriteLine("Counted nodes in {0}", sw.Elapsed);
                sw.Reset();
                BinaryWriter bw = new BinaryWriter(outFile);

                bw.Write(PKG2);
                bw.Write(new byte[12*4]);

                sw.Start();
                Dictionary<String, uint> stringDict = WriteStrings(inFile, bw);
                sw.Stop();
                Console.WriteLine("Wrote strings in {0}", sw.Elapsed);
                sw.Reset();
                sw.Start();
                Dictionary<WZCanvasProperty, uint> bDict = dumpImg ? WriteBitmaps(inFile, bw) : null;
                sw.Stop();
                Console.WriteLine("Wrote bitmaps in {0}", sw.Elapsed);
                sw.Reset();
                sw.Start();
                Dictionary<WZMP3Property, uint> mDict = dumpSnd ? WriteMP3s(inFile, bw) : null;
                sw.Stop();
                Console.WriteLine("Wrote MP3s in {0}", sw.Elapsed);
                sw.Reset();

                // now write nodes!

                long nPos = bw.BaseStream.Position;
                Console.WriteLine("N POS{0}", nPos);
                bw.Seek(4, SeekOrigin.Begin);
                bw.Write(nodesCount);
                bw.Write((ulong)nPos);
                bw.BaseStream.Position = nPos;

                sw.Start();
                HashSet<WZObject> ooled = new HashSet<WZObject>();
                GetUOLedNodes(inFile.MainDirectory, ooled);

                Dictionary<WZObject, uint> uoledid = new Dictionary<WZObject, uint>();
                Dictionary<WZUOLProperty, long> uoloffsetoffet = new Dictionary<WZUOLProperty, long>();
                uint asddfa = 0;
                WriteNode(inFile.MainDirectory, bw, stringDict, mDict, bDict, ooled, ref uoledid, ref uoloffsetoffet, ref asddfa);

                foreach(KeyValuePair<WZUOLProperty, long> fad in uoloffsetoffet) {
                    bw.BaseStream.Position = fad.Value;
                    bw.Write(uoledid[fad.Key.ResolveFully()]);
                }

                sw.Stop();
                Console.WriteLine("Wrote nodes in {0}", sw.Elapsed);
                sw2.Stop();
            }
            Console.WriteLine("Total time taken: {0}", sw2.Elapsed);
            Console.ReadLine();
        }

        private static void WriteNode(WZObject n, BinaryWriter bw, Dictionary<String, uint> strings, Dictionary<WZMP3Property, uint> mp3s, Dictionary<WZCanvasProperty, uint> canvases, HashSet<WZObject> uoled, ref Dictionary<WZObject, uint> uoledoffsets, ref Dictionary<WZUOLProperty, long> uols, ref uint nids)
        {
            if (uoled.Contains(n))
                uoledoffsets.Add(n, nids);
            ++nids;
            bw.Write(strings[n.Name]);
            ushort cc = (ushort)n.ChildCount;
            byte type = (byte)(cc > 0 ? 0x80 : 0);
            bool brokenUol = false;
            if (n is WZUInt16Property || n is WZInt32Property)
                type |= 1;
            else if (n is WZSingleProperty || n is WZDoubleProperty)
                type |= 2;
            else if (n is WZStringProperty)
                type |= 3;
            else if (n is WZPointProperty)
                type |= 4;
            else if (n is WZCanvasProperty)
                type |= 5;
            else if (n is WZMP3Property)
                type |= 6;
            else if (n is WZUOLProperty) {
                try {
                    ((WZUOLProperty)n).ResolveFully();
                }
                catch(KeyNotFoundException) {
                    brokenUol = true;
                }
                if (!brokenUol) type |= 7;
            }

            bw.Write(type);

            if (n is WZUInt16Property)
                bw.Write((int)n.ValueOrDie<ushort>());
            else if (n is WZInt32Property)
                bw.Write(n.ValueOrDie<int>());
            else if (n is WZSingleProperty)
                bw.Write((double)n.ValueOrDie<Single>());
            else if (n is WZDoubleProperty)
                bw.Write(n.ValueOrDie<Double>());
            else if (n is WZStringProperty)
                bw.Write(strings[n.ValueOrDie<String>()]);
            else if (n is WZPointProperty) {
                WZPointProperty v = (WZPointProperty)n;
                bw.Write(v.Value.X);
                bw.Write(v.Value.Y);
            } else if (!brokenUol && n is WZUOLProperty) {
                uols.Add((WZUOLProperty)n, bw.BaseStream.Position);
                bw.Write(0U);
            } else if (n is WZCanvasProperty)
                bw.Write(canvases == null ? 0U : canvases[(WZCanvasProperty)n]);
            else if (n is WZMP3Property)
                bw.Write(mp3s == null ? 0U : mp3s[(WZMP3Property)n]);

            if (cc <= 0) return;
            bw.Write(cc);
            foreach (WZObject c in n)
                WriteNode(c, bw, strings, mp3s, canvases, uoled, ref uoledoffsets, ref uols, ref nids);
        }

        private static Dictionary<String, uint> WriteStrings(WZFile infile, BinaryWriter bw)
        {
            List<String> strs = new List<string>(GetStrings(infile));

            long strPos = bw.BaseStream.Position;
            Console.WriteLine("S POS{0}", strPos);
            bw.Seek(16, SeekOrigin.Begin);
            bw.Write((uint)strs.Count);
            bw.Write((ulong)strPos);
            bw.BaseStream.Position = strPos;
            uint strId = 0;
            Dictionary<String, uint> ret = new Dictionary<string, uint>(strs.Count, StringComparer.Ordinal);
            foreach (string s in strs) {
                string r = s;
                ret.Add(s, strId++);
                byte[] utf = Encoding.UTF8.GetBytes(r);
                bw.Write((ushort)utf.Length);
                bw.Write(utf);
            }
            return ret;
        }

        private static Dictionary<WZCanvasProperty, uint> WriteBitmaps(WZFile infile, BinaryWriter bw)
        {
            HashSet<WZCanvasProperty> cs = new HashSet<WZCanvasProperty>();
            GetCanvasNodes(infile.MainDirectory, cs);
            List<ulong> offsets = new List<ulong>();
            Dictionary<WZCanvasProperty, uint> cids =  new Dictionary<WZCanvasProperty, uint>();
            uint id = 0;
            foreach(WZCanvasProperty c in cs) {
                cids.Add(c, id++);
                offsets.Add((ulong)bw.BaseStream.Position);
                Bitmap b = c.Value;
                bw.Write((ushort)b.Width);
                bw.Write((ushort)b.Height);
                byte[] com = GetCompressedBitmap(b);
                c.Dispose();
                bw.Write((uint)com.Length);
                bw.Write(com);
            }
            long bmPos = bw.BaseStream.Position;
            bw.Seek(28, SeekOrigin.Begin);
            bw.Write((uint)cs.Count);
            bw.Write((ulong)bmPos);
            bw.BaseStream.Position = bmPos;
            foreach(ulong u in offsets) {
                bw.Write(u);
            }
            return cids;
        }

        private static Dictionary<WZMP3Property, uint> WriteMP3s(WZFile infile, BinaryWriter bw)
        {
            HashSet<WZMP3Property> mp3s = new HashSet<WZMP3Property>();
            GetMP3Nodes(infile.MainDirectory, mp3s);
            List<ulong> offsets = new List<ulong>();
            Dictionary<WZMP3Property, uint> mp3ids = new Dictionary<WZMP3Property, uint>();
            uint id = 0;
            foreach(WZMP3Property mp in mp3s) {
                mp3ids.Add(mp, id++);
                offsets.Add((ulong)bw.BaseStream.Position);
                byte[] ba = mp.Value;
                bw.Write((uint)ba.Length);
                bw.Write(ba);
                mp.Dispose();
            }
            long bmPos = bw.BaseStream.Position;
            bw.Seek(40, SeekOrigin.Begin);
            bw.Write((uint)mp3s.Count);
            bw.Write((ulong)bmPos);
            bw.BaseStream.Position = bmPos;
            foreach(ulong u in offsets) {
                bw.Write(u);
            }
            return mp3ids;
        }

        private static IEnumerable<string> GetStrings(WZFile f)
        {
            HashSet<String> r = new HashSet<string>(StringComparer.Ordinal) {string.Empty};
            foreach (WZObject c in f.MainDirectory)
                GetStrings(c, r);
            return r;
        }

        private static void GetStrings(WZObject f, HashSet<String> strs)
        {
            strs.Add(f.Name);
            if (f is WZStringProperty) strs.Add(f.ValueOrDie<String>());
            if (f.ChildCount < 1) return;
            foreach (WZObject c in f)
                GetStrings(c, strs);
        }

        private static void GetUOLedNodes(WZObject f, HashSet<WZObject> refObjs)
        {
            var wzuolProperty = f as WZUOLProperty;
            try {
                if (wzuolProperty != null) refObjs.Add((wzuolProperty).ResolveFully());
            } catch(KeyNotFoundException) {
                Console.WriteLine("UOL {0} has invalid link to {1}", f.Path, wzuolProperty.Value);
            }
            if (f.ChildCount < 1) return;
            foreach (WZObject c in f)
                GetUOLedNodes(c, refObjs);
        }

        private static void GetMP3Nodes(WZObject f, HashSet<WZMP3Property> refObjs)
        {
            if (f is WZMP3Property) refObjs.Add((WZMP3Property)f);
            else if(f.ChildCount > 0)
                foreach(WZObject c in f)
                    GetMP3Nodes(c, refObjs);
        }

        private static void GetCanvasNodes(WZObject f, HashSet<WZCanvasProperty> refObjs)
        {
            if (f is WZCanvasProperty) refObjs.Add((WZCanvasProperty)f);
            if (f.ChildCount > 0)
                foreach (WZObject c in f)
                    GetCanvasNodes(c, refObjs);
        }

        private static void NodeCount(WZObject f, ref uint nodeC, ref uint strC, ref uint bitmapC, ref uint sndC)
        {
			++nodeC;
			if (f is WZStringProperty) ++strC;
			else if (f is WZCanvasProperty) ++bitmapC;
			else if (f is WZMP3Property) ++sndC;
            if (f.ChildCount < 1) return;
            foreach (WZObject c in f) {
                NodeCount(c, ref nodeC, ref strC, ref bitmapC, ref sndC);
            }
        }

        private static byte[] GetCompressedBitmap(Bitmap b)
        {
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int inLen = bd.Stride*bd.Height;
            int outLen = EMaxOutputLen(inLen);
            IntPtr outBuf = Marshal.AllocHGlobal(outLen);
            outLen = ECompressLZ4(bd.Scan0, outBuf, inLen);
            byte[] @out = new byte[outLen];
            Marshal.Copy(outBuf, @out, 0, outLen);
            Marshal.FreeHGlobal(outBuf);
            b.UnlockBits(bd);
            return @out;
        }

#if WIN32
        [DllImport("lz4_32.dll", EntryPoint = "LZ4_compressHC")]
        private static extern int ECompressLZ4(IntPtr source, IntPtr dest, int inputLen);
#elif WIN64
        [DllImport("lz4_64.dll", EntryPoint = "LZ4_compressHC")]
        private static extern int ECompressLZ4(IntPtr source, IntPtr dest, int inputLen);
#else
#error No architecture selected!
#endif
        

#if WIN32
        [DllImport("lz4_32.dll", EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen(int inputLen);
#elif WIN64
        [DllImport("lz4_64.dll", EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen(int inputLen);
#else
#error No architecture selected!
#endif
        
    }
}