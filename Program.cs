// WZ2NX is copyright angelsl, 2011 to 2015 inclusive.
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
        private static readonly byte[] PKG5 = {0x50, 0x4B, 0x47, 0x35}; // PKG5
        private static readonly byte[] WZBM = {0x57, 0x5A, 0x42, 0x4D}; // WZBM
        private static readonly byte[] WZAU = {0x57, 0x5A, 0x41, 0x55}; // WZAU
        private static readonly byte[] HeaderWZMagic = {0x84, 0x41};

        private static readonly bool _is64bit = IntPtr.Size == 8;

        private static readonly Stopwatch _swOperation = new Stopwatch();
        private static readonly Stopwatch _fullTimer = new Stopwatch();

        private static bool _dumpImg, _dumpSnd;

        private static void EnsureMultiple(this Stream s, int multiple) {
            int skip = (int) (multiple - (s.Position%multiple));
            if (skip == multiple)
                return;
            s.Write(new byte[skip], 0, skip);
        }

        private static void Main(string[] args) {
            #region Option parsing

            string inWz = null, outPath = null;
            WZVariant wzVar = (WZVariant) 255;
            bool initialEnc = true;
            OptionSet oSet = new OptionSet {
                {"in=", "Path to input WZ; required.", a => inWz = a}, {
                    "out=", "Path to output NX; optional, defaults to <WZ file name>.nx in this directory",
                    a => outPath = a
                }, {
                    "wzv=", "WZ encryption key; required.",
                    a => wzVar = (WZVariant) Enum.Parse(typeof (WZVariant), a, true)
                },
                {"Ds|dumpsound", "Set to include sound properties in the NX file.", a => _dumpSnd = true},
                {"Di|dumpimage", "Set to include canvas properties in the NX file.", a => _dumpImg = true},
                {"wzn", "Set if the input WZ is not encrypted.", a => initialEnc = false}
            };
            oSet.Parse(args);

            if (inWz == null || wzVar == (WZVariant) 255) {
                oSet.WriteOptionDescriptions(Console.Out);
                return;
            }
            if (outPath == null)
                outPath = Path.GetFileNameWithoutExtension(inWz) + ".nx";

            #endregion

            try {
                Run(inWz, outPath, wzVar, initialEnc);
            } catch (Exception e) {
                Console.WriteLine(e);
                Console.WriteLine("Exception; toggling /wzn and retrying.");
                initialEnc = !initialEnc;
                Run(inWz, outPath, wzVar, initialEnc);
            }
        }

        private static void ReportTime(string nextTask) {
            Console.WriteLine("done. E{0} T{1}", _swOperation.Elapsed,
                _fullTimer.Elapsed);
            _swOperation.Restart();
            Console.Write("{0,-31}", nextTask);
        }

        private static void Run(string inWz, string outPath, WZVariant wzVar, bool initialEnc) {
            Console.WriteLine("Input .wz: {0}{1}Output .nx: {2}", Path.GetFullPath(inWz),
                Environment.NewLine, Path.GetFullPath(outPath));

            _fullTimer.Start();
            _swOperation.Start();
            Console.Write("Parsing input WZ... ".PadRight(31));

            WZReadSelection rFlags = WZReadSelection.EagerParseImage |
                                     WZReadSelection.EagerParseStrings;
            if (!_dumpImg)
                rFlags |= WZReadSelection.NeverParseCanvas;

            using (WZFile wzf = new WZFile(inWz, wzVar, initialEnc, rFlags))
            using (
                FileStream outFs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite,
                    FileShare.None))
            using (BinaryWriter bw = new BinaryWriter(outFs)) {
                DumpState state = new DumpState();

                state.AddBlob(null);

                ReportTime("Writing header...");
                bw.Write(PKG5);
                bw.Write(new byte[(4 + 8)*3]);
                bw.Write(HeaderWZMagic);

                ReportTime("Writing nodes...");
                outFs.EnsureMultiple(4);
                ulong nodeOffset = (ulong) bw.BaseStream.Position;
                List<WZObject> nodeLevel = new List<WZObject> {wzf.MainDirectory};
                while (nodeLevel.Count > 0)
                    WriteNodeLevel(ref nodeLevel, state, bw);

                ulong stringOffset;
                uint stringCount = (uint) state.Strings.Count;
                {
                    ReportTime("Writing string data...");
                    Dictionary<uint, string> strings = state.Strings.ToDictionary(kvp => kvp.Value,
                        kvp => kvp.Key);
                    ulong[] offsets = new ulong[stringCount];
                    for (uint idx = 0; idx < stringCount; ++idx) {
                        outFs.EnsureMultiple(2);
                        offsets[idx] = (ulong) bw.BaseStream.Position;
                        WriteString(strings[idx], bw);
                    }

                    outFs.EnsureMultiple(8);
                    stringOffset = (ulong) bw.BaseStream.Position;
                    for (uint idx = 0; idx < stringCount; ++idx)
                        bw.Write(offsets[idx]);
                }

                ulong baOffset;
                uint baCount = (uint) state.Blobs.Count;
                {
                    ReportTime("Writing bitmap and sound data...");
                    ulong[] offsets = new ulong[stringCount];
                    List<WZObject> blobs = state.Blobs;
                    for (int idx = 0; idx < baCount; ++idx) {
                        outFs.EnsureMultiple(8);
                        WZObject bNode = blobs[idx];
                        offsets[idx] = (ulong) bw.BaseStream.Position;
                        if (bNode == null) {
                            bw.Write(new byte[34]);
                            continue;
                        }

                        switch (bNode.Type) {
                            case WZObjectType.Canvas:
                                WriteBitmap((WZCanvasProperty) bNode, bw);
                                break;
                            case WZObjectType.Audio:
                                WriteMP3((WZAudioProperty) bNode, bw);
                                break;
                            default:
                                throw new InvalidOperationException($"Invalid blob type {bNode.Type}");
                        }
                    }

                    outFs.EnsureMultiple(8);
                    baOffset = (ulong) bw.BaseStream.Position;
                    for (uint idx = 0; idx < stringCount; ++idx)
                        bw.Write(offsets[idx]);
                }

                ReportTime("Writing linked node data...");
                byte[] uolReplace = new byte[16];
                foreach (KeyValuePair<WZUOLProperty, Action<BinaryWriter, byte[]>> pair in state.UOLs) {
                    WZObject result = pair.Key.FinalTarget;
                    if (result == null)
                        continue;
                    bw.BaseStream.Position = (long) (nodeOffset + state.GetNodeID(result)*20 + 4);
                    bw.BaseStream.Read(uolReplace, 0, 16);
                    pair.Value(bw, uolReplace);
                }

                ReportTime("Finalising...");

                bw.Seek(4, SeekOrigin.Begin);
                bw.Write((uint) state.Nodes.Count);
                bw.Write(nodeOffset);
                bw.Write(stringCount);
                bw.Write(stringOffset);
                bw.Write(baCount);
                bw.Write(baOffset);

                ReportTime("Completed!");
            }
        }

        private static void WriteNodeLevel(ref List<WZObject> nodeLevel, DumpState ds, BinaryWriter bw) {
            uint nextChildId = (uint) (ds.GetNextNodeID() + nodeLevel.Count);
            foreach (WZObject levelNode in nodeLevel) {
                if (levelNode.Type == WZObjectType.UOL)
                    WriteUOL((WZUOLProperty) levelNode, ds, bw);
                else
                    WriteNode(levelNode, ds, bw, nextChildId);
                nextChildId += (uint) levelNode.ChildCount;
            }
            List<WZObject> @out = new List<WZObject>();
            foreach (WZObject levelNode in nodeLevel.Where(n => n.ChildCount > 0))
                @out.AddRange(levelNode.OrderBy(f => f.Name, StringComparer.Ordinal));
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
            bw.Write((ushort) node.ChildCount);
            ushort type;

            switch (node.Type) {
                case WZObjectType.Directory:
                case WZObjectType.Image:
                case WZObjectType.SubProperty:
                case WZObjectType.Convex:
                case WZObjectType.Null:
                    type = 0;
                    break;
                case WZObjectType.UInt16:
                case WZObjectType.Int32:
                case WZObjectType.Int64:
                    type = 1;
                    break;
                case WZObjectType.Single:
                case WZObjectType.Double:
                    type = 2;
                    break;
                case WZObjectType.String:
                    type = 3;
                    break;
                case WZObjectType.Point:
                    type = 4;
                    break;
                case WZObjectType.Canvas:
                case WZObjectType.Audio:
                    type = 5;
                    break;
                default:
                    throw new NotImplementedException($"Unhandled WZ node type {node.Type}");
            }
            bw.Write(type);

            switch (node.Type) {
                case WZObjectType.UInt16:
                    bw.Write((long) ((WZUInt16Property) node).Value);
                    break;
                case WZObjectType.Int32:
                    bw.Write((long) ((WZInt32Property) node).Value);
                    break;
                case WZObjectType.Int64:
                    bw.Write(((WZInt64Property) node).Value);
                    break;
                case WZObjectType.Single:
                    bw.Write((double) ((WZSingleProperty) node).Value);
                    break;
                case WZObjectType.Double:
                    bw.Write(((WZDoubleProperty) node).Value);
                    break;
                case WZObjectType.String:
                    bw.Write(ds.AddString(((WZStringProperty) node).Value));
                    bw.Write(0);
                    break;
                case WZObjectType.Point: {
                    Point pNode = ((WZPointProperty) node).Value;
                    bw.Write(pNode.X);
                    bw.Write(pNode.Y);
                    break;
                }
                case WZObjectType.Audio:
                    bw.Write(_dumpSnd ? ds.AddBlob(node) : 0U);
                    bw.Write(0);
                    break;
                case WZObjectType.Canvas:
                    bw.Write(_dumpImg ? ds.AddBlob(node) : 0U);
                    bw.Write(0);
                    break;
                default:
                    if (type != 0)
                        throw new InvalidOperationException($"Type data not handled for {node.Type}");
                    bw.Write(0L);
                    break;
            }
        }

        private static void WriteString(string s, BinaryWriter bw) {
            if (s.Any(char.IsControl))
                Console.WriteLine("Warning; control character in string. Perhaps toggle /wzn?");
            byte[] toWrite = Encoding.UTF8.GetBytes(s);
            bw.Write((ushort) toWrite.Length);
            bw.Write(toWrite);
        }

        private static void WriteBitmap(WZCanvasProperty node, BinaryWriter bw) {
            using (node) {
                Bitmap b = node.Value;
                int rawLen;
                byte[] compressed = GetCompressedBitmap(b, out rawLen);

                bw.Write(compressed.LongLength);
                bw.Write((long) rawLen);
                bw.Write((short) 1);

                bw.Write(WZBM);
                bw.Write(0);
                bw.Write(b.Width);
                bw.Write(b.Height);

                bw.Write(compressed);
            }
        }

        private static void WriteMP3(WZAudioProperty node, BinaryWriter bw) {
            using (node) {
                byte[] m = node.Value;
                byte[] header = node.Header;

                bw.Write(m.LongLength + header.LongLength + 2);
                bw.Write(m.LongLength + header.LongLength + 2);
                bw.Write((short) 0);

                bw.Write(WZAU);
                bw.Write(node.Duration);
                bw.Write(0L);

                bw.Write((ushort) header.Length);
                bw.Write(header);
                bw.Write(m);
            }
        }

        private static byte[] GetCompressedBitmap(Bitmap b, out int inLen) {
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            inLen = bd.Stride*bd.Height;
            int outLen = _is64bit ? EMaxOutputLen64(inLen) : EMaxOutputLen32(inLen);
            byte[] outBuf = new byte[outLen];
            outLen = _is64bit
                ? ECompressLZ464(bd.Scan0, outBuf, inLen, outLen, 0)
                : ECompressLZ432(bd.Scan0, outBuf, inLen, outLen, 0);
            b.UnlockBits(bd);
            Array.Resize(ref outBuf, outLen);
            return outBuf;
        }

        [DllImport("lz4hc_32.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compress_HC")]
        private static extern int ECompressLZ432(IntPtr source, byte[] dest, int inputLen, int maxSize, int level);

        [DllImport("lz4hc_64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compress_HC")]
        private static extern int ECompressLZ464(IntPtr source, byte[] dest, int inputLen, int maxSize, int level);

        [DllImport("lz4hc_32.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen32(int inputLen);

        [DllImport("lz4hc_64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen64(int inputLen);

        private sealed class DumpState {
            public DumpState() {
                Blobs = new List<WZObject>();
                Strings = new Dictionary<string, uint>(StringComparer.Ordinal) {{"", 0}};
                UOLs = new Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>>();
                Nodes = new Dictionary<WZObject, uint>();
            }

            public List<WZObject> Blobs { get; }

            public Dictionary<string, uint> Strings { get; }

            public Dictionary<WZUOLProperty, Action<BinaryWriter, byte[]>> UOLs { get; }

            public Dictionary<WZObject, uint> Nodes { get; }

            public uint AddBlob(WZObject node) {
                uint ret = (uint) Blobs.Count;
                Blobs.Add(node);
                return ret;
            }

            public uint AddString(string str) {
                if (Strings.ContainsKey(str))
                    return Strings[str];
                uint ret = (uint) Strings.Count;
                Strings.Add(str, ret);
                return ret;
            }

            public void AddNode(WZObject node) {
                uint ret = (uint) Nodes.Count;
                Nodes.Add(node, ret);
            }

            public uint GetNodeID(WZObject node) {
                return Nodes[node];
            }

            public uint GetNextNodeID() {
                return (uint) Nodes.Count;
            }

            public void AddUOL(WZUOLProperty node, long currentPosition) {
                UOLs.Add(node, (bw, data) => {
                    bw.BaseStream.Position = currentPosition;
                    bw.Write(data);
                });
            }
        }
    }
}
