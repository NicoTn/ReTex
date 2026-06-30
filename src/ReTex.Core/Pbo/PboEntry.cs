namespace ReTex.Core.Pbo;

/// <summary>One file record inside a PBO header.</summary>
public sealed class PboEntry
{
    /// <summary>Virtual path within the PBO (backslash-separated, e.g. "data\skin_co.paa").</summary>
    public required string FileName { get; init; }

    public uint PackingMethod { get; init; }
    public uint OriginalSize { get; init; }
    public uint Reserved { get; init; }
    public uint TimeStamp { get; init; }

    /// <summary>Size of the stored (possibly compressed) data block.</summary>
    public uint DataSize { get; init; }

    /// <summary>Absolute offset of this entry's data block in the PBO file. Filled in by the reader.</summary>
    public long DataOffset { get; set; }

    public bool IsCompressed => PackingMethod == PboArchive.MimeCompressed;

    /// <summary>Decompressed size (OriginalSize for compressed entries, else DataSize).</summary>
    public long UnpackedSize => OriginalSize != 0 ? OriginalSize : DataSize;

    /// <summary>Lower-case extension including the dot, e.g. ".paa".</summary>
    public string Extension => System.IO.Path.GetExtension(FileName).ToLowerInvariant();

    public override string ToString() => FileName;
}
