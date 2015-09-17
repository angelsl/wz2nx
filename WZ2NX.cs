using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using reWZ;
using reWZ.WZProperties;

namespace WZ2NX {
    public class WZ2NX {
        private class DumpState {
            public DumpState(string inPath, string outPath, WZVariant wzType, bool wzEncrypted, bool doAudio, bool doBitmap, Action<string, bool> statusCallback) {
                InPath = inPath;
                OutPath = outPath;
                WZType = wzType;
                WZEncrypted = wzEncrypted;
                DoAudio = doAudio;
                DoBitmap = doBitmap;
                _statusCallback = statusCallback;
            }

            public string InPath { get; }
            public string OutPath { get; }
            public WZVariant WZType { get; }
            public bool WZEncrypted { get; }
            public bool DoAudio { get; }
            public bool DoBitmap { get; }
            public WZFile WZFile { get; set; }
            public FileStream OutFileStream { get; set; }
            public BinaryWriter OutWriter { get; set; }

            public readonly long NodeBlockOffset = 44;
            public long StringTableOffset { get; set; }
            public long BlobTableOffset { get; set; }

            private uint _nextStringID = 0;
            private uint _nextNodeID = 0;
            private uint _nextBlobID = 0;
            private readonly Action<string, bool> _statusCallback;
            private readonly Dictionary<string, uint> _stringsAdded = new Dictionary<string, uint>(65536, StringComparer.Ordinal);

            public List<WZObject> Nodes { get; } = new List<WZObject>(65536);
            public Dictionary<WZObject, uint> FirstChild { get; } = new Dictionary<WZObject, uint>(65536);
            public Dictionary<WZObject, uint> ObjectToNodeID { get; } = new Dictionary<WZObject, uint>(65536);
            public List<string> Strings { get; } = new List<string>(65536);
            public List<WZObject> Blobs { get; } = new List<WZObject>(65536);

            public Dictionary<WZObject, Task<BitmapCompressionResult>> BitmapCompression { get; } =
                new Dictionary<WZObject, Task<BitmapCompressionResult>>(1024);

            public uint AddNode(WZObject o) {
                Nodes.Add(o);
                ObjectToNodeID.Add(o, _nextNodeID);
                return _nextNodeID++;
            }

            public uint AddString(string s) {
                if (_stringsAdded.ContainsKey(s))
                    return _stringsAdded[s];
                _stringsAdded[s] = _nextStringID;
                Strings.Add(s);
                return _nextStringID++;
            }

            public uint AddBlob(WZObject o) {
                Blobs.Add(o);
                return _nextBlobID++;
            }

            public void Callback(string s, bool done = false) {
                _statusCallback?.Invoke(s, done);
            }

            public void CallbackOK() {
                Callback("OK", true);
            }
        }

        private class BitmapCompressionResult {
            public BitmapCompressionResult(int width, int height, byte[] compressedData, long rawLength) {
                Width = width;
                Height = height;
                CompressedData = compressedData;
                RawLength = rawLength;
            }

            public int Width { get; }
            public int Height { get; }
            public byte[] CompressedData { get; }
            public long RawLength { get; }
        }

        public static void Convert(string inPath, string outPath, WZVariant wzType, bool wzEncrypted, bool doAudio, bool doBitmap, Action<string, bool> statusCallback) {
            /*
                Brief outline:
                Iterate through WZ file and collect all nodes
                Start compressing bitmaps
                Write nodes and collect strings
                Write strings
                Write byte arrays
                Write UOLs
                Finalise
            */
            DumpState ds = new DumpState(inPath, outPath, wzType, wzEncrypted, doAudio, doBitmap, statusCallback);

            ds.AddBlob(null);

            LoadWZFile(ds);
            FlattenTree(ds);
            if(ds.DoBitmap) CompressBitmaps(ds);
            CreateOutFile(ds);
            WriteNodes(ds);
            WriteStrings(ds);
            WriteBlobs(ds);
            FixUOLs(ds);
            WriteHeader(ds);
            Cleanup(ds);
        }

