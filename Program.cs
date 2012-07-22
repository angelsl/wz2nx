using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using Mono.Options;
using reWZ;
using reWZ.WZProperties;

namespace WZ2NX
{
    internal static class Extensions
    {
        public static void Restart(this Stopwatch sw)
        {
            sw.Stop();
            sw.Reset();
            sw.Start();
        }
    }

    internal static class Program
    {
        private class DumpState
        {
            private readonly List<WZCanvasProperty> _canvases;
            private readonly List<WZMP3Property> _mp3s;
            private readonly Dictionary<WZObject, uint> _nodes;
            private readonly Dictionary<String, uint> _strings;
            private readonly Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>> _uols;

            public DumpState()
            {
                _canvases = new List<WZCanvasProperty>();
                _strings = new Dictionary<string, uint>(StringComparer.Ordinal) {{"", 0}};
                _mp3s = new List<WZMP3Property>();
                _uols = new Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>>();
                _nodes = new Dictionary<WZObject, uint>();
            }

            public List<WZCanvasProperty> Canvases
            {
                get { return _canvases; }
            }

            public Dictionary<string, uint> Strings
            {
                get { return _strings; }
            }

            public List<WZMP3Property> MP3s
            {
                get { return _mp3s; }
            }

            public Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>> UOLs
            {
                get { return _uols; }
            }

            public Dictionary<WZObject, uint> Nodes
            {
                get { return _nodes; }
            }

            public uint AddCanvas(WZCanvasProperty node)
            {
                uint ret = (uint)_canvases.Count;
                _canvases.Add(node);
                return ret;
            }

            public uint AddMP3(WZMP3Property node)
            {
                uint ret = (uint)_mp3s.Count;
                _mp3s.Add(node);
                return ret;
            }

            public uint AddString(string str)
            {
                if (_strings.ContainsKey(str))
                    return _strings[str];
                uint ret = (uint)_strings.Count;
                _strings.Add(str, ret);
                return ret;
            }

            public uint AddNode(WZObject node)
            {
                uint ret = (uint)_nodes.Count;
                _nodes.Add(node, ret);
                return ret;
            }

            public uint GetNodeID(WZObject node)
            {
                return _nodes[node];
            }

            public uint GetNextNodeID()
            {
                return (uint)_nodes.Count;
            }

            public void AddUOL(WZUOLProperty node, long currentPosition)
            {
                _uols.Add(node, (bw, data) => {
                                    bw.BaseStream.Position = currentPosition;
                                    bw.Write(data);
                                });
            }
        }

        private static readonly byte[] PKG3 = {0x50, 0x4B, 0x47, 0x33}; // PKG3

