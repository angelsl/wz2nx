// WZ2NX is copyright angelsl, 2011 to 2015 inclusive.
// 
// This file (WZ2NX.cs) is part of WZ2NX.
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using reWZ;
using reWZ.WZProperties;

namespace WZ2NX {
    public class WZ2NX {
        private const int NodeSize = 20;

        private static readonly byte[] PKG5 = {0x50, 0x4B, 0x47, 0x35}; // PKG5
        private static readonly byte[] WZBM = {0x57, 0x5A, 0x42, 0x4D}; // WZBM
        private static readonly byte[] WZAU = {0x57, 0x5A, 0x41, 0x55}; // WZAU
        private static readonly byte[] HeaderWZMagic = {0x84, 0x41};

        private static readonly bool _is64bit = IntPtr.Size == 8;

        public static void Convert(string inPath, string outPath, WZVariant wzType, bool wzEncrypted, bool doAudio,
            bool doBitmap, Action<string, bool> statusCallback, Action<int, int> bitmapCallback) {
            DumpState ds = new DumpState(inPath, outPath, wzType, wzEncrypted, doAudio, doBitmap, statusCallback, bitmapCallback);

            LoadWZFile(ds);
            CreateOutFile(ds);
            WriteNodes(ds);
            FixUOLs(ds);
            WriteNodesToFile(ds);
            WriteStrings(ds);
            WriteBlobs(ds);
            WriteHeader(ds);
            Cleanup(ds);
        }

        private static void LoadWZFile(DumpState ds) {
            ds.Callback("Parsing WZ file");
            ds.WZFile = new WZFile(ds.InPath, ds.WZType, ds.WZEncrypted);
            ds.CallbackOK();
        }

        private static void WriteNodes(DumpState ds) {
            ds.Callback("Collecting nodes");

            List<WZObject> nodes = ds.Nodes;
            int current = 0, remaining = 1;
            nodes.Add(ds.WZFile.MainDirectory);
            while (remaining > 0) {
                WZObject curNode = nodes[current];
                ds.ObjectToNodeID[curNode] = (uint) current;
                WriteNode(ds, curNode, (uint) (current + remaining));
                nodes.AddRange(curNode.OrderBy(o => o.Name, StringComparer.Ordinal));
                remaining += -1 + curNode.ChildCount;
                ++current;
            }

            ds.CallbackOK(current);
        }

        private static void WriteNodesToFile(DumpState ds) {
            ds.Callback("Writing nodes");
            ds.OutFileStream.Position = ds.NodeBlockOffset;
            ds.NodeBlockStream.Position = 0;
            ds.NodeBlockStream.CopyTo(ds.OutFileStream);
            ds.CallbackOK();
        }

        private static BitmapCompressionResult CompressBitmapWork(WZCanvasProperty o, uint id) {
            using (o) {
                Bitmap b = o.Value;
                BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                int inLen = bd.Stride*bd.Height;
                int outLen = _is64bit ? EMaxOutputLen64(inLen) : EMaxOutputLen32(inLen);
                byte[] outBuf = new byte[outLen];
                outLen = _is64bit
                    ? ECompressLZ464(bd.Scan0, outBuf, inLen, outLen, 0)
                    : ECompressLZ432(bd.Scan0, outBuf, inLen, outLen, 0);
                b.UnlockBits(bd);
                Array.Resize(ref outBuf, outLen);
                return new BitmapCompressionResult(id, b.Width, b.Height, outBuf, inLen);
            }
        }

        private static void CreateOutFile(DumpState ds) {
            ds.OutFileStream = new FileStream(ds.OutPath, FileMode.Create, FileAccess.ReadWrite);
            ds.OutWriter = new BinaryWriter(ds.OutFileStream);
            ds.NodeBlockStream = new MemoryStream(100 * 1024 * 1024);
        }

        private static void Cleanup(DumpState ds) {
            ds.OutWriter.Dispose();
            ds.OutFileStream.Dispose();
            ds.OutWriter = null;
            ds.OutFileStream = null;
            ds.WZFile.Dispose();
            ds.WZFile = null;
        }