        private static void LoadWZFile(DumpState ds) {
            ds.Callback("Parsing WZ file");
            ds.WZFile = new WZFile(ds.InPath, ds.WZType, ds.WZEncrypted, WZReadSelection.EagerParseImage | WZReadSelection.EagerParseStrings);
            ds.CallbackOK();
        }

        private static void FlattenTree(DumpState ds) {
            ds.Callback("Collecting nodes");
            WZDirectory root = ds.WZFile.MainDirectory;
            ds.AddNode(root);
            AddNodeLevel(ds, root);
            ds.CallbackOK();
        }

        private static void AddNodeLevel(DumpState ds, WZObject parent) {
            IEnumerable<WZObject> sortedChildren = parent.OrderBy(o => o.Name, StringComparer.Ordinal);
            bool first = true;
            foreach (WZObject child in sortedChildren) {
                if (first) {
                    ds.FirstChild[parent] = ds.AddNode(child);
                    first = false;
                } else {
                    ds.AddNode(child);
                }
            }
            foreach (WZObject child in sortedChildren)
                AddNodeLevel(ds, child);
        }

        private static void CompressBitmaps(DumpState ds) {
            ds.Callback("Starting bitmap compression");
            foreach (WZObject node in ds.Nodes) {
                if (node.Type != WZObjectType.Canvas)
                    continue;
                ds.BitmapCompression[node] = Task.Factory.StartNew((Func<object, BitmapCompressionResult>) CompressBitmapWork, node);
            }
            ds.CallbackOK();
        }

        private static BitmapCompressionResult CompressBitmapWork(object o) {
            WZCanvasProperty c = (WZCanvasProperty) o;
            using (c) {
                Bitmap b = c.Value;
                BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                int inLen = bd.Stride * bd.Height;
                int outLen = _is64bit ? EMaxOutputLen64(inLen) : EMaxOutputLen32(inLen);
                byte[] outBuf = new byte[outLen];
                outLen = _is64bit ? ECompressLZ464(bd.Scan0, outBuf, inLen, outLen, 0) : ECompressLZ432(bd.Scan0, outBuf, inLen, outLen, 0);
                b.UnlockBits(bd);
                Array.Resize(ref outBuf, outLen);
                return new BitmapCompressionResult(b.Width, b.Height, outBuf, inLen);
            }
        }

        private static void CreateOutFile(DumpState ds) {
            ds.OutFileStream = new FileStream(ds.OutPath, FileMode.Create, FileAccess.ReadWrite);
            ds.OutWriter = new BinaryWriter(ds.OutFileStream);
        }

        private static void Cleanup(DumpState ds) {
            ds.OutWriter.Dispose();
            ds.OutFileStream.Dispose();
            ds.OutWriter = null;
            ds.OutFileStream = null;
            ds.WZFile.Dispose();
            ds.WZFile = null;
        }

        private static void WriteNodes(DumpState ds) {
            ds.Callback("Writing nodes");
            ds.OutFileStream.Position = ds.NodeBlockOffset;
            foreach (WZObject node in ds.Nodes) {
                WriteNode(ds, node);
            }
            ds.CallbackOK();
        }

        private static void WriteStrings(DumpState ds) {
            ds.Callback("Writing strings");
            BinaryWriter w = ds.OutWriter;

            List<long> offsets = new List<long>(ds.Strings.Count);
            foreach (string str in ds.Strings) {
                ds.OutFileStream.EnsureMultiple(2);
                offsets.Add(ds.OutFileStream.Position);
                byte[] toWrite = Encoding.UTF8.GetBytes(str);
                w.Write((ushort) toWrite.Length);
                w.Write(toWrite);
            }

            ds.OutFileStream.EnsureMultiple(8);
            ds.StringTableOffset = ds.OutFileStream.Position;
            foreach (long offset in offsets) {
                w.Write(offset);
            }
            ds.CallbackOK();
        }

