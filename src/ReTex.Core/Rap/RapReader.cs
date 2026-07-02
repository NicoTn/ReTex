using System.Text;

namespace ReTex.Core.Rap;

/// <summary>
/// Parses a rapified Arma config (config.bin, signature "\0raP") into a class tree.
/// No external tools required. Nested class bodies are stored at file offsets, so we
/// read with random access over the full byte buffer.
/// </summary>
public static class RapReader
{
    private static readonly byte[] Signature = { 0x00, (byte)'r', (byte)'a', (byte)'P' };

    public static bool IsRapified(byte[] data) =>
        data.Length >= 4 && data[0] == 0x00 && data[1] == (byte)'r' && data[2] == (byte)'a' && data[3] == (byte)'P';

    public static RapClass ParseFile(string path) => Parse(File.ReadAllBytes(path));

    public static RapClass Parse(byte[] data)
    {
        if (!IsRapified(data))
            throw new InvalidDataException("Not a rapified config (missing \\0raP signature).");

        var r = new Cursor(data) { Pos = 4 };
        _ = r.ReadU32();           // version (0)
        _ = r.ReadU32();           // always 8 (reserved)
        _ = r.ReadU32();           // enum offset (end block; unused here)

        var root = new RapClass { Name = "" };
        ReadClassBody(r, root, data.Length, 0);
        return root;
    }

    private static void ReadClassBody(Cursor r, RapClass cls, int dataLen, int depth)
    {
        if (depth > 512) throw new InvalidDataException("rap nesting too deep (malformed/cyclic).");

        cls.Parent = r.ReadAsciiZ();
        int n = r.ReadCompressedInt();
        if (n is < 0 or > 5_000_000) throw new InvalidDataException($"implausible rap entry count {n}.");
        for (int i = 0; i < n; i++)
        {
            byte type = r.ReadByte();
            switch (type)
            {
                case 0: // nested class -> name + offset to its body
                {
                    string name = r.ReadAsciiZ();
                    uint offset = r.ReadU32();
                    var child = new RapClass { Name = name };
                    if (offset > 0 && offset < dataLen)
                    {
                        int saved = r.Pos;
                        r.Pos = (int)offset;
                        ReadClassBody(r, child, dataLen, depth + 1);
                        r.Pos = saved;
                    }
                    cls.Classes.Add(child);
                    break;
                }
                case 1: // value: subtype + name + value
                {
                    byte sub = r.ReadByte();
                    string name = r.ReadAsciiZ();
                    cls.Properties[name] = ReadValue(r, sub);
                    break;
                }
                case 2: // array: name + array
                {
                    string name = r.ReadAsciiZ();
                    cls.Properties[name] = ReadArray(r);
                    break;
                }
                case 3: // forward class declaration
                    cls.ExternalClasses.Add(r.ReadAsciiZ());
                    break;
                case 4: // delete statement
                    _ = r.ReadAsciiZ();
                    break;
                case 5: // array += (append)
                    _ = r.ReadU32();
                    string an = r.ReadAsciiZ();
                    cls.Properties[an] = ReadArray(r);
                    break;
                default:
                    throw new InvalidDataException($"Unknown rap entry type {type} at {r.Pos}.");
            }
        }
    }

    private static RapValue ReadValue(Cursor r, byte subType) => subType switch
    {
        0 => new RapValue { Raw = r.ReadAsciiZ() },          // string
        1 => new RapValue { Raw = (double)r.ReadFloat() },   // float
        2 => new RapValue { Raw = (long)r.ReadI32() },       // int
        3 => ReadArray(r),                                   // array (rare as value subtype)
        _ => new RapValue { Raw = r.ReadAsciiZ() },          // unknown -> treat as string
    };

    private static RapValue ReadArray(Cursor r)
    {
        int n = r.ReadCompressedInt();
        if (n is < 0 or > 5_000_000) throw new InvalidDataException($"implausible rap array count {n}.");
        var list = new List<RapValue>(Math.Min(n, 1024));
        for (int i = 0; i < n; i++)
        {
            byte elem = r.ReadByte();
            list.Add(elem switch
            {
                0 => new RapValue { Raw = r.ReadAsciiZ() },
                1 => new RapValue { Raw = (double)r.ReadFloat() },
                2 => new RapValue { Raw = (long)r.ReadI32() },
                3 => ReadArray(r),
                _ => new RapValue { Raw = r.ReadAsciiZ() },
            });
        }
        return new RapValue { Raw = list };
    }

    private sealed class Cursor
    {
        private readonly byte[] _d;
        public int Pos;
        public Cursor(byte[] d) => _d = d;

        public byte ReadByte() => _d[Pos++];
        public uint ReadU32() { uint v = BitConverter.ToUInt32(_d, Pos); Pos += 4; return v; }
        public int ReadI32() { int v = BitConverter.ToInt32(_d, Pos); Pos += 4; return v; }
        public float ReadFloat() { float v = BitConverter.ToSingle(_d, Pos); Pos += 4; return v; }

        public string ReadAsciiZ()
        {
            int start = Pos;
            while (_d[Pos] != 0) Pos++;
            string s = Encoding.UTF8.GetString(_d, start, Pos - start);
            Pos++; // skip terminator
            return s;
        }

        /// <summary>7-bit LEB128-style compressed integer.</summary>
        public int ReadCompressedInt() => BinaryUtil.ReadCompressedInt(_d, ref Pos);
    }
}
