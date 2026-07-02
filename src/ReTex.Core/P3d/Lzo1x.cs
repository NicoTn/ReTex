namespace ReTex.Core.P3d;

/// <summary>
/// Faithful port of the ODOL LZO1X decompressor (BI wiki "Compressed LZO File Format" =
/// oberhumer lzo1x_decompress_safe). Unlike a streaming LZO reader, this decodes exactly one
/// self-terminating LZO1X block filling <c>outLen</c> bytes and returns how many INPUT bytes it
/// consumed - which is what p3d array-walking needs (the arrays store no compressed length).
/// The goto-based control flow mirrors the reference source so it can be checked against it.
///
/// STATUS: VALIDATED. On Base_Gravis_Pack.p3d it decodes the VertexTable point block (offset
/// 136036) to exactly 93588 bytes = 7799 XYZTriplets whose coordinates all fall inside the
/// model bbox (e.g. first point (0.1906, 0.6627, 0.6820)); the UV (offset 121115 -> 31196 bytes)
/// and normal (178288 -> 31196 bytes) blocks also decode cleanly, and each block's reported
/// consumed-byte count lands exactly on the next field. Its first 16 output bytes match lzo.net
/// byte-for-byte on the same input (the difference was only that lzo.net buffers/overshoots).
/// The earlier "runs off buffer" symptom was a framing error (wrong start offset), not a port
/// bug: compressed arrays start after a count + optional DefaultFill byte + a 1-byte packing
/// flag (0x02 = LZO) - see ODOL_FORMAT_SPEC.md "EMPIRICALLY-CONFIRMED VertexTable framing".
/// </summary>
public static class Lzo1x
{
    private const int M2_MAX_OFFSET = 0x0800;

    /// <summary>Decompresses one LZO1X block at <paramref name="ip0"/> into a new buffer of
    /// <paramref name="outLen"/> bytes. Returns consumed input bytes (so <c>ip0 + consumed</c>
    /// is the next field). Throws if the block does not exactly fill the output.</summary>
    public static byte[] Decompress(byte[] input, int ip0, int outLen, out int consumed)
    {
        var outp = new byte[outLen];
        consumed = DecompressInto(input, ip0, outp, outLen);
        return outp;
    }

    /// <summary>Diagnostic: decode until the natural end-of-stream marker into an oversized
    /// buffer, reporting the output length reached and input bytes consumed. Does not require the
    /// expected output size (used to discover an array's true uncompressed length). Returns false
    /// if it runs off the buffers before hitting a marker.</summary>
    public static bool TryDecodeNatural(byte[] I, int ip0, int cap, out int outSize, out int consumed, out byte[] outBuf)
    {
        outBuf = new byte[cap];
        outSize = 0; consumed = 0;
        try { (outSize, consumed) = DecodeNatural(I, ip0, outBuf, cap); return true; }
        catch { return false; }
    }

    private static (int outSize, int consumed) DecodeNatural(byte[] I, int ip0, byte[] O, int outLen)
    {
        // identical to DecompressInto but the end marker returns at ANY op (not requiring op==outLen)
        int ip = ip0, op = 0, t, m;
        if ((I[ip] & 0xFF) > 17) { t = (I[ip++] & 0xFF) - 17; if (t < 4) goto match_next; do { O[op++] = I[ip++]; } while (--t > 0); goto first_literal_run; }
    main_loop:
        t = I[ip++] & 0xFF;
        if (t >= 16) goto match;
        if (t == 0) { while (I[ip] == 0) { t += 255; ip++; } t += 15 + (I[ip++] & 0xFF); }
        O[op++] = I[ip++]; O[op++] = I[ip++]; O[op++] = I[ip++]; O[op++] = I[ip++];
        if (--t > 0) do { O[op++] = I[ip++]; } while (--t > 0);
    first_literal_run:
        t = I[ip++] & 0xFF;
        if (t >= 16) goto match;
        m = op - (1 + M2_MAX_OFFSET); m -= t >> 2; m -= (I[ip++] & 0xFF) << 2;
        O[op++] = O[m++]; O[op++] = O[m++]; O[op++] = O[m]; goto match_done;
    match:
        if (t >= 64) { m = op - 1; m -= (t >> 2) & 7; m -= (I[ip++] & 0xFF) << 3; t = (t >> 5) - 1; goto copy_match; }
        else if (t >= 32) { t &= 31; if (t == 0) { while (I[ip] == 0) { t += 255; ip++; } t += 31 + (I[ip++] & 0xFF); } m = op - 1; m -= ((I[ip] & 0xFF) >> 2) + ((I[ip + 1] & 0xFF) << 6); ip += 2; }
        else if (t >= 16) { m = op; m -= (t & 8) << 11; t &= 7; if (t == 0) { while (I[ip] == 0) { t += 255; ip++; } t += 7 + (I[ip++] & 0xFF); } m -= ((I[ip] & 0xFF) >> 2) + ((I[ip + 1] & 0xFF) << 6); ip += 2; if (m == op) return (op, ip - ip0); m -= 0x4000; }
        else { m = op - 1; m -= t >> 2; m -= (I[ip++] & 0xFF) << 2; O[op++] = O[m++]; O[op++] = O[m]; goto match_done; }
        O[op++] = O[m++]; O[op++] = O[m++]; do { O[op++] = O[m++]; } while (--t > 0); goto match_done;
    copy_match:
        O[op++] = O[m++]; O[op++] = O[m++]; do { O[op++] = O[m++]; } while (--t > 0);
    match_done:
        t = I[ip - 2] & 3; if (t == 0) goto main_loop;
    match_next:
        O[op++] = I[ip++]; if (t > 1) { O[op++] = I[ip++]; if (t > 2) { O[op++] = I[ip++]; } } t = I[ip++] & 0xFF; goto match;
    }