        private static void WriteBlobs(DumpState ds) {
            ds.Callback("Writing blobs");
            BinaryWriter w = ds.OutWriter;

            List<long> offsets = new List<long>(ds.Blobs.Count);
            foreach (WZObject o in ds.Blobs) {
                ds.OutFileStream.EnsureMultiple(8);
                offsets.Add(ds.OutFileStream.Position);
                if (o == null) {
                    w.Write(new byte[34]);
                    continue;
                }
                switch (o.Type) {
                    case WZObjectType.Canvas: {
                        Task<BitmapCompressionResult> task = ds.BitmapCompression[o];
                        task.Wait();
                        WriteBitmap(task.Result, w);
                        break;
                    }
                    case WZObjectType.Audio: {
                        WriteAudio((WZAudioProperty)o, w);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Invalid WZ blob type {o.Type}");
                }
            }

            ds.OutFileStream.EnsureMultiple(8);
            ds.BlobTableOffset = ds.OutFileStream.Position;
            foreach (long offset in offsets) {
                w.Write(offset);
            }
            ds.CallbackOK();
        }

        private static void FixUOLs(DumpState ds) {
            ds.Callback("Resolving UOLs");
            BinaryWriter w = ds.OutWriter;

            foreach (WZObject o in ds.Nodes) {
                if (o.Type != WZObjectType.UOL)
                    continue;
                WZObject target = ((WZUOLProperty) o).FinalTarget;
                if (target == null)
                    continue;
                uint myId = ds.ObjectToNodeID[o];
                uint tgId = ds.ObjectToNodeID[target];
                byte[] copy = new byte[NodeSize - 4];

                ds.OutFileStream.Position = ds.NodeBlockOffset + tgId*NodeSize + 4;
                ds.OutFileStream.Read(copy, 0, copy.Length);
                ds.OutFileStream.Position = ds.NodeBlockOffset + myId*NodeSize + 4;
                w.Write(copy);
            }
            ds.CallbackOK();
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
            w.Write(ds.Blobs.Count);
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

        private static void WriteNode(DumpState ds, WZObject node) {
            BinaryWriter bw = ds.OutWriter;
            bw.Write(ds.AddString(node.Name));
            if (node.ChildCount > 0) {
                bw.Write(ds.FirstChild[node]);
                bw.Write((ushort) node.ChildCount);
            } else {
                bw.Write(0U);
                bw.Write((ushort)0);
            }

            ushort type;
            switch (node.Type) {
                case WZObjectType.Directory:
                case WZObjectType.Image:
                case WZObjectType.SubProperty:
                case WZObjectType.Convex:
                case WZObjectType.UOL:
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
                    bw.Write(ds.DoAudio ? ds.AddBlob(node) : 0U);
                    bw.Write(0);
                    break;
                case WZObjectType.Canvas:
                    bw.Write(ds.DoBitmap ? ds.AddBlob(node) : 0U);
                    bw.Write(0);
                    break;
                default:
                    if (type != 0)
                        throw new InvalidOperationException($"Type data not handled for {node.Type}");
                    bw.Write(0L);
                    break;
            }
        }

        private static readonly byte[] PKG5 = { 0x50, 0x4B, 0x47, 0x35 }; // PKG5
        private static readonly byte[] WZBM = { 0x57, 0x5A, 0x42, 0x4D }; // WZBM
        private static readonly byte[] WZAU = { 0x57, 0x5A, 0x41, 0x55 }; // WZAU
        private static readonly byte[] HeaderWZMagic = { 0x84, 0x41 };
        private const int NodeSize = 20;

        private static readonly bool _is64bit = IntPtr.Size == 8;

        [DllImport("lz4hc_32.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compress_HC")]
        private static extern int ECompressLZ432(IntPtr source, byte[] dest, int inputLen, int maxSize, int level);

        [DllImport("lz4hc_64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compress_HC")]
        private static extern int ECompressLZ464(IntPtr source, byte[] dest, int inputLen, int maxSize, int level);

        [DllImport("lz4hc_32.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compressBound")]
        private static extern int EMaxOutputLen32(int inputLen);

        [DllImport("lz4hc_64.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "LZ4_compressBound")]
        internal static extern int EMaxOutputLen64(int inputLen);
    }

    static class Extensions {
        internal static void EnsureMultiple(this Stream s, int multiple) {
            int skip = (int) (multiple - (s.Position % multiple));
            if (skip == multiple)
                return;
            s.Write(new byte[skip], 0, skip);
        }
    }
}
