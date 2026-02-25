namespace SSFExt
{
    internal class KS
    {
        static byte[][] KS_tonext(byte[] ram)
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

        static byte[][] KS_seqext(byte[] ram)
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
    }


}
