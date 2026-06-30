using System.Text;

namespace ReTex.Core.Pbo;

/// <summary>
/// Reads an Arma PBO: parses the header to list entries and extracts individual files
/// on demand (no full unpack). Arma 3 PBOs store files uncompressed, which is handled
/// inline; compressed (Cprs) entries are rare and delegated to the pboc CLI by callers.
/// </summary>
public sealed class PboArchive : IDisposable
{
    public const uint MimeUncompressed = 0x00000000;
    public const uint MimeVersion      = 0x56657273; // "Vers"
    public const uint MimeCompressed   = 0x43707273; // "Cprs"
    public const uint MimeEncrypted    = 0x456e6372; // "Encr"

    public string FilePath { get; }
    public IReadOnlyList<PboEntry> Entries => _entries;
    public IReadOnlyDictionary<string, string> Properties => _properties;

    /// <summary>The PBO's $PBOPREFIX$ value (the in-game virtual root), if present.</summary>
    public string? Prefix =>
        _properties.TryGetValue("prefix", out var p) ? p : null;

    private readonly List<PboEntry> _entries = new();
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileStream _stream;

    public PboArchive(string path)
    {
        FilePath = path;
        _stream = File.OpenRead(path);
        ReadHeader();
    }

    private void ReadHeader()
    {
        using var br = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);
        var raw = new List<PboEntry>();
        bool first = true;

        while (true)
        {
            string name = ReadAsciiZ(br);
            uint mime     = br.ReadUInt32();
            uint orig     = br.ReadUInt32();
            uint reserved = br.ReadUInt32();
            uint ts       = br.ReadUInt32();
            uint dataSize = br.ReadUInt32();

            // First entry may be a version/properties block: empty name + "Vers" mime,
            // followed by asciiz key/value pairs terminated by an empty key.
            if (first && name.Length == 0 && mime == MimeVersion)
            {
                first = false;
                while (true)
                {
                    string key = ReadAsciiZ(br);
                    if (key.Length == 0) break;
                    _properties[key] = ReadAsciiZ(br);
                }
                continue;
            }
            first = false;

            // Header terminator: empty name with all-zero fields.
            if (name.Length == 0 && mime == 0 && orig == 0 && reserved == 0 && ts == 0 && dataSize == 0)
                break;

            raw.Add(new PboEntry
            {
                // Normalize separators: most PBOs store entry paths with backslashes, but some
                // packers use forward slashes (e.g. "AV/config.cpp"). The rest of the codebase
                // (config lookup, virtual texture resolution) assumes backslashes, so canonicalize here.
                FileName = name.Replace('/', '\\'),
                PackingMethod = mime,
                OriginalSize = orig,
                Reserved = reserved,
                TimeStamp = ts,
                DataSize = dataSize,
            });
        }

        // Data blocks follow the header, concatenated in entry order.
        long offset = _stream.Position;
        foreach (var e in raw)
        {
            e.DataOffset = offset;
            offset += e.DataSize;
            _entries.Add(e);
        }
    }

    /// <summary>Reads and returns the (decompressed) bytes of a single entry.</summary>
    public byte[] Extract(PboEntry entry)
    {
        if (entry.PackingMethod == MimeEncrypted)
            throw new NotSupportedException($"Encrypted PBO entry: {entry.FileName}");

        _stream.Seek(entry.DataOffset, SeekOrigin.Begin);
        var packed = new byte[entry.DataSize];
        ReadExactly(_stream, packed);

        if (entry.PackingMethod == MimeCompressed)
            throw new NotSupportedException(
                $"Compressed PBO entry: {entry.FileName}. Extract via the pboc CLI instead.");

        return packed; // uncompressed
    }

    public PboEntry? Find(string fileName) =>
        _entries.FirstOrDefault(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    private static string ReadAsciiZ(BinaryReader br)
    {
        var bytes = new List<byte>(32);
        byte b;
        while ((b = br.ReadByte()) != 0) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of PBO data.");
            read += n;
        }
    }

    public void Dispose() => _stream.Dispose();
}
