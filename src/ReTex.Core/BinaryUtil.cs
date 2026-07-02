namespace ReTex.Core;

/// <summary>Small binary-decoding primitives shared across the rapified-config and ODOL p3d readers.</summary>
internal static class BinaryUtil
{
    /// <summary>7-bit LEB128-style compressed integer, as used by both rapified configs and ODOL p3d lumps.</summary>
    public static int ReadCompressedInt(byte[] d, ref int pos)
    {
        int value = 0, shift = 0;
        byte b;
        do
        {
            b = d[pos++];
            value |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return value;
    }
}
