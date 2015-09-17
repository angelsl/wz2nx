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
using System.Diagnostics;
using System.IO;
using CommandLine;
using reWZ;

namespace WZ2NX {
    internal static class Program {
        private static readonly Stopwatch _perTask = new Stopwatch();
        private static readonly Stopwatch _total = new Stopwatch();

        public static void Main(string[] args) {
            Parser.Default.ParseArguments<Options>(args).WithParsed(Run);
        }

        private static void Run(Options o) {
            if (string.IsNullOrWhiteSpace(o.OutPath))
                o.OutPath = Path.GetFileNameWithoutExtension(o.InPath) + ".nx";
            _total.Start();
            WZ2NX.Convert(o.InPath, o.OutPath, o.WZVariant, !o.WZNotEncrypted, o.DoAudio, o.DoBitmap, PrintStatus);
            Console.WriteLine("OK. T{0}", _total.Elapsed);
        }

        private static void PrintStatus(string s, bool d) {
            if (d)
                Console.WriteLine("{0,-4} E{1} T{2}", s, _perTask.Elapsed, _total.Elapsed);
            else {
                _perTask.Restart();
                Console.Write("{0,-31}", s + "...");
            }
        }

        private class Options {
            [Option('i', "in-path", Required = true, HelpText = "Path to the input WZ file.")]
            public string InPath { get; set; }

            [Option('o', "out-path", Required = false,
                HelpText = "Path to the output NX file. Defaults to <WZ file name>.nx in the current directory.")]
            public string OutPath { get; set; }

            [Option('t', "in-type", Required = true, HelpText = "WZ variant of the input WZ file.")]
            public WZVariant WZVariant { get; set; }

            [Option('n', "in-unencrypted", Default = false, HelpText = "Set if the input WZ file is not encrypted.")]
            public bool WZNotEncrypted { get; set; }

            [Option('a', "dump-audio", Default = false, HelpText = "Set if the output NX file should contain audio.")]
            public bool DoAudio { get; set; }

            [Option('b', "dump-bitmap", Default = false, HelpText = "Set if the output WZ file should contain bitmaps.")
            ]
            public bool DoBitmap { get; set; }
        }
    }
}
