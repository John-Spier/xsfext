using System.Text;
using UtfUnknown;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json.Serialization;


namespace SSFExt
{
    [JsonSourceGenerationOptions(
    WriteIndented = true,
    IncludeFields = true
    //DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    )]
    [JsonSerializable(typeof(XsfFile))]
    [JsonSerializable(typeof(XsfTable))]
    [JsonSerializable(typeof(VFSFile2))]
    [JsonSerializable(typeof(VFSBinary2))]
    [JsonSerializable(typeof(List<XsfFile>))]
    [JsonSerializable(typeof(List<VFSFile2>))]
    [JsonSerializable(typeof(List<int>))]
    internal partial class JsonSerializationContext : JsonSerializerContext
    {
    }

    public enum BinaryType
    {
        ANY,
        BIN,
        XSF,
        MINIXSF
    }
    public enum XsfType //can add gsf for real gba playback later
    {
        ANY,
        SSF,
        DSF
    }
    public struct XsfFile
    {
        //public uint segment;
        public bool modified;
        public string filename;
        public byte[] headersect;
        public uint start;
        public uint end;
        public uint crc;
        public byte[] reserved_area;
        public byte[] tags;
        public string tag_encoding;
        public bool is_library;
        public string md5;
    }
    public struct XsfTable
    {
        public BinaryType btype;
        public XsfType ftype;
        public byte[] ram;
        public List<XsfFile> minixsfs;
    }
    public struct VFSFile2
    {
        public bool load_direct;
        public string source;
        public string name;
        public uint filetype;
        public int file_start; //needed for seq/ton just in case
        public int file_end;
        public VFSBinary2 binary;
        public bool dont_load_xsflibs;
        public List<int> libs;
    }
    public struct VFSBinary2
    {
        public byte[] name; //64
        public int size;
        public int padding;
        public int addr;
        //public byte[] data;
    }


    internal class Program
    {
        static void Main(string[] args)
        {

            ExtractMergeVFS("PRI.VFS", BinaryType.MINIXSF);
            //x.btype = BinaryType.XSF;
            //SaveMiniXsf(x, [".\\VFSTEST\\FULL19.SSF"]);
            //var v = GetVFSFiles(".\\SF3\\", "*.minissf");
            //SaveVFSFile("PRI.VFS", v);
        }

        static XsfFile CreateMiniPSF(XsfTable lib, XsfTable psf, bool verbose = false, StreamWriter? con = null, byte[]? zerolib = null,
            Encoding? outenc = null, int start_padding = -1, int end_padding = -1, bool savezero = true)
        {
            con ??= new(Console.OpenStandardOutput());
            con.AutoFlush = true;
            int lib_base, psf_base;
            int h = HeaderSize(lib.ftype), k = HeaderSize(psf.ftype);
            int change_start = int.MaxValue, change_end = int.MinValue, bytediff = 0;
            uint lib_start = GetBinOffset(lib.ram, lib.ftype, false);//BitConverter.ToUInt32(lib.ram, 24);// % 0x20000000;
            uint psf_start = GetBinOffset(psf.ram, psf.ftype, false);// % 0x20000000;
            
            //var context = new JsonSerializationContext();
            var sourceFile = psf.minixsfs.LastOrDefault(x => !x.is_library);
            
            if (string.IsNullOrEmpty(sourceFile.filename))
            {
                throw new InvalidOperationException("No non-library XSF file found in the collection");
            }
            
            XsfFile psf1 = sourceFile; // Structs are value types, no json needed
            bool zlib = !(zerolib == null || zerolib.Length == 0);
            if (lib_start <= psf_start)
            {
                psf_base = k;
                lib_base = (int)(psf_start - lib_start) + h;
            }
            else
            {
                lib_base = h;
                psf_base = (int)(lib_start - psf_start) + k;
                change_start = h;
            }

            if (psf_base + psf.ram.Length > lib_base + lib.ram.Length)
            {
                change_end = psf.ram.Length;
            }
            if (start_padding == -1)
            {
                start_padding = h;
            }
            if (end_padding == -1)
            {
                end_padding = h;
            }

            if (verbose)
            {
                con.WriteLine($"Library: {FindName(lib.minixsfs.Last())} XSF: {FindName(psf1)}");
                con.WriteLine($"Lib start/length: {lib_start}/{lib.ram.Length} XSF start/length: {psf_start}/{psf.ram.Length}");
                con.WriteLine($"Lib/XSF base address: {lib_base}/{psf_base} XSF original start/end: {psf1.start}/{psf1.end}");// - psf.minipsfs.Last(x => !x.is_library).start}");
            }


            zerolib ??= [];
            int nonzerocorr = 0;

            if (change_start == int.MaxValue || change_end == int.MinValue)
            {
                for (int i = 0; i + lib_base < lib.ram.Length && i + psf_base < psf.ram.Length; i++)
                {
                    if (lib.ram[i + lib_base] != psf.ram[i + psf_base])
                    {
                        bytediff++;

                        change_start = int.Min(change_start, i + psf_base);
                        change_end = int.Max(change_end, i + psf_base + 1);
                        //con.WriteLine($"{i + lib_base}/{i + psf_base}: {lib.ram[i + lib_base]}/{psf.ram[i + psf_base]}");
                    }
                    else if (lib.ram[i + lib_base] != 0)
                    {
                        nonzerocorr++;
                        if (zlib && zerolib.Length > i + lib_base)
                        {
                            zerolib[i + lib_base] = lib.ram[i + lib_base];
                        }
                        //con.WriteLine($"OK: {i + lib_base}/{i + psf_base}: {lib.ram[i + lib_base]}/{psf.ram[i + psf_base]}");
                    }
                }
            }

            if (zlib && change_start - psf_base + lib_base <= zerolib.Length)
            {
                Array.Copy(lib.ram, 0, zerolib, 0, change_start - psf_base + lib_base);
            }

            if (zlib && change_end - psf_base + lib_base <= zerolib.Length && change_end - psf_base + lib_base >= 0)
            {
                Array.Copy(lib.ram, change_end - psf_base + lib_base, zerolib, change_end - psf_base + lib_base, zerolib.Length - (change_end - psf_base + lib_base));
            }

            if (verbose)
            {
                //con.WriteLine($"Lib start/length: {lib_start}/{lib.ram.Length} PSF start/length: {psf_start}/{psf.ram.Length}");
                //con.WriteLine($"Lib/PSF base address: {lib_base}/{psf_base}");
                con.WriteLine($"First/last changed addresses: {change_start}/{change_end}");
                con.WriteLine($"Differences/nonzero correct/correct zero bytes: {bytediff}/{nonzerocorr}/{psf.ram.Length - (nonzerocorr + bytediff)}");
                con.WriteLine($"Start/end of non-copied addresses in library due to no overlap: {change_start - psf_base + lib_base}/{change_end - psf_base + lib_base}");
                //con.WriteLine();
            }
            //change_start -= change_start % 2048;
            if (change_end > change_start)
            {
                Encoding? encoding = null;

                try
                {
                    encoding = Encoding.GetEncoding(psf1.tag_encoding);
                }
                catch (Exception e)
                {
                    if (verbose)
                    {
                        con.WriteLine($"Encoding error {e.Message}, defaulting to UTF8");
                    }
                    encoding = null;
                }
                outenc ??= encoding;
                psf1.start = (uint)(change_start - (change_start % start_padding));
                psf1.end = (uint)int.Min(change_end + GetPadding(change_end, end_padding), psf.ram.Length);
                if (verbose)
                {
                    con.WriteLine($"Header start/end: {(psf1.start - h + psf_start)}/{(psf1.end - h + psf_start)}");
                    long hdrstart = GetBinOffset(psf1.headersect, psf.ftype);
                    con.WriteLine($"Old Header Start: {hdrstart}");// Old Header End: {BitConverter.ToUInt32(psf1.headersect, 0x1C) + hdrstart}");
                    con.WriteLine();
                }
                psf1.headersect = GetHeaderSect(psf1.start - (uint)h + psf_start, psf.ftype, false);
                //Array.Copy(BitConverter.GetBytes(psf1.end - psf1.start), 0, psf1.headersect, 0x1C, 4);
                psf1.tags = RemoveLibTags(psf1.tags, encoding, [$"_lib={Path.GetFileName(lib.minixsfs.Last().filename)}"], outenc: outenc);
                psf1.modified = true;
            }
            else
            {
                psf1.start = (uint)k;
                psf1.end = (uint)k;
                psf1.modified = savezero;
            }


            return psf1;
        }

        static byte[][] Tonext(byte[] ram)
        {
            ArgumentNullException.ThrowIfNull(ram);
            int fisz = ram.Length;
            if (fisz == 0) return [];

            var extracted = new List<byte[]>();
            int offset = 0;
            int[] samplesize = [2, 1];
            int[] unitdatasize = [0x12, 0x0A, 0x0A, 0x04];
            int layersize = 0x20;

            static ushort ReadU16BE(byte[] b, int i) => (ushort)((b[i] << 8) | b[i + 1]);
            static uint ReadU32BE(byte[] b, int i) => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

            try
            {
                while (offset < fisz)
                {
                    // linear scan for a potential offmix value (two-byte BE) at any alignment
                    int pos = -1;
                    for (int p = offset; p + 2 <= fisz; p++)
                    {
                        int candidate = ReadU16BE(ram, p);
                        if (candidate >= 0x000A && candidate < 0x0108)
                        {
                            pos = p;
                            break;
                        }
                    }

                    if (pos < 0) break;

                    // need at least 8 bytes for offmix, offvl, offpeg, offplfo
                    if (pos + 8 > fisz)
                    {
                        offset = pos + 1;
                        continue;
                    }

                    int offmix = ReadU16BE(ram, pos);
                    int offvl = ReadU16BE(ram, pos + 2);
                    int offpeg = ReadU16BE(ram, pos + 4);
                    int offplfo = ReadU16BE(ram, pos + 6);

                    int nvoices = (offmix - 8) / 2;
                    if (nvoices <= 0)
                    {
                        offset = pos + 1;
                        continue;
                    }

                    // ensure voice offsets are present
                    if (pos + 8 + 2 * nvoices > fisz)
                    {
                        offset = pos + 1;
                        continue;
                    }

                    ushort[] offvoices = new ushort[nvoices];
                    for (int i = 0; i < nvoices; i++)
                    {
                        offvoices[i] = ReadU16BE(ram, pos + 8 + 2 * i);
                    }

                    // build offset list
                    int totalOffsets = 4 + offvoices.Length;
                    int[] offsetList = new int[totalOffsets];
                    offsetList[0] = offmix;
                    offsetList[1] = offvl;
                    offsetList[2] = offpeg;
                    offsetList[3] = offplfo;
                    for (int i = 0; i < offvoices.Length; i++) offsetList[4 + i] = offvoices[i];

                    // compute diffs and check monotonic sequence (all diffs > 0)
                    int[] diffs = new int[totalOffsets - 1];
                    bool monotonic = true;
                    for (int i = 0; i < diffs.Length; i++)
                    {
                        diffs[i] = offsetList[i + 1] - offsetList[i];
                        if (diffs[i] <= 0) { monotonic = false; break; }
                    }

                    if (!monotonic)
                    {
                        offset = pos + 1;
                        continue;
                    }

                    // check deltas consistency: first 4 diffs mod unitdatasize == 0,
                    // remaining diffs mod layersize == 4
                    bool deltasConsistent = true;
                    for (int i = 0; i < diffs.Length; i++)
                    {
                        if (i < 4)
                        {
                            if (diffs[i] % unitdatasize[i] != 0) { deltasConsistent = false; break; }
                        }
                        else
                        {
                            if (diffs[i] % layersize != 4) { deltasConsistent = false; break; }
                        }
                    }

                    if (!deltasConsistent)
                    {
                        offset = pos + 1;
                        continue;
                    }

                    uint offtonemax = 0;
                    int tonemaxsize = 0;

                    // loop through voices and layers
                    bool boundsProblem = false;
                    foreach (ushort offvoice in offvoices)
                    {
                        int voiceBase = pos + offvoice;
                        // need at least voiceBase+3 to read signed byte at +2
                        if (voiceBase + 3 > fisz) { boundsProblem = true; break; }

                        sbyte nlayersSigned = (sbyte)ram[voiceBase + 2];
                        int nlayers = nlayersSigned + 1;
                        if (nlayers < 0) { boundsProblem = true; break; }

                        for (int ilayer = 0; ilayer < nlayers; ilayer++)
                        {
                            long layerBaseLong = voiceBase + 4L + layersize * ilayer;
                            if (layerBaseLong + 10 > fisz) { boundsProblem = true; break; }

                            int layerBase = (int)layerBaseLong;
                            uint offtone = ReadU32BE(ram, layerBase + 2) & 0x0007FFFFu;
                            if (offtone > offtonemax)
                            {
                                offtonemax = offtone;
                                int pcm8b = (ram[layerBase + 3] >> 4) & 0x1;
                                int lengthSamples = ReadU16BE(ram, layerBase + 8);
                                tonemaxsize = samplesize[pcm8b] * lengthSamples;
                            }
                        }
                        if (boundsProblem) break;
                    }

                    if (boundsProblem)
                    {
                        offset = pos + 1;
                        continue;
                    }

                    // compute total size and clamp to remaining bytes
                    long totalSizeLong = (long)offtonemax + tonemaxsize;
                    if (totalSizeLong <= 0)
                    {
                        offset = pos + 1;
                        continue;
                    }
                    int maxAvailable = fisz - pos;
                    int totalSize = (int)Math.Min(totalSizeLong, maxAvailable);

                    // extract bytes
                    byte[] seg = new byte[totalSize];
                    Array.Copy(ram, pos, seg, 0, totalSize);
                    extracted.Add(seg);

                    // advance offset to last byte of tone data (as in original)
                    offset = pos + totalSize - 1;
                    offset++;
                }
            }
            catch
            {
                // on any error return whatever was found so far
                return [.. extracted];
            }

            return [.. extracted];
        }

        static byte[][] Seqext(byte[] ram)
        {
            ArgumentNullException.ThrowIfNull(ram);
            int fisz = ram.Length;
            var extracted = new List<byte[]>();

            // build command length table (256 entries)
            int[] comlen = new int[256];
            for (int i = 0; i < 0x80; i++) comlen[i] = 5;
            int idx = 0x80;
            comlen[idx++] = 1; comlen[idx++] = 4; comlen[idx++] = 2; comlen[idx++] = 1;
            for (int i = 0; i < 0x1C; i++) comlen[idx++] = 1;
            for (int i = 0; i < 0x20; i++) comlen[idx++] = 4;
            for (int i = 0; i < 0x30; i++) comlen[idx++] = 3;
            for (int i = 0; i < 0x10; i++) comlen[idx++] = 1;

            static ushort ReadU16BE(byte[] b, int i) => (ushort)((b[i] << 8) | b[i + 1]);
            static uint ReadU32BE(byte[] b, int i) => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

            try
            {
                // linear scan every possible offset (safe; exact validation follows)
                for (int pos = 0; pos + 6 <= fisz; pos++)
                {
                    // read potential header values (big-endian)
                    ushort ntrack = ReadU16BE(ram, pos);
                    uint ftrack = ReadU32BE(ram, pos + 2);

                    // test 1: basic sanity of track count and first-track offset
                    if (!(ntrack > 0 && ntrack < 128 && 4 * ntrack + 2 == ftrack))
                        continue;

                    // location of last track offset (table starts at pos+2, entries are 4 bytes)
                    int lastTrackIndexOffset = pos + 2 + 4 * (ntrack - 1);
                    if (lastTrackIndexOffset + 4 > fisz)
                        continue;

                    uint atrack = ReadU32BE(ram, lastTrackIndexOffset);

                    // test 2: ensure last-track header fits in file
                    if ((long)pos + atrack + 6 >= fisz)
                        continue;

                    // read ntmp and aseq from last track header
                    int ntmpOff = pos + (int)atrack + 2;
                    int aseqOff = pos + (int)atrack + 4;
                    if (ntmpOff + 2 > fisz || aseqOff + 2 > fisz)
                        continue;

                    ushort ntmp = ReadU16BE(ram, ntmpOff);
                    ushort aseq = ReadU16BE(ram, aseqOff);

                    int offseq = pos + (int)atrack + aseq;

                    // test 3: tempo offset consistency
                    if (!(offseq < fisz && 8 * ntmp + 0x8 == aseq))
                        continue;

                    // walk commands until end-of-track (0x83) or out-of-bounds
                    int walk = offseq;
                    while (walk < fisz)
                    {
                        int com = ram[walk];
                        int step = comlen[com]; // safe: com is 0..255, table has 256 entries
                        walk += step;
                        if (com == 0x83) break;
                    }

                    if (walk <= pos || walk > fisz)
                        continue;

                    int length = walk - pos;
                    var seg = new byte[length];
                    Array.Copy(ram, pos, seg, 0, length);
                    extracted.Add(seg);

                    // advance pos to last byte of extracted chunk (mimic original behaviour)
                    pos = walk - 1;
                }
            }
            catch
            {
                // return whatever we found so far on error
                return [.. extracted];
            }

            return [.. extracted];
        }

        static VFSFile2[] GetVFSFiles(string dir, string pattern = "*.ssf", Encoding? enc = null, 
            bool verbose = false, StreamWriter? con = null, bool usemd5 = true, XsfType? xpattern = null,
            bool add_direct_files = true) //BinaryType? bpattern = null,
        {
            xpattern ??= XsfType.ANY;
            List<VFSFile2> xsffiles = [];
            List<VFSFile2> listfiles = [];
            Dictionary<string, int> xsfmd5 = []; //key = md5 or filename, value = index in xsffiles
            con ??= new(Console.OpenStandardOutput());
            con.AutoFlush = true;
            foreach (string file in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
            {
                try
                {
                    if (pattern == "*.*")
                    {
                        
                        switch (xpattern.Value)
                        {
                            case XsfType.SSF:
                                if (!(Path.GetExtension(file).Equals(".ssf", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetExtension(file).Equals(".minissf", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetExtension(file).Equals(".ssfbin", StringComparison.OrdinalIgnoreCase)))
                                {
                                    continue;
                                }
                                break;
                            case XsfType.DSF:
                                if (!(Path.GetExtension(file).Equals(".ssf", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetExtension(file).Equals(".minissf", StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetExtension(file).Equals(".dsfbin", StringComparison.OrdinalIgnoreCase)))
                                {
                                    continue;
                                }
                                break;
                            case XsfType.ANY:
                                if (!(Path.GetExtension(file).EndsWith("sf", StringComparison.OrdinalIgnoreCase) || //includes mini and xsfs longer than 3 chars like ncsf
                                    Path.GetExtension(file).EndsWith("sfbin", StringComparison.OrdinalIgnoreCase)))
                                {
                                    continue;
                                }
                                break;
                        }
                    }
                    if (verbose)
                    {
                        con.WriteLine("Loading {0}...", Path.GetFullPath(file));
                    }
                    XsfTable table = LoadFile(file, enc: enc, getmd5: usemd5, loadfile: false);
                    VFSFile2 info = new()
                    {
                        libs = [],
                        filetype = table.ftype switch
                        {
                            XsfType.SSF => 0xFFFFFF18,
                            XsfType.DSF => 0xFFFFFF19,
                            _ => 0
                        }
                    };
                    int primary_file = 1;
                    foreach ((int idx, XsfFile x) in table.minixsfs.Index())
                    {
                        XsfTable xtable = LoadFile(x.filename, enc: enc, getmd5: usemd5, loadlibs: false);
                        string fmd5 = usemd5 ? xtable.minixsfs[0].md5 : xtable.minixsfs[0].filename;
                        
                        if (xsfmd5.TryGetValue(fmd5, out int xsf))
                        {
                            info.libs.Add(xsf);
                        }
                        else
                        {
                            xsfmd5.Add(fmd5, xsffiles.Count);
                            info.libs.Add(xsffiles.Count);
                            xsffiles.Add(new VFSFile2
                            {
                                load_direct = false,
                                source = xtable.minixsfs[0].filename,
                                name = Path.GetFileName(xtable.minixsfs[0].filename), //FindName(xtable.minixsfs[0], enc),
                                filetype = GetBinOffset(xtable.minixsfs[0].headersect, xtable.ftype),
                                file_start = (int)xtable.minixsfs[0].start,
                                file_end = (int)xtable.minixsfs[0].end,
                                dont_load_xsflibs = true
                            });
                        }
                        if (!x.is_library)
                        {
                            primary_file = -idx; //xsfmd5[fmd5];
                            info.name = FindName(x, enc);
                        }
                    }
                    if (info.libs.Count > 0 && !string.IsNullOrEmpty(info.name))
                    {
                        info.libs.Add(primary_file);
                        listfiles.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.ToString());
                }
            }

            if (add_direct_files)
            {
                if (xpattern.Value == XsfType.SSF || xpattern.Value == XsfType.ANY)
                {
                    foreach (string f in Directory.GetFiles(dir, "vgm68.bin", SearchOption.AllDirectories))
                    {
                        xsffiles.Add(new VFSFile2
                        {
                            load_direct = true,
                            name = "VGM/MOD/MDX 68K Driver",
                            filetype = 0xFFFFFF1A,
                            source = Path.GetFullPath(f)
                        });
                    }
                    foreach (string f in Directory.GetFiles(dir, "*.mod", SearchOption.AllDirectories))
                    {
                        listfiles.Add(new VFSFile2
                        {
                            load_direct = true,
                            name = Path.GetFileNameWithoutExtension(f),
                            filetype = 0xFFFFFF1B,
                            source = Path.GetFullPath(f)
                        });
                    }
                    foreach (string f in Directory.GetFiles(dir, "*.vgm", SearchOption.AllDirectories))
                    {
                        listfiles.Add(new VFSFile2
                        {
                            load_direct = true,
                            name = Path.GetFileNameWithoutExtension(f),
                            filetype = 0xFFFFFF1C,
                            source = Path.GetFullPath(f)
                        });
                    }
                    foreach (string f in Directory.GetFiles(dir, "*.mdx", SearchOption.AllDirectories))
                    {
                        listfiles.Add(new VFSFile2
                        {
                            load_direct = true,
                            name = Path.GetFileNameWithoutExtension(f),
                            filetype = 0xFFFFFF1D,
                            source = Path.GetFullPath(f)
                        });
                    }
                }
            }
            xsffiles.AddRange(listfiles);
            return [.. xsffiles];
        }

        static XsfTable LoadFile(string filename, BinaryType? btype = null, XsfType? xtype = null, 
            Encoding? enc = null, bool loadlibs = true, bool getmd5 = false, bool loadfile = true, bool naomi = true)
        {
            XsfTable xt = new()
            {
                minixsfs = []
            };
            try
            {
                FileStream fs = new(filename, FileMode.Open);
                BinaryReader br = new(fs);
                uint ftype = br.ReadUInt32();
                if (!btype.HasValue)
                {
                    btype = ftype switch
                    {
                        //SSF, DSF
                        0x11465350 or 0x12465350 => (BinaryType?)BinaryType.MINIXSF,//not making a redundant loader for single xsf
                        _ => (BinaryType?)BinaryType.BIN,
                    };
                }
                if (!xtype.HasValue)
                {
                    xtype = ftype switch
                    {
                        //SSF
                        0x11465350 => (XsfType?)XsfType.SSF,
                        //DSF
                        0x12465350 => (XsfType?)XsfType.DSF,
                        < 0x80000 => (XsfType?)XsfType.SSF, //SSF max size
                        _ => (XsfType?)XsfType.DSF,//Bigger than SSF is DSF by default, if GSF/other support added this needs to change
                    };
                }
                switch (btype)
                {
                    case BinaryType.BIN:
                        xt.btype = BinaryType.BIN;
                        xt.ftype = xtype.Value;
                        br.BaseStream.Seek(0, SeekOrigin.Begin);
                        xt.ram = loadfile ? br.ReadBytes((int)fs.Length) : new byte[2048]; //LARGEST HEADER SIZE
                        switch (xtype.Value)
                        {
                            case XsfType.SSF:
                            case XsfType.DSF:
                                var info = new XsfFile
                                {
                                    filename = Path.GetFullPath(filename),
                                    headersect = [.. xt.ram.Take(HeaderSize(xtype.Value))],
                                    start = (uint)HeaderSize(xtype.Value),
                                    end = (uint)fs.Length,
                                    crc = 0,
                                    reserved_area = [],
                                    tags = [],
                                    tag_encoding = enc?.WebName ?? "utf-8",
                                    is_library = false
                                };
                                xt.minixsfs.Add(info);
                                break;
                        }
                        br.Dispose();
                        fs.Dispose();
                        break;
                    case BinaryType.MINIXSF:
                    case BinaryType.XSF:
                        br.Dispose();
                        fs.Dispose();
                        xt.ftype = xtype.Value;
                        switch (xtype.Value)
                        {
                            case XsfType.SSF:
                                xt.ram = new byte[0x80004];
                                break;
                            case XsfType.DSF:
                                xt.ram = new byte[naomi ? 0x800004 : 0x200004];
                                break;
                            default:
                                Console.Error.WriteLine("Unsupported xSF type!");
                                break;
                        }
                        xt = LoadMiniXsf(filename, xt, enc, false, loadlibs, getmd5, loadfile);
                        if (loadfile)
                        {
                            uint lowest = xt.minixsfs.Min(x => x.start);
                            uint highest = xt.minixsfs.Max(x => x.end);
                            byte[] mem = new byte[highest - lowest + xt.minixsfs.First().headersect.Length];
                            Array.Copy(xt.ram, lowest, mem, xt.minixsfs.First().headersect.Length, highest - lowest);
                            if (xt.minixsfs.First().headersect.Length == 4)
                            {
                                Array.Copy(BitConverter.GetBytes(lowest - 4), 0, mem, 0, 4);
                            }
                            else
                            {
                                Console.Error.WriteLine("Unsupported xSF Type - Header not 4 bytes!");
                            }
                            for (int i = 0; i < xt.minixsfs.Count; i++)
                            {
                                XsfFile xf = xt.minixsfs[i]; //foreach worked the last time
                                xf.start -= lowest - (uint)xt.minixsfs.First().headersect.Length;
                                xf.end -= lowest - (uint)xt.minixsfs.First().headersect.Length;
                                xt.minixsfs[i] = xf;
                            }
                            xt.ram = mem;
                        }
                        break;
                    default:
                        Console.Error.WriteLine("Unsupported filetype!");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error loading file: " + ex.Message);
            }
            return xt;
        }
        static XsfTable LoadMiniXsf(string filename, XsfTable xtab, Encoding? enc = null, 
            bool islib = false, bool loadlibs = true, bool getmd5 = false, bool loadfile = true)
        {
            BinaryReader binary = new(new FileStream(filename, FileMode.Open));
            binary.BaseStream.Seek(4, SeekOrigin.Begin);
            XsfFile info = new()
            {
                filename = Path.GetFullPath(filename)
            };
            //byte[] tempram = new byte[xtab.ram.Length];
            int rsize = binary.ReadInt32();
            int psize = binary.ReadInt32();
            
            if (loadlibs)
            {
                //string[] libraries = Xsflibs(binary, 16 + psize + rsize, enc, true, false);
                foreach (string l in Xsflibs(binary, 16 + psize + rsize, enc, true, false))
                {
                    LoadMiniXsf(Path.Join(Path.GetDirectoryName(filename), l), xtab, enc, true, true, getmd5, loadfile);
                }
            }
            if (loadfile)
            {
                binary.BaseStream.Seek(12, SeekOrigin.Begin);
                info.crc = binary.ReadUInt32();
                info.modified = false;
                info.reserved_area = binary.ReadBytes(rsize);
                ZLibStream zlib = new(binary.BaseStream, CompressionMode.Decompress);
                MemoryStream tempmem = new();
                zlib.CopyTo(tempmem); //sometimes this is longer than the ram size so it needs to be truncated
                byte[] tempram = tempmem.ToArray();
                if (getmd5) //would make this a (copied) function but doesnt look needed
                {
                    info.md5 = Convert.ToHexString(MD5.HashData(tempram));
                }
                //int bytesread = tempram.Length;
                binary.BaseStream.Seek(16 + psize + rsize, SeekOrigin.Begin);
                try
                {
                    enc ??= CharsetDetector.DetectFromStream(binary.BaseStream).Detected.Encoding;
                }
                catch
                {
                    enc ??= Encoding.UTF8;
                }
                info.tag_encoding = enc.WebName;
                binary.BaseStream.Seek(16 + psize + rsize, SeekOrigin.Begin);
                info.tags = binary.ReadBytes((int)binary.BaseStream.Length - (int)binary.BaseStream.Position);
                switch (xtab.ftype)
                {
                    case XsfType.SSF:
                    case XsfType.DSF:
                        info.headersect = [.. tempram.Take(HeaderSize(xtab.ftype))];
                        info.start = BitConverter.ToUInt32(info.headersect, 0) + (uint)HeaderSize(xtab.ftype);
                        info.end = uint.Min(info.start + (uint)(tempram.Length - HeaderSize(xtab.ftype)), (uint)xtab.ram.Length);
                        break;
                    default:
                        Console.Error.WriteLine("Unsupported xSF type!");
                        return xtab;
                }
                Array.Copy(tempram, info.headersect.Length, xtab.ram, info.start, int.Min(tempram.Length - info.headersect.Length, (int)(xtab.ram.Length - info.start)));
                binary.BaseStream.Seek(16 + rsize, SeekOrigin.Begin);
                tempram = binary.ReadBytes(psize);
                if (BitConverter.ToUInt32(Crc32.Hash(tempram)) != info.crc)
                {
                    Console.Error.WriteLine("{0}: Wrong CRC!", filename);
                }
                zlib.Dispose();
            }
            else
            {
                binary.BaseStream.Seek(16 + psize + rsize, SeekOrigin.Begin);
                info.tags = binary.ReadBytes((int)binary.BaseStream.Length - (int)binary.BaseStream.Position);
            }
            info.is_library = islib;
            xtab.minixsfs.Add(info);
            //zlib.Close();
            if (loadlibs)
            {
                //string[] libraries = Xsflibs(new BinaryReader(new MemoryStream(info.tags)), 0, enc, false, true);
                foreach (string l in Xsflibs(new BinaryReader(new MemoryStream(info.tags)), 0, enc, false, true))
                {
                    LoadMiniXsf(Path.Join(Path.GetDirectoryName(filename), l), xtab, enc, true, true, getmd5, loadfile);
                }
            }
            binary.Dispose();
            return xtab;
        }

        static string[] Xsflibs(BinaryReader br, int tagpos, Encoding? enc = null, bool mainlib = true, bool auxlib = true)
        {
            try
            {
                //enc ??= Encoding.UTF8;
                List<string> liblines = [];

                string lib = "";
                if (tagpos < 0)
                {
                    br.BaseStream.Seek(4, SeekOrigin.Begin);
                    uint rsize = br.ReadUInt32();
                    uint psize = br.ReadUInt32();
                    tagpos = (int)(16 + psize + rsize);
                }
                if (tagpos > (br.BaseStream.Length - 5))
                {
                    return [];
                }
                br.BaseStream.Seek(tagpos, SeekOrigin.Begin);
                uint tagsig = br.ReadUInt32();

                if (tagsig == 0x4741545B && br.ReadByte() == 0x5D)
                {
                    try
                    {
                        enc ??= CharsetDetector.DetectFromStream(br.BaseStream).Detected.Encoding;
                    }
                    catch
                    {
                        enc ??= Encoding.UTF8;
                    }
                    br.BaseStream.Seek(tagpos + 5, SeekOrigin.Begin);
                    StreamReader sr = new(br.BaseStream, enc);
                    while (sr.Peek() >= 0)
                    {
                        try
                        {
                            lib = sr.ReadLine() ?? string.Empty;
                            if (lib.StartsWith("_lib", StringComparison.OrdinalIgnoreCase))
                            {

                                if (mainlib && lib.StartsWith("_lib="))
                                {
                                    if (auxlib)
                                    {
                                        liblines.Add("_lib0=" + lib.Split('=', StringSplitOptions.RemoveEmptyEntries));
                                    }
                                    else
                                    {
                                        liblines.Add(lib);
                                    }

                                }
                                else if (auxlib && int.TryParse(lib.Split('=')[0][4..], out int val))
                                {
                                    if (val > 1)
                                    {
                                        liblines.Add(lib);
                                    }
                                }
                            }
                        }
                        catch (Exception tx)
                        {
                            Console.Error.WriteLine("Exception: {0}", tx.Message);
                            Console.Error.WriteLine("{0} was not a valid tag line", lib);
                        }

                    }
                }
                //if (mainlib) liblines.Sort();
                liblines.Sort(Comparer<string>.Create(
                    (x, y) => int.Parse(x.Split('=')[0][4..]).CompareTo(int.Parse(y.Split('=')[0][4..]))
                    ));
                List<string> libs = [];

                foreach (string ls in liblines)
                {
                    try
                    {
                        libs.Add(ls.Split('=', StringSplitOptions.RemoveEmptyEntries)[1]);
                    }
                    catch (Exception lx)
                    {
                        Console.Error.WriteLine("{0} was not a valid library", ls);
                        Console.Error.WriteLine("Exception: {0}", lx.Message);
                    }
                }
                return [.. libs];
            }
            catch (Exception mx)
            {
                Console.Error.WriteLine("Tag Exception: {0}", mx.Message);
            }
            return [];
        }

        static int SaveMiniXsf(XsfTable xsfTable, string[]? fn = null, int xn = -1, Encoding? enc = null)
        {
            if (xn == -1)
            {
                xn = xsfTable.minixsfs.FindLastIndex(x => !x.is_library);
            }
            fn ??= [];
            if (fn.Length < xsfTable.minixsfs.Count)
            {
                Array.Resize(ref fn, xsfTable.minixsfs.Count);
            }
            switch (xsfTable.btype)
            {
                case BinaryType.BIN:
                    if (string.IsNullOrEmpty(fn[0]))
                    {
                        fn[0] = xsfTable.ftype switch
                        {
                            XsfType.SSF => Path.GetFileNameWithoutExtension(xsfTable.minixsfs.Where(x => !x.is_library).Last().filename) + ".ssfbin",
                            XsfType.DSF => Path.GetFileNameWithoutExtension(xsfTable.minixsfs.Where(x => !x.is_library).Last().filename) + ".dsfbin",
                            _ => Path.GetFileNameWithoutExtension(xsfTable.minixsfs.Where(x => !x.is_library).Last().filename) + ".bin",
                        };
                    }
                    try
                    {
                        File.WriteAllBytes(fn[0], xsfTable.ram);
                        return 1;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Error saving file: " + ex.Message);
                        return 0;
                    }
                    //break;
                case BinaryType.XSF:
                    if (string.IsNullOrEmpty(fn[0]))
                    {
                        fn[0] = Path.GetFileNameWithoutExtension(xsfTable.minixsfs.Where(x => !x.is_library).Last().filename) + ".ssf";
                    }
                    try
                    {
                        XsfFile xf = xsfTable.minixsfs[xn];
                        xf.modified = true;
                        Array.Copy(xsfTable.ram, xf.headersect, xf.headersect.Length);
                        Encoding e;
                        try
                        {
                            e = Encoding.GetEncoding(xsfTable.minixsfs[xn].tag_encoding);
                        }
                        finally { }
                        if (enc != null)
                        {
                            e = enc;
                        }
                        xf.tags = RemoveLibTags(xf.tags, e);
                        xf.start = (uint)xf.headersect.Length;
                        xf.end = (uint)xsfTable.ram.Length;
                        if (SaveXsfFile(xsfTable.ram, xf, xsfTable.ftype, fn[0]))
                        {
                            return 1;
                        }
                        else
                        {
                            Console.Error.WriteLine("Error saving xSF file!");
                            return 0;
                        }
                    }
                    catch (Exception ex) 
                    {
                        Console.Error.WriteLine("Error preparing xSF file for saving: " + ex.Message);
                        return 0;
                    }
                    //break;
                case BinaryType.MINIXSF:
                    int i = 0;
                    foreach (XsfFile xf in xsfTable.minixsfs.Where(x => x.modified))
                    {
                        if (SaveXsfFile(xsfTable.ram, xf, xsfTable.ftype, string.IsNullOrEmpty(fn[i]) ? xf.filename : fn[i]))
                        {
                            i++;
                        }
                    }
                    return i;
                    //break;

            }
            return 0;
        }

        static bool SaveXsfFile(byte[] ram, XsfFile xsfFile, XsfType type, string? fn = null)
        {
            xsfFile.reserved_area ??= [];
            xsfFile.tags ??= [];
            if (string.IsNullOrEmpty(fn))
            {
                fn = Path.GetFileName(xsfFile.filename);
            }
            BinaryWriter bw = new(new FileStream(fn, FileMode.Create));
            switch (type)
            {
                case XsfType.SSF:
                    bw.Write(0x11465350); //SSF
                    break;
                case XsfType.DSF:
                    bw.Write(0x12465350); //DSF
                    break;
                default:
                    Console.Error.WriteLine("Unsupported xSF type!");
                    return false;
            }
            bw.Write(xsfFile.reserved_area.Length);
            MemoryStream mem = new();
            ZLibStream zlib = new(mem, CompressionLevel.SmallestSize, true);
            zlib.Write(xsfFile.headersect);
            zlib.Write(ram, (int)xsfFile.start, (int)(xsfFile.end - xsfFile.start));
            zlib.Flush();
            zlib.Close(); //still needed for some reason
            byte[] tempram = mem.ToArray();
            bw.Write(tempram.Length);
            bw.Write(BitConverter.ToUInt32(Crc32.Hash(tempram)));
            bw.Write(xsfFile.reserved_area);
            bw.Write(tempram);

            zlib.Dispose();
            mem.Dispose();

            bw.Write(xsfFile.tags);
            bw.Flush();
            bw.Dispose();

            return true;
        }

        static byte[] RemoveLibTags(byte[] data, Encoding? enc = null, List<string>? liblines = null,
            bool keeplibs = false, bool replacetags = true, Encoding? outenc = null, string tagnewline = "\n")
        {
            try
            {
                enc ??= CharsetDetector.DetectFromBytes(data).Detected.Encoding;
                outenc ??= enc;
            }
            catch
            {
                enc ??= Encoding.UTF8;
                outenc ??= Encoding.UTF8;
            }
            if (string.IsNullOrEmpty(tagnewline)) //make default this?
            {
                tagnewline = Environment.NewLine;
            }
            liblines ??= [];
            List<string> rtags = [];
            if (replacetags)
            {
                rtags = [.. liblines.Select(x => x.Split('=', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() + '=')];
            }
            if (!keeplibs)
            {
                rtags.Add("_lib");
            }

            try
            {
                StreamReader sr = new(new MemoryStream(data), enc);
                BinaryReader br = new(sr.BaseStream);
                string lib = "";
                uint tagsig = 0;
                if (data.Length > 4)
                {
                    tagsig = br.ReadUInt32();
                }
                if (tagsig == 0x4741545B && br.ReadByte() == 0x5D)
                {
                    while (sr.Peek() >= 0)
                    {
                        try
                        {
                            lib = sr.ReadLine() ?? string.Empty;

                            if (!rtags.Any(x => lib.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                            {
                                liblines.Add(lib);
                            }
                        }
                        catch (Exception tx)
                        {
                            Console.Error.WriteLine("Exception: {0}", tx.Message);
                            Console.Error.WriteLine("{0} was not a valid tag line", lib);
                        }
                    }
                }
                sr.Dispose();
                br.Dispose();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Tag error: {e.Message}");
            }


            string tagtext = "[TAG]" + string.Join(tagnewline, liblines);
            return outenc.GetBytes(tagtext);
        }

        static string FindName(XsfFile psfFile, Encoding? enc = null)
        {
            try
            {
                enc ??= Encoding.GetEncoding(psfFile.tag_encoding);
            }
            catch
            {
                //Console.Error.WriteLine("No encoding found for tags, autodetecting...");
            }
            try
            {
                enc ??= CharsetDetector.DetectFromBytes(psfFile.tags).Detected.Encoding;
            }
            catch
            {
                //Console.Error.WriteLine("No encoding could be autodetected, using UTF8!");
                enc = Encoding.UTF8;
            }
            try
            {
                if (psfFile.tags == null || psfFile.tags.Length < 5)
                {
                    return Path.GetFileNameWithoutExtension(psfFile.filename);
                }
                BinaryReader br = new(new MemoryStream(psfFile.tags));
                StreamReader sr = new(br.BaseStream, enc);
                uint tagsig = br.ReadUInt32();
                string lib = "";
                string fname = "";
                if (tagsig == 0x4741545B && br.ReadByte() == 0x5D)
                {
                    while (sr.Peek() >= 0)
                    {
                        try
                        {
                            lib = sr.ReadLine() ?? "";
                            if (lib.StartsWith("title", StringComparison.OrdinalIgnoreCase))
                            {
                                fname = lib.Split('=', StringSplitOptions.RemoveEmptyEntries)[1];
                            }
                        }
                        catch (Exception tx)
                        {
                            Console.Error.WriteLine("Exception: {0}", tx.Message);
                            Console.Error.WriteLine("{0} was not a valid tag line", lib);
                        }

                    }
                }
                if (string.IsNullOrEmpty(fname))
                {
                    return Path.GetFileNameWithoutExtension(psfFile.filename); //if both are null returns null
                }
                else
                {
                    return fname;
                }
            }
            catch (Exception cx)
            {
                Console.Error.WriteLine("Tag Field Exception: {0}", cx.Message);
                try
                {
                    return Path.GetFileNameWithoutExtension(psfFile.filename);
                }
                catch
                {
                    return "Unknown";
                }
            }
        }

        static uint GetBinOffset(byte[] headersect, XsfType type, bool AddBase = true)
        {
            uint offset = 0;
            if (AddBase) //this makes gsf easier because there are 2 entry points
            {
                offset = type switch
                {
                    XsfType.SSF => 0x05A00000,
                    XsfType.DSF => 0xA0800000,
                    _ => 0
                };
            }
            switch (type)
            {
                case XsfType.SSF:
                case XsfType.DSF:
                    if (headersect.Length < HeaderSize(type))
                    {
                        throw new ArgumentException($"Header section must be at least {HeaderSize(type)} bytes long.");
                    }
                    return BitConverter.ToUInt32(headersect, 0) + offset;
                default:
                    throw new ArgumentException("Unsupported xSF type!");
            }
            //return 0;
        }

        static byte[] GetHeaderSect(uint offset, XsfType type, bool SubtractOffset = false)
        {
            if (SubtractOffset)
            {
                offset -= type switch
                {
                    XsfType.SSF => 0x05A00000,
                    XsfType.DSF => 0xA0800000,
                    _ => 0
                };
            }
            return type switch
            {
                XsfType.SSF or XsfType.DSF => BitConverter.GetBytes(offset),
                _ => throw new ArgumentException("Unsupported xSF type!"),
            };
        }
        static int GetPadding(int base_addr, int sector = 2048)
        {
            int base_pad = sector - (base_addr % sector);
            if (base_pad == sector)
            {
                return 0;
            }
            else
            {
                return base_pad;
            }
        }

        static void SaveVFSFile(string filename, VFSFile2[] files, Encoding? encout = null, bool naomi = true)
        {
            encout ??= Encoding.ASCII;
            BinaryWriter writer = new(new FileStream(filename, FileMode.Create));
            int base_addr = 12 + (files.Length * 84);
            int base_pad = GetPadding(base_addr);
            int addr = base_addr + base_pad;

            for (int i = 0; i < files.Length; i++)
            {
                files[i].libs ??= [];
                if (files[i].load_direct)
                {
                    files[i].binary.size = (int)new FileInfo(files[i].source).Length;
                    files[i].file_start = 0;
                    files[i].file_end = files[i].binary.size;
                }
                else if (files[i].libs.Count > 0)
                {
                    //files[i].binary.data = [.. files[i].libs.SelectMany(BitConverter.GetBytes)];
                    files[i].binary.size = files[i].libs.Count * sizeof(int); //files[i].binary.data.Length;
                }
                else
                {
                    files[i].binary.size = files[i].file_end - files[i].file_start;
                }
                files[i].binary.name = new byte[64];
                encout.GetEncoder().Convert(files[i].name.ToCharArray(), 0, files[i].name.Length, files[i].binary.name, 0, files[i].binary.name.Length - 1, true, out _, out _, out _);
                files[i].binary.addr = addr;
                files[i].binary.padding = GetPadding(files[i].binary.size);
                addr += files[i].binary.size + files[i].binary.padding;
            }

            writer.Write(0x00534656); //VFS
            writer.Write(files.Length);
            writer.Write((base_addr + base_pad) / 2048);

            foreach (VFSFile2 hfile in files)
            {
                writer.Write(hfile.binary.name);
                writer.Write(hfile.binary.size);
                writer.Write(hfile.binary.addr / 2048);
                writer.Write((hfile.binary.size + hfile.binary.padding) / 2048);
                writer.Write(hfile.binary.addr);
                writer.Write(hfile.filetype);
            }
            writer.Write(new byte[base_pad]);

            foreach (VFSFile2 f in files)
            {
                if (f.load_direct)
                {
                    //byte[] direct_vfs = File.ReadAllBytes(f.source);
                    writer.Write(File.ReadAllBytes(f.source));
                }
                else if (f.libs.Count > 0)
                {
                    writer.Write(f.libs.SelectMany(BitConverter.GetBytes).ToArray());
                }
                else
                {
                    //XsfTable table = LoadFile(f.source, loadlibs: !f.dont_load_xsflibs, naomi: naomi);
                    writer.Write(LoadFile(f.source, loadlibs: !f.dont_load_xsflibs, naomi: naomi).ram, f.file_start, f.binary.size);
                }
                writer.Write(new byte[f.binary.padding]);
            }
            writer.Flush();
            writer.Close();
            writer.Dispose();

            return;
        }

        static int HeaderSize(XsfType type)
        {
            return type switch
            {
                XsfType.SSF or XsfType.DSF => 4,
                _ => 0
            };
        }

        static void ExtractMergeVFS(string filename, BinaryType type = BinaryType.XSF, 
            string[]? outfiles = null, Encoding? enc = null, Encoding? encout = null,
            bool extract_all = false) //, bool smallest_lib = true) // string? outdir = null)
        {
            enc ??= Encoding.ASCII;
            encout ??= Encoding.UTF8;
            BinaryReader vbr = new(new FileStream(filename, FileMode.Open));
            if (vbr.ReadUInt32() != 0x00534656)
            {
                Console.Error.WriteLine("Not a valid VFS file!");
                return;
            }
            int filecount = vbr.ReadInt32();
            bool[] dont_extract = new bool[filecount];
            //uint[] crcs = new uint[filecount];
            //int base_addr = vbr.ReadInt32() * 2048;
            vbr.ReadInt32(); //base addr, not needed since libs have absolute addresses
            int done_out_files = 0;
            if (!extract_all)
            {
                for (int i = 0; i < filecount; i++)
                {
                    try
                    {
                        vbr.BaseStream.Seek(12 + (i * 84), SeekOrigin.Begin);
                        string fname = enc.GetString(vbr.ReadBytes(64)).TrimEnd('\0');
                        int bsize = vbr.ReadInt32();
                        int saddr = vbr.ReadInt32();
                        int ssize = vbr.ReadInt32();
                        int baddr = vbr.ReadInt32();
                        uint ftype = vbr.ReadUInt32();
                        if (ftype == 0xFFFFFF18 || ftype == 0xFFFFFF19)
                        {
                            uint lowest = uint.MaxValue;
                            uint highest = uint.MinValue;
                            dont_extract[i] = true;
                            vbr.BaseStream.Seek(baddr, SeekOrigin.Begin);
                            XsfTable xsf = new()
                            {
                                btype = type,
                                ftype = ftype switch
                                {
                                    0xFFFFFF18 => XsfType.SSF,
                                    0xFFFFFF19 => XsfType.DSF,
                                    _ => XsfType.ANY
                                },
                                minixsfs = []
                            };
                            int[] ints = new int[(bsize - 4) / 4];
                            string[] strings = new string[ints.Length];
                            vbr.BaseStream.Seek(baddr + (bsize - 4), SeekOrigin.Begin);
                            int primary_lib = -vbr.ReadInt32();

                            for (int j = 0; j < ints.Length; j++)
                            {
                                vbr.BaseStream.Seek(baddr + (j * 4), SeekOrigin.Begin);
                                ints[j] = vbr.ReadInt32();
                                vbr.BaseStream.Seek(12 + (ints[j] * 84) + 80, SeekOrigin.Begin);
                                uint libftype = vbr.ReadUInt32();
                                if (libftype < lowest)
                                {
                                    lowest = libftype;
                                }
                                vbr.BaseStream.Seek(12 + (ints[j] * 84) + 64, SeekOrigin.Begin);
                                uint libend = vbr.ReadUInt32() + libftype;
                                if (libend > highest)
                                {
                                    highest = libend;
                                }
                                vbr.BaseStream.Seek(12 + (ints[j] * 84), SeekOrigin.Begin);

                                strings[j] = xsf.ftype switch
                                {
                                    XsfType.SSF => j == primary_lib ? fname + ".minissf"
                                    : Path.GetFileNameWithoutExtension(enc.GetString(vbr.ReadBytes(64)).TrimEnd('\0')) + ".ssflib",
                                    XsfType.DSF => j == primary_lib ? fname + ".minidsf"
                                    : Path.GetFileNameWithoutExtension(enc.GetString(vbr.ReadBytes(64)).TrimEnd('\0')) + ".dsflib",
                                    _ => strings[j]
                                };
                            }
                            xsf.ram = new byte[highest - lowest + HeaderSize(xsf.ftype)];
                            
                            for (int j = 0; j < ints.Length; j++)
                            {
                                vbr.BaseStream.Seek(12 + (ints[j] * 84), SeekOrigin.Begin);
                                //string libname = enc.GetString(vbr.ReadBytes(64)).TrimEnd('\0');
                                vbr.BaseStream.Seek(12 + (ints[j] * 84) + 76, SeekOrigin.Begin);
                                int seekaddr = vbr.ReadInt32();
                                uint hsaddr = vbr.ReadUInt32();
                                uint lib_load_addr = hsaddr - lowest + (uint)HeaderSize(xsf.ftype);
                                vbr.BaseStream.Seek(12 + (ints[j] * 84) + 64, SeekOrigin.Begin);
                                int rsize = vbr.ReadInt32();
                                vbr.BaseStream.Seek(seekaddr, SeekOrigin.Begin);

                                vbr.Read(xsf.ram, (int)lib_load_addr, rsize);
                                byte[] hsect = GetHeaderSect(hsaddr, xsf.ftype, true);
                                /*if (!dont_extract[ints[j]])
                                {
                                    byte[] crcram = new byte[rsize + HeaderSize(xsf.ftype)];
                                    Array.Copy(hsect, 0, crcram, 0, HeaderSize(xsf.ftype));
                                    Array.Copy(xsf.ram, lib_load_addr, crcram, HeaderSize(xsf.ftype), rsize);
                                    crcs[ints[j]] = BitConverter.ToUInt32(Crc32.Hash(crcram));
                                }*/
                                byte[] tags = encout.GetBytes("[TAG]");
                                if (j < 1 || j > primary_lib)
                                {
                                    //tags = encout.GetBytes("[TAG]");
                                }
                                else if (j < primary_lib)
                                {
                                    tags = encout.GetBytes($"[TAG]_lib={strings[j - 1]}");
                                }
                                else if (j == primary_lib)
                                {
                                    List<string> liblines = [];
                                    liblines.Add($"_lib={strings[j - 1]}");
                                    for (int k = j + 1; k < ints.Length; k++)
                                    {
                                        liblines.Add($"_lib{k - j + 1}={strings[k]}");
                                    }
                                    tags = encout.GetBytes("[TAG]" + string.Join("\n", liblines));
                                }
                                //xsf.minixsfs ??= [];
                                xsf.minixsfs.Add(new()
                                {
                                    filename = strings[j],
                                    headersect = hsect,
                                    start = lib_load_addr,
                                    end = lib_load_addr + (uint)rsize,
                                    //crc = crcs[ints[j]],
                                    is_library = j != primary_lib,
                                    modified = true,
                                    tag_encoding = encout.WebName,// ?? "utf-8",
                                    tags = tags
                                });
                                dont_extract[ints[j]] = true;
                            }
                            outfiles ??= [];
                            if (done_out_files <= outfiles.Length)
                            {
                                SaveMiniXsf(xsf, outfiles[done_out_files..]);
                            }
                            else
                            {
                                SaveMiniXsf(xsf, outfiles);
                            }
                            done_out_files += xsf.minixsfs.Count;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Error processing file {0}: {1}", i, ex.Message);
                    }
                }
            }
        }
    }
}