        private static void Main(string[] args)
        {
            #region Option parsing

            string inWz = null, outPath = null;
            WZVariant wzVar = (WZVariant)255;
            bool dumpImg = false, dumpSnd = false, initialEnc = true;
            OptionSet oSet = new OptionSet();
            oSet.Add("in=", "Path to input WZ; required.", a => inWz = a);
            oSet.Add("out=", "Path to output NX; optional, defaults to <WZ file name>.nx in this directory", a => outPath = a);
            oSet.Add("wzv=", "WZ encryption key; required.", a => wzVar = (WZVariant)Enum.Parse(typeof(WZVariant), a, true));
            oSet.Add("Ds|dumpsound", "Set to include sound properties in the NX file.", a => dumpSnd = true);
            oSet.Add("Di|dumpimage", "Set to include canvas properties in the NX file.", a => dumpImg = true);
            oSet.Add("wzn", "Set if the input WZ is not encrypted.", a => initialEnc = false);
            oSet.Parse(args);

            if (inWz == null || wzVar == (WZVariant)255) {
                oSet.WriteOptionDescriptions(Console.Out);
                return;
            }
            if (outPath == null)
                outPath = Path.GetFileNameWithoutExtension(inWz) + ".nx";

            #endregion

            Console.WriteLine("Input .wz: {0}{1}Output .nx: {2}", Path.GetFullPath(inWz), Environment.NewLine, Path.GetFullPath(outPath));
            Stopwatch swOperation = new Stopwatch();
            Stopwatch fullTimer = new Stopwatch();

            Action<string> reportDone = (string str) => { Console.WriteLine("done. E{0} T{1}", swOperation.Elapsed, fullTimer.Elapsed);
            swOperation.Restart(); Console.Write(str);};

            fullTimer.Start();
            swOperation.Start();
            Console.Write("Parsing input WZ... ".PadRight(31));

            WZReadSelection rFlags = WZReadSelection.EagerParseImage | WZReadSelection.EagerParseStrings;
            if(!dumpImg) rFlags |= WZReadSelection.NeverParseCanvas;

            using (WZFile wzf = new WZFile(inWz, wzVar, initialEnc, rFlags))
            using (FileStream outFs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (BinaryWriter bw = new BinaryWriter(outFs)) {
                DumpState state = new DumpState();

                reportDone("Writing header... ".PadRight(31));
                bw.Write(PKG3);
                bw.Write(new byte[(4 + 8)*4]);

                reportDone("Writing nodes... ".PadRight(31));
                ulong nodeOffset = (ulong)bw.BaseStream.Position;
                List<WZObject> nodeLevel = new List<WZObject> {wzf.MainDirectory};
                while(nodeLevel.Count > 0)
                    WriteNodeLevel(ref nodeLevel, state, bw);

                reportDone("Writing string data...".PadRight(31));
                ulong stringOffset = (ulong)bw.BaseStream.Position;
                uint stringCount = (uint)state.Strings.Count;
                Dictionary<uint, String> strings = state.Strings.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
                for(uint idx = 0; idx < stringCount; ++idx) {
                    WriteString(strings[idx], bw);
                }

                ulong bitmapOffset = 0UL;
                uint bitmapCount = 0U;
                if (dumpImg) {
                    reportDone("Writing canvas data...".PadRight(31));
                    bitmapCount = (uint)state.Canvases.Count;
                    List<ulong> offsets = new List<ulong>();
                    foreach (WZCanvasProperty cNode in state.Canvases) {
                        offsets.Add((ulong)bw.BaseStream.Position);
                        WriteBitmap(cNode, bw);
                    }
                    bitmapOffset = (ulong)bw.BaseStream.Position;
                    offsets.ForEach(bw.Write);
                }

                ulong soundOffset = 0UL;
                uint soundCount = 0U;
                if(dumpSnd) {
                    reportDone("Writing MP3 data... ".PadRight(31));
                    soundCount = (uint)state.MP3s.Count;
                    List<ulong> offsets = new List<ulong>();
                    foreach(WZMP3Property mNode in state.MP3s) {
                        offsets.Add((ulong)bw.BaseStream.Position);
                        WriteMP3(mNode, bw);
                    }
                    soundOffset = (ulong)bw.BaseStream.Position;
                    offsets.ForEach(bw.Write);
                }

                reportDone("Writing linked node data... ".PadRight(31));
                byte[] uolReplace = new byte[16];
                foreach(KeyValuePair<WZUOLProperty, Action<BinaryWriter, byte[]>> pair in state.UOLs) {
                    WZObject result = SafeResolveUOL(pair.Key);
                    if(result == null) continue;
                    bw.BaseStream.Position = (long)(nodeOffset + state.GetNodeID(result)*20 + 4);
                    bw.BaseStream.Read(uolReplace, 0, 16);
                    pair.Value(bw, uolReplace);
                }

                reportDone("Finalising... ".PadRight(31));

                bw.Seek(4, SeekOrigin.Begin);
                bw.Write((uint)state.Nodes.Count);
                bw.Write(nodeOffset);
                bw.Write(stringCount);
                bw.Write(stringOffset);
                bw.Write(bitmapCount);
                bw.Write(bitmapOffset);
                bw.Write(soundCount);
                bw.Write(soundOffset);

                reportDone("Completed!");
            }
            Console.ReadLine();
        }

        private static WZObject SafeResolveUOL(WZUOLProperty uol)
        {
            HashSet<WZObject> results = new HashSet<WZObject> {uol};

            WZObject ret = uol;
            try
            {
                WZUOLProperty rUol;
                while ((rUol = ret as WZUOLProperty) != null)
                {
                    ret = rUol.Resolve();
                    if (ret == null || results.Contains(ret)) return null;
                    results.Add(ret);
                }
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
            catch (NotSupportedException) {
                return null;
            }
            return ret;
        }

        private static void WriteNodeLevel(ref List<WZObject> nodeLevel, DumpState ds, BinaryWriter bw)
        {
            uint nextChildId = (uint)(ds.GetNextNodeID() + nodeLevel.Count); 
            foreach(WZObject levelNode in nodeLevel) {
                if(levelNode is WZUOLProperty)
                    WriteUOL((WZUOLProperty)levelNode, ds, bw);
                else WriteNode(levelNode, ds, bw, nextChildId);
                nextChildId += (uint)levelNode.ChildCount;
            }
            List<WZObject> @out = new List<WZObject>();
            foreach (WZObject levelNode in nodeLevel.Where(n => n.ChildCount > 0)) {
                @out.AddRange(levelNode);
            }
            nodeLevel.Clear();
            nodeLevel = @out;
        }

        private static void WriteUOL(WZUOLProperty node, DumpState ds, BinaryWriter bw)
        {
            ds.AddNode(node);
            bw.Write(ds.AddString(node.Name));
            ds.AddUOL(node, bw.BaseStream.Position);
            bw.Write(0L);
            bw.Write(0L);
        }

        private static void WriteNode(WZObject node, DumpState ds, BinaryWriter bw, uint nextChildID)
        {
            ds.AddNode(node);
            bw.Write(ds.AddString(node.Name));
            bw.Write((ushort)node.ChildCount);
            ushort type;

            if (node is WZDirectory || node is WZImage || node is WZSubProperty || node is WZConvexProperty || node is WZNullProperty)
                type = 0; // no data; children only (8)
            else if (node is WZInt32Property || node is WZUInt16Property)
                type = 1; // int32 (4)
            else if (node is WZSingleProperty || node is WZDoubleProperty)
                type = 2; // Double (0)
            else if (node is WZStringProperty)
                type = 3; // String (4)
            else if (node is WZPointProperty)
                type = 4; // (0)
            else if (node is WZCanvasProperty)
                type = 5; // (4)
            else if (node is WZMP3Property)
                type = 6; // (4)
            else
                throw new InvalidOperationException("Unhandled WZ node type [1]");

            bw.Write(type);

            if (node is WZInt32Property)
                bw.Write(((WZInt32Property)node).Value);
            else if (node is WZUInt16Property)
                bw.Write((int)((WZUInt16Property)node).Value);
            else if (node is WZSingleProperty)
                bw.Write((double)((WZSingleProperty)node).Value);
            else if (node is WZDoubleProperty)
                bw.Write(((WZDoubleProperty)node).Value);
            else if (node is WZStringProperty)
                bw.Write(ds.AddString(((WZStringProperty)node).Value));
            else if (node is WZPointProperty)
            {
                Point pNode = ((WZPointProperty)node).Value;
                bw.Write(pNode.X);
                bw.Write(pNode.Y);
            }
            else if (node is WZCanvasProperty)
                bw.Write(ds.AddCanvas((WZCanvasProperty)node));
            else if (node is WZMP3Property)
                bw.Write(ds.AddMP3((WZMP3Property)node));

            switch(type) {
                case 0:
                    bw.Write(0L);
                    break;
                case 1:
                case 3:
                case 5:
                case 6:
                    bw.Write(0);
                    break;
            }

            bw.Write(nextChildID);
        }

        private static void WriteString(string s, BinaryWriter bw)
        {
            byte[] toWrite = Encoding.UTF8.GetBytes(s);
            bw.Write((ushort)toWrite.Length);
            bw.Write(toWrite);
        }

        private static void WriteBitmap(WZCanvasProperty node, BinaryWriter bw)
        {
            Bitmap b = node.Value;
            bw.Write((ushort)b.Width);
            bw.Write((ushort)b.Height);

            byte[] compressed = GetCompressedBitmap(b);
            node.Dispose();
            b = null;

            bw.Write((uint)compressed.Length);
            bw.Write(compressed);
        }

        private static void WriteMP3(WZMP3Property node, BinaryWriter bw)
        {
            byte[] m = node.Value;
            bw.Write((uint)m.Length);
            bw.Write(m);
            node.Dispose();
            m = null;
        }

        private static byte[] GetCompressedBitmap(Bitmap b)
        {
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int inLen = bd.Stride*bd.Height;
            int outLen = EMaxOutputLen(inLen);
            byte[] outBuf = new byte[outLen];
            outLen = ECompressLZ4(bd.Scan0, outBuf, inLen);
            b.UnlockBits(bd);
            Array.Resize(ref outBuf, outLen);
            return outBuf;
        }

#if WIN32
        [DllImport("lz4_32.dll", EntryPoint = "LZ4_compressHC")]
        private static extern int ECompressLZ4(IntPtr source, byte[] dest, int inputLen);
#elif WIN64
        [DllImport("lz4_64.dll", EntryPoint = "LZ4_compressHC")]
        private static extern int ECompressLZ4(IntPtr source, byte[] dest, int inputLen);
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