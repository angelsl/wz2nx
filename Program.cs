// WZ2NX is copyright angelsl, 2011 to 2013 inclusive.
// 
// This file (Program.cs) is part of WZ2NX.
// 
// WZ2NX is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// WZ2NX is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with WZ2NX. If not, see <http://www.gnu.org/licenses/>.
// 
// Linking WZ2NX statically or dynamically with other modules
// is making a combined work based on WZ2NX. Thus, the terms and
// conditions of the GNU General Public License cover the whole combination.
// 
// As a special exception, the copyright holders of WZ2NX give you
// permission to link WZ2NX with independent modules to produce an
// executable, regardless of the license terms of these independent modules,
// and to copy and distribute the resulting executable under terms of your
// choice, provided that you also meet, for each linked independent module,
// the terms and conditions of the license of that module. An independent
// module is a module which is not derived from or based on WZ2NX.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Mono.Options;
using reWZ;
using reWZ.WZProperties;

namespace WZ2NX {
    internal static class Extensions {
        public static void Restart(this Stopwatch sw) {
            sw.Stop();
            sw.Reset();
            sw.Start();
        }
    }

    internal static class Program {
        private static readonly byte[] PKG4 = {0x50, 0x4B, 0x47, 0x34}; // PKG4
        private static readonly bool _is64bit = IntPtr.Size == 8;
        private static bool dumpImg, dumpSnd;

        private static void EnsureMultiple(this Stream s, int multiple) {
            var skip = (int)(multiple - (s.Position%multiple));
            if (skip == multiple) return;
            s.Write(new byte[skip], 0, skip);
        }

        private static void Main(string[] args) {
            #region Option parsing

            string inWz = null, outPath = null;
            var wzVar = (WZVariant)255;
            bool initialEnc = true;
            var oSet = new OptionSet();
            oSet.Add("in=", "Path to input WZ; required.", a => inWz = a);
            oSet.Add("out=", "Path to output NX; optional, defaults to <WZ file name>.nx in this directory",
                     a => outPath = a);
            oSet.Add("wzv=", "WZ encryption key; required.",
                     a => wzVar = (WZVariant)Enum.Parse(typeof (WZVariant), a, true));
            oSet.Add("Ds|dumpsound", "Set to include sound properties in the NX file.", a => dumpSnd = true);
            oSet.Add("Di|dumpimage", "Set to include canvas properties in the NX file.", a => dumpImg = true);
            oSet.Add("wzn", "Set if the input WZ is not encrypted.", a => initialEnc = false);
            oSet.Parse(args);

            if (inWz == null || wzVar == (WZVariant)255) {
                oSet.WriteOptionDescriptions(Console.Out);
                return;
            }
            if (outPath == null) outPath = Path.GetFileNameWithoutExtension(inWz) + ".nx";

            #endregion

            Action run = () => {
                             Console.WriteLine("Input .wz: {0}{1}Output .nx: {2}", Path.GetFullPath(inWz),
                                               Environment.NewLine, Path.GetFullPath(outPath));

                             var swOperation = new Stopwatch();
                             var fullTimer = new Stopwatch();

                             Action<string> reportDone = str => {
                                                             Console.WriteLine("done. E{0} T{1}", swOperation.Elapsed,
                                                                               fullTimer.Elapsed);
                                                             swOperation.Restart();
                                                             Console.Write(str);
                                                         };

                             fullTimer.Start();
                             swOperation.Start();
                             Console.Write("Parsing input WZ... ".PadRight(31));

                             WZReadSelection rFlags = WZReadSelection.EagerParseImage |
                                                      WZReadSelection.EagerParseStrings;
                             if (!dumpImg) rFlags |= WZReadSelection.NeverParseCanvas;

                             using (var wzf = new WZFile(inWz, wzVar, initialEnc, rFlags))
                             using (
                                 var outFs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite,
                                                            FileShare.None))
                             using (var bw = new BinaryWriter(outFs)) {
                                 var state = new DumpState();

                                 reportDone("Writing header... ".PadRight(31));
                                 bw.Write(PKG4);
                                 bw.Write(new byte[(4 + 8)*4]);

                                 reportDone("Writing nodes... ".PadRight(31));
                                 outFs.EnsureMultiple(4);
                                 var nodeOffset = (ulong)bw.BaseStream.Position;
                                 var nodeLevel = new List<WZObject> {wzf.MainDirectory};
                                 while (nodeLevel.Count > 0) WriteNodeLevel(ref nodeLevel, state, bw);

                                 ulong stringOffset;
                                 var stringCount = (uint)state.Strings.Count;
                                 {
                                     reportDone("Writing string data...".PadRight(31));
                                     Dictionary<uint, String> strings = state.Strings.ToDictionary(kvp => kvp.Value,
                                                                                                   kvp => kvp.Key);
                                     var offsets = new ulong[stringCount];
                                     for (uint idx = 0; idx < stringCount; ++idx) {
                                         outFs.EnsureMultiple(2);
                                         offsets[idx] = (ulong)bw.BaseStream.Position;
                                         WriteString(strings[idx], bw);
                                     }

                                     outFs.EnsureMultiple(8);
                                     stringOffset = (ulong)bw.BaseStream.Position;
                                     for (uint idx = 0; idx < stringCount; ++idx) bw.Write(offsets[idx]);
                                 }

                                 ulong bitmapOffset = 0UL;
                                 uint bitmapCount = 0U;
                                 if (dumpImg) {
                                     reportDone("Writing canvas data...".PadRight(31));
                                     bitmapCount = (uint)state.Canvases.Count;
                                     var offsets = new ulong[bitmapCount];
                                     long cId = 0;
                                     foreach (WZCanvasProperty cNode in state.Canvases) {
                                         outFs.EnsureMultiple(8);
                                         offsets[cId++] = (ulong)bw.BaseStream.Position;
                                         WriteBitmap(cNode, bw);
                                     }
                                     outFs.EnsureMultiple(8);
                                     bitmapOffset = (ulong)bw.BaseStream.Position;
                                     for (uint idx = 0; idx < bitmapCount; ++idx) bw.Write(offsets[idx]);
                                 }

                                 ulong soundOffset = 0UL;
                                 uint soundCount = 0U;
                                 if (dumpSnd) {
                                     reportDone("Writing MP3 data... ".PadRight(31));
                                     soundCount = (uint)state.MP3s.Count;
                                     var offsets = new ulong[soundCount];
                                     long cId = 0;
                                     foreach (WZAudioProperty mNode in state.MP3s) {
                                         outFs.EnsureMultiple(8);
                                         offsets[cId++] = (ulong)bw.BaseStream.Position;
                                         WriteMP3(mNode, bw);
                                     }
                                     outFs.EnsureMultiple(8);
                                     soundOffset = (ulong)bw.BaseStream.Position;
                                     for (uint idx = 0; idx < soundCount; ++idx) bw.Write(offsets[idx]);
                                 }

                                 reportDone("Writing linked node data... ".PadRight(31));
                                 var uolReplace = new byte[16];
                                 foreach (var pair in state.UOLs) {
                                     WZObject result = SafeResolveUOL(pair.Key);
                                     if (result == null) continue;
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
                         };
            try {
                run();
            } catch (Exception e) {
                Console.WriteLine(e);
                Console.WriteLine("Exception; toggling /wzn and retrying.");
                initialEnc = !initialEnc;
                run();
            }
        }

        private static WZObject SafeResolveUOL(WZUOLProperty uol) {
            var results = new HashSet<WZObject> {uol};

            WZObject ret = uol;
            try {
                WZUOLProperty rUol;
                while ((rUol = ret as WZUOLProperty) != null) {
                    ret = rUol.Resolve();
                    if (ret == null || results.Contains(ret)) return null;
                    results.Add(ret);
                }
            } catch (KeyNotFoundException) {
                return null;
            } catch (NotSupportedException) {
                return null;
            }
            return ret;
        }

        private static void WriteNodeLevel(ref List<WZObject> nodeLevel, DumpState ds, BinaryWriter bw) {
            var nextChildId = (uint)(ds.GetNextNodeID() + nodeLevel.Count);
            foreach (WZObject levelNode in nodeLevel) {
                if (levelNode is WZUOLProperty) WriteUOL((WZUOLProperty)levelNode, ds, bw);
                else WriteNode(levelNode, ds, bw, nextChildId);
                nextChildId += (uint)levelNode.ChildCount;
            }
            var @out = new List<WZObject>();
            foreach (WZObject levelNode in nodeLevel.Where(n => n.ChildCount > 0)) @out.AddRange(levelNode.OrderBy(f => f.Name, StringComparer.Ordinal));
            nodeLevel.Clear();
            nodeLevel = @out;
        }

        private static void WriteUOL(WZUOLProperty node, DumpState ds, BinaryWriter bw) {
            ds.AddNode(node);
            bw.Write(ds.AddString(node.Name));
            ds.AddUOL(node, bw.BaseStream.Position);
            bw.Write(0L);
            bw.Write(0L);
        }

        private static void WriteNode(WZObject node, DumpState ds, BinaryWriter bw, uint nextChildID) {
            ds.AddNode(node);
            bw.Write(ds.AddString(node.Name));
            bw.Write(nextChildID);
            bw.Write((ushort)node.ChildCount);
            ushort type;

            if (node is WZDirectory || node is WZImage || node is WZSubProperty || node is WZConvexProperty ||
                node is WZNullProperty) type = 0; // no data; children only (8)
            else if (node is WZInt32Property || node is WZUInt16Property || node is WZInt64Property) type = 1; // int32 (4)
            else if (node is WZSingleProperty || node is WZDoubleProperty) type = 2; // Double (0)
            else if (node is WZStringProperty) type = 3; // String (4)
            else if (node is WZPointProperty) type = 4; // (0)
            else if (node is WZCanvasProperty) type = 5; // (4)
            else if (node is WZAudioProperty) type = 6; // (4)
            else throw new InvalidOperationException("Unhandled WZ node type [1]");

            bw.Write(type);

            if (node is WZInt32Property) bw.Write((long)((WZInt32Property)node).Value);
            else if (node is WZUInt16Property) bw.Write((long)((WZUInt16Property)node).Value);
            else if (node is WZInt64Property) bw.Write(((WZInt64Property)node).Value);
            else if (node is WZSingleProperty) bw.Write((double)((WZSingleProperty)node).Value);
            else if (node is WZDoubleProperty) bw.Write(((WZDoubleProperty)node).Value);
            else if (node is WZStringProperty) bw.Write(ds.AddString(((WZStringProperty)node).Value));
            else if (node is WZPointProperty) {
                Point pNode = ((WZPointProperty)node).Value;
                bw.Write(pNode.X);
                bw.Write(pNode.Y);
            } else if (node is WZCanvasProperty) {
                var wzcp = (WZCanvasProperty)node;
                bw.Write(ds.AddCanvas(wzcp));
                if (dumpImg) {
                    bw.Write((ushort)wzcp.Value.Width);
                    bw.Write((ushort)wzcp.Value.Height);
                    wzcp.Dispose();
                } else bw.Write(0);
            } else if (node is WZAudioProperty) {
                var wzmp = (WZAudioProperty)node;
                bw.Write(ds.AddMP3(wzmp));
                if (dumpSnd) {
                    bw.Write((uint)wzmp.Value.Length);
                    wzmp.Dispose();
                } else bw.Write(0);
            }
            switch (type) {
                case 0:
                    bw.Write(0L);
                    break;
                case 3:
                    bw.Write(0);
                    break;
            }
        }

        private static void WriteString(string s, BinaryWriter bw) {
            byte[] toWrite = Encoding.UTF8.GetBytes(s);
            bw.Write((ushort)toWrite.Length);
            bw.Write(toWrite);
        }

        private static void WriteBitmap(WZCanvasProperty node, BinaryWriter bw) {
            Bitmap b = node.Value;

            byte[] compressed = GetCompressedBitmap(b);
            node.Dispose();
            b = null;

            bw.Write((uint)compressed.Length);
            bw.Write(compressed);
        }

        private static void WriteMP3(WZAudioProperty node, BinaryWriter bw) {
            byte[] m = node.Value;
            bw.Write(m);
            node.Dispose();
            m = null;
        }

        private static byte[] GetCompressedBitmap(Bitmap b) {
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
                                       PixelFormat.Format32bppArgb);
            int inLen = bd.Stride*bd.Height;
            int outLen = _is64bit ? EMaxOutputLen64(inLen) : EMaxOutputLen32(inLen);
            var outBuf = new byte[outLen];
            outLen = _is64bit ? ECompressLZ464(bd.Scan0, outBuf, inLen) : ECompressLZ432(bd.Scan0, outBuf, inLen);
            b.UnlockBits(bd);
            Array.Resize(ref outBuf, outLen);
            return outBuf;
        }