    private static int DecompressInto(byte[] I, int ip0, byte[] O, int outLen)
    {
        int ip = ip0;
        int op = 0;
        int t;
        int m; // m_pos index into O

        if ((I[ip] & 0xFF) > 17)
        {
            t = (I[ip++] & 0xFF) - 17;
            if (t < 4) goto match_next;
            do { O[op++] = I[ip++]; } while (--t > 0);
            goto first_literal_run;
        }

    main_loop:
        t = I[ip++] & 0xFF;
        if (t >= 16) goto match;
        if (t == 0)
        {
            while (I[ip] == 0) { t += 255; ip++; }
            t += 15 + (I[ip++] & 0xFF);
        }
        // literal run of t+3 bytes
        O[op++] = I[ip++]; O[op++] = I[ip++]; O[op++] = I[ip++]; O[op++] = I[ip++];
        if (--t > 0)
            do { O[op++] = I[ip++]; } while (--t > 0);

    first_literal_run:
        t = I[ip++] & 0xFF;
        if (t >= 16) goto match;
        m = op - (1 + M2_MAX_OFFSET);
        m -= t >> 2;
        m -= (I[ip++] & 0xFF) << 2;
        O[op++] = O[m++]; O[op++] = O[m++]; O[op++] = O[m];
        goto match_done;

    match:
        if (t >= 64)
        {
            m = op - 1;
            m -= (t >> 2) & 7;
            m -= (I[ip++] & 0xFF) << 3;
            t = (t >> 5) - 1;
            goto copy_match;
        }
        else if (t >= 32)
        {
            t &= 31;
            if (t == 0)
            {
                while (I[ip] == 0) { t += 255; ip++; }
                t += 31 + (I[ip++] & 0xFF);
            }
            m = op - 1;
            m -= ((I[ip] & 0xFF) >> 2) + ((I[ip + 1] & 0xFF) << 6);
            ip += 2;
        }
        else if (t >= 16)
        {
            m = op;
            m -= (t & 8) << 11;
            t &= 7;
            if (t == 0)
            {
                while (I[ip] == 0) { t += 255; ip++; }
                t += 7 + (I[ip++] & 0xFF);
            }
            m -= ((I[ip] & 0xFF) >> 2) + ((I[ip + 1] & 0xFF) << 6);
            ip += 2;
            if (m == op)   // end-of-stream marker
            {
                if (op != outLen)
                    throw new InvalidDataException($"LZO block underfilled: {op} of {outLen}.");
                return ip - ip0;
            }
            m -= 0x4000;
        }
        else
        {
            m = op - 1;
            m -= t >> 2;
            m -= (I[ip++] & 0xFF) << 2;
            O[op++] = O[m++]; O[op++] = O[m];
            goto match_done;
        }

        // shared match copy (t+2 bytes), byte-by-byte (correct even when overlapping)
        O[op++] = O[m++]; O[op++] = O[m++];
        do { O[op++] = O[m++]; } while (--t > 0);
        goto match_done;

    copy_match:
        O[op++] = O[m++]; O[op++] = O[m++];
        do { O[op++] = O[m++]; } while (--t > 0);

    match_done:
        t = I[ip - 2] & 3;
        if (t == 0) goto main_loop;

    match_next:
        O[op++] = I[ip++];
        if (t > 1) { O[op++] = I[ip++]; if (t > 2) { O[op++] = I[ip++]; } }
        t = I[ip++] & 0xFF;
        goto match;
    }
}