        private static void WriteStrings(DumpState ds) {
            BinaryWriter w = ds.OutWriter;

            ds.Callback("Writing strings");
            List<long> offsets = new List<long>(ds.Strings.Count);
            foreach (string str in ds.Strings) {
                ds.OutFileStream.EnsureMultiple(2);
                offsets.Add(ds.OutFileStream.Position);
                byte[] toWrite = Encoding.UTF8.GetBytes(str);
                w.Write((ushort) toWrite.Length);
                w.Write(toWrite);
            }
            ds.CallbackOK(offsets.Count);

            ds.Callback("Writing string offset table");
            ds.OutFileStream.EnsureMultiple(8);
            ds.StringTableOffset = ds.OutFileStream.Position;
            foreach (long offset in offsets)
                w.Write(offset);
            ds.CallbackOK(offsets.Count);
        }

        private static void WriteBlobs(DumpState ds) {
            BinaryWriter w = ds.OutWriter;
            long[] offsets = new long[ds.BlobCount];

            // Write null blob
            {
                ds.OutFileStream.EnsureMultiple(8);
                offsets[0] = ds.OutFileStream.Position;
            }

            if (ds.DoAudio) {
                // Write audio blobs
                ds.Callback("Writing audio");
                foreach (Tuple<uint, WZAudioProperty> o in ds.Audios) {
                    ds.OutFileStream.EnsureMultiple(8);
                    offsets[o.Item1] = ds.OutFileStream.Position;
                    WriteAudio(o.Item2, w);
                }
                ds.CallbackOK(ds.Audios.Count);
            }

            // Write bitmaps
            if(ds.DoBitmap) {
                ds.Callback("Writing bitmaps");
                int bitmapsWritten = 0, bitmapCount = ds.BitmapCount;
                ManualResetEventSlim @lock = ds.BitmapCompletionLock;
                ConcurrentQueue<BitmapCompressionResult> results = ds.CompressedBitmaps;
                while (bitmapsWritten < bitmapCount) {
                    @lock.Wait();
                    BitmapCompressionResult bcr;
                    if (!results.TryDequeue(out bcr)) {
                        @lock.Reset();
                        continue;
                    }
                    ds.OutFileStream.EnsureMultiple(8);
                    offsets[bcr.ID] = ds.OutFileStream.Position;
                    WriteBitmap(bcr, w);
                    ++bitmapsWritten;
                    if((bitmapsWritten & 0x7FF) == 0x400) ds.ProgressCallback(bitmapsWritten, bitmapCount);
                }
                Debug.Assert(bitmapsWritten == bitmapCount);
                Debug.Assert(results.IsEmpty);
                ds.CallbackOK(bitmapsWritten);
            }

            ds.Callback("Writing blob offset table");
            ds.OutFileStream.EnsureMultiple(8);
            ds.BlobTableOffset = ds.OutFileStream.Position;
            foreach (long offset in offsets) {
                Debug.Assert(offset > ds.NodeBlockOffset);
                w.Write(offset);
            }
            ds.CallbackOK(offsets.Length);
        }

        private unsafe static void FixUOLs(DumpState ds) {
            ds.Callback("Resolving UOLs");
            int count = 0;
            int invalid = 0;
            byte[] nodeBlockBytes = ds.NodeBlockStream.GetBuffer();
            fixed (byte* nodePR = nodeBlockBytes) {
                foreach (WZObject o in ds.Nodes) {
                    if (o.Type != WZObjectType.UOL)
                        continue;
                    WZObject target = ((WZUOLProperty) o).FinalTarget;
                    if (target == null) {
                        ++invalid;
                        continue;
                    }
                    uint myId = ds.ObjectToNodeID[o];
                    uint tgId = ds.ObjectToNodeID[target];

                    // lol, abuse
                    *((double*) (nodePR + myId * NodeSize + 4)) = *((double*)(nodePR + tgId*NodeSize + 4));
                    ++count;
                }
            }
            ds.CallbackOK(count);
            ds.Callback("Killing Nexon for invalid UOLs");
            ds.CallbackOK(invalid);
        }

        private static void WriteHeader(DumpState ds) {
            ds.Callback("Writing header");
            BinaryWriter w = ds.OutWriter;
            ds.OutFileStream.Position = 0;

            w.Write(PKG5);
            w.Write(ds.Nodes.Count);
            w.Write(ds.NodeBlockOffset);
            w.Write(ds.Strings.Count);
            w.Write(ds.StringTableOffset);
            w.Write(ds.BlobCount); // +1 for the empty blob
            w.Write(ds.BlobTableOffset);
            w.Write(HeaderWZMagic);
            ds.CallbackOK();
        }