        [DllImport("lz4_32.dll", EntryPoint = "LZ4_compressHC")]
        private static extern int ECompressLZ432(IntPtr source, byte[] dest, int inputLen);

        [DllImport("lz4_64.dll", EntryPoint = "LZ4_compressHC")]
        private static extern int ECompressLZ464(IntPtr source, byte[] dest, int inputLen);

        [DllImport("lz4_32.dll", EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen32(int inputLen);

        [DllImport("lz4_64.dll", EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen64(int inputLen);

        private sealed class DumpState {
            private readonly List<WZCanvasProperty> _canvases;
            private readonly List<WZAudioProperty> _mp3s;
            private readonly Dictionary<WZObject, uint> _nodes;
            private readonly Dictionary<String, uint> _strings;
            private readonly Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>> _uols;

            public DumpState() {
                _canvases = new List<WZCanvasProperty>();
                _strings = new Dictionary<string, uint>(StringComparer.Ordinal) {{"", 0}};
                _mp3s = new List<WZAudioProperty>();
                _uols = new Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>>();
                _nodes = new Dictionary<WZObject, uint>();
            }

            public List<WZCanvasProperty> Canvases {
                get { return _canvases; }
            }

            public Dictionary<string, uint> Strings {
                get { return _strings; }
            }

            public List<WZAudioProperty> MP3s {
                get { return _mp3s; }
            }

            public Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>> UOLs {
                get { return _uols; }
            }

            public Dictionary<WZObject, uint> Nodes {
                get { return _nodes; }
            }

            public uint AddCanvas(WZCanvasProperty node) {
                var ret = (uint)_canvases.Count;
                _canvases.Add(node);
                return ret;
            }

            public uint AddMP3(WZAudioProperty node) {
                var ret = (uint)_mp3s.Count;
                _mp3s.Add(node);
                return ret;
            }

            public uint AddString(string str) {
                if (_strings.ContainsKey(str)) return _strings[str];
                var ret = (uint)_strings.Count;
                _strings.Add(str, ret);
                return ret;
            }

            public void AddNode(WZObject node) {
                var ret = (uint)_nodes.Count;
                _nodes.Add(node, ret);
            }

            public uint GetNodeID(WZObject node) {
                return _nodes[node];
            }

            public uint GetNextNodeID() {
                return (uint)_nodes.Count;
            }

            public void AddUOL(WZUOLProperty node, long currentPosition) {
                _uols.Add(node, (bw, data) => {
                                    bw.BaseStream.Position = currentPosition;
                                    bw.Write(data);
                                });
            }
        }
    }
}
