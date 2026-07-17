using System.Security.Cryptography;
using System.Text;

namespace ReTex.Core.Paint;

public sealed record PaintRecoverySnapshot(string Path, int Width, int Height, byte[] Pixels);

public static class PaintRecoveryStore
{
    private const uint Magic = 0x54505852; // RXPT
    private const int Version = 1;

    public static string GetFilePath(string directory, string documentPath)
    {
        string identity;
        try { identity = System.IO.Path.GetFullPath(documentPath).ToUpperInvariant(); }
        catch { identity = documentPath.ToUpperInvariant(); }
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".bgra";
        return System.IO.Path.Combine(directory, name);
    }

    public static void Write(string directory, PaintDocument document)
    {
        Directory.CreateDirectory(directory);
        string destination = GetFilePath(directory, document.Path);
        string temporary = destination + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write(document.Path);
                writer.Write(document.Width); writer.Write(document.Height); writer.Write(document.Pixels.Length);
                writer.Write(document.Pixels);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool TryRead(string file, out PaintRecoverySnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            bool current = stream.Length >= 8 && reader.ReadUInt32() == Magic;
            if (current && reader.ReadInt32() != Version) return false;
            if (!current) stream.Position = 0; // Legacy v4.10 recovery record.

            string path = reader.ReadString();
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            if (width <= 0 || height <= 0) return false;
            int expected = checked(width * height * 4);
            int length = current ? reader.ReadInt32() : expected;
            if (length != expected || stream.Length - stream.Position != length) return false;
            byte[] pixels = reader.ReadBytes(length);
            if (pixels.Length != length) return false;
            snapshot = new PaintRecoverySnapshot(path, width, height, pixels);
            return true;
        }
        catch { return false; }
    }

    public static void Delete(string directory, IEnumerable<string> documentPaths)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string path in documentPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = GetFilePath(directory, path);
            try { if (File.Exists(file)) File.Delete(file); }
            catch { }
            try { if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp"); }
            catch { }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch { }
    }
}