        private static void WriteBitmap(BitmapCompressionResult bcr, BinaryWriter bw) {
            bw.Write(bcr.CompressedData.LongLength);
            bw.Write(bcr.RawLength);
            bw.Write((short) 1);

            bw.Write(WZBM);
            bw.Write(0);
            bw.Write(bcr.Width);
            bw.Write(bcr.Height);

            bw.Write(bcr.CompressedData);
        }

        private static void WriteAudio(WZAudioProperty node, BinaryWriter bw) {
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

        private static unsafe void WriteNode(DumpState ds, WZObject node, uint firstChild) {
            byte[] nBytes = new byte[sizeof(NodeData)];
            fixed (byte* nBytesP = nBytes) {
                NodeData* nData = (NodeData*) nBytesP;
                nData->NodeNameID = ds.AddString(node.Name);
                nData->FirstChildID = firstChild;
                nData->ChildCount = (ushort) node.ChildCount;

                switch (node.Type) {
                    case WZObjectType.Directory:
                    case WZObjectType.Image:
                    case WZObjectType.SubProperty:
                    case WZObjectType.Convex:
                    case WZObjectType.UOL:
                    case WZObjectType.Null:
                        nData->Type = 0;
                        break;
                    case WZObjectType.UInt16:
                    case WZObjectType.Int32:
                    case WZObjectType.Int64:
                        nData->Type = 1;
                        break;
                    case WZObjectType.Single:
                    case WZObjectType.Double:
                        nData->Type = 2;
                        break;
                    case WZObjectType.String:
                        nData->Type = 3;
                        break;
                    case WZObjectType.Point:
                        nData->Type = 4;
                        break;
                    case WZObjectType.Canvas:
                    case WZObjectType.Audio:
                        nData->Type = 5;
                        break;
                    default:
                        throw new NotImplementedException($"Unhandled WZ node type {node.Type}");
                }

                switch (node.Type) {
                    case WZObjectType.UInt16:
                        nData->Type1Data = ((WZUInt16Property) node).Value;
                        break;
                    case WZObjectType.Int32:
                        nData->Type1Data = ((WZInt32Property) node).Value;
                        break;
                    case WZObjectType.Int64:
                        nData->Type1Data = ((WZInt64Property) node).Value;
                        break;
                    case WZObjectType.Single:
                        nData->Type2Data = ((WZSingleProperty) node).Value;
                        break;
                    case WZObjectType.Double:
                        nData->Type2Data = ((WZDoubleProperty) node).Value;
                        break;
                    case WZObjectType.String:
                        nData->TypeIDData = ds.AddString(((WZStringProperty) node).Value);
                        break;
                    case WZObjectType.Point: {
                        Point pNode = ((WZPointProperty) node).Value;
                        nData->Type4DataX = pNode.X;
                        nData->Type4DataY = pNode.Y;
                        break;
                    }
                    case WZObjectType.Audio:
                        // Blob 0 is the empty blob
                        nData->TypeIDData = ds.DoAudio ? ds.AddAudio((WZAudioProperty) node) : 0U;
                        break;
                    case WZObjectType.Canvas:
                        // Blob 0 is the empty blob
                        nData->TypeIDData = ds.DoBitmap ? ds.AddBitmap((WZCanvasProperty) node) : 0U;
                        break;
                    default:
                        if (nData->Type != 0)
                            throw new InvalidOperationException($"Type data not handled for {node.Type}");
                        break;
                }
            }

            ds.NodeBlockStream.Write(nBytes, 0, nBytes.Length);
        }

        [DllImport("lz4hc_32.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compress_HC")]
        private static extern int ECompressLZ432(IntPtr source, byte[] dest, int inputLen, int maxSize, int level);

        [DllImport("lz4hc_64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compress_HC")]
        private static extern int ECompressLZ464(IntPtr source, byte[] dest, int inputLen, int maxSize, int level);

        [DllImport("lz4hc_32.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen32(int inputLen);

        [DllImport("lz4hc_64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compressBound")]
        internal static extern int EMaxOutputLen64(int inputLen);

        private class DumpState {
            private readonly Action<string, bool> _statusCallback;

            private readonly Dictionary<string, uint> _stringsAdded = new Dictionary<string, uint>(65536,
                StringComparer.Ordinal);

            public readonly long NodeBlockOffset = 44;
            private uint _nextBlobID = 1; // Blob 0 is the empty blob
            private uint _nextStringID;

            public DumpState(string inPath, string outPath, WZVariant wzType, bool wzEncrypted, bool doAudio,
                bool doBitmap, Action<string, bool> statusCallback, Action<int, int> progressCallback) {
                InPath = inPath;
                OutPath = outPath;
                WZType = wzType;
                WZEncrypted = wzEncrypted;
                DoAudio = doAudio;
                DoBitmap = doBitmap;
                _statusCallback = statusCallback;
                ProgressCallback = progressCallback;
            }

            public string InPath { get; }
            public string OutPath { get; }
            public WZVariant WZType { get; }
            public bool WZEncrypted { get; }
            public bool DoAudio { get; }
            public bool DoBitmap { get; }

            public Action<int, int> ProgressCallback { get; }

            public WZFile WZFile { get; set; }
            public FileStream OutFileStream { get; set; }
            public BinaryWriter OutWriter { get; set; }
            public MemoryStream NodeBlockStream { get; set; }
            public long StringTableOffset { get; set; }
            public long BlobTableOffset { get; set; }
            public int BitmapCount { get; private set; }
            public int BlobCount => (int) _nextBlobID;

            public List<WZObject> Nodes { get; } = new List<WZObject>(65536);
            public Dictionary<WZObject, uint> ObjectToNodeID { get; } = new Dictionary<WZObject, uint>(65536);
            public List<string> Strings { get; } = new List<string>(65536);
            public List<Tuple<uint, WZAudioProperty>> Audios { get; } = new List<Tuple<uint, WZAudioProperty>>(1024);
            public ConcurrentQueue<BitmapCompressionResult> CompressedBitmaps { get; } = new ConcurrentQueue<BitmapCompressionResult>();
            public ManualResetEventSlim BitmapCompletionLock { get; } = new ManualResetEventSlim(false);

            public uint AddString(string s) {
                if (_stringsAdded.ContainsKey(s))
                    return _stringsAdded[s];
                _stringsAdded[s] = _nextStringID;
                Strings.Add(s);
                return _nextStringID++;
            }

            public uint AddAudio(WZAudioProperty o) {
                Audios.Add(Tuple.Create(_nextBlobID, o));
                return _nextBlobID++;
            }

            public uint AddBitmap(WZCanvasProperty o) {
                uint thisId = _nextBlobID++;
                Task.Factory.StartNew(() => {
                    BitmapCompressionResult bcr = CompressBitmapWork(o, thisId);
                    CompressedBitmaps.Enqueue(bcr);
                    BitmapCompletionLock.Set();
                });
                BitmapCount++;
                return thisId;
            }

            public void Callback(string s, bool done = false) {
                _statusCallback?.Invoke(s, done);
            }

            public void CallbackOK() {
                Callback("OK", true);
            }

            public void CallbackOK(int count) {
                Callback($"{count} OK", true);
            }
        }

        private class BitmapCompressionResult {
            public BitmapCompressionResult(uint id, int width, int height, byte[] compressedData, long rawLength) {
                ID = id;
                Width = width;
                Height = height;
                CompressedData = compressedData;
                RawLength = rawLength;
            }

            public uint ID { get; }
            public int Width { get; }
            public int Height { get; }
            public byte[] CompressedData { get; }
            public long RawLength { get; }
        }

        [StructLayout(LayoutKind.Explicit, Size = 20, Pack = 2)]
        internal struct NodeData {
            [FieldOffset(0)]
            internal uint NodeNameID;

            [FieldOffset(4)]
            internal uint FirstChildID;

            [FieldOffset(8)]
            internal ushort ChildCount;

            [FieldOffset(10)]
            internal ushort Type;

            [FieldOffset(12)]
            internal long Type1Data;

            [FieldOffset(12)]
            internal double Type2Data;

            [FieldOffset(12)]
            internal uint TypeIDData;

            [FieldOffset(12)]
            internal int Type4DataX;

            [FieldOffset(16)]
            internal int Type4DataY;
        }
    }

    internal static class Extensions {
        internal static void EnsureMultiple(this Stream s, int multiple) {
            int skip = (int) (multiple - (s.Position%multiple));
            if (skip == multiple)
                return;
            s.Write(new byte[skip], 0, skip);
        }
    }
}
