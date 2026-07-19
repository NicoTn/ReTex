using ReTex.Core.Paint;

namespace ReTex.Core.Tests;

public sealed class PaintRecoveryStoreTests
{
    [Fact] public void RoundTripPreservesDocumentAndUsesCaseInsensitiveIdentity()
    {
        string directory = NewDirectory();
        try
        {
            var pixels = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var document = new PaintDocument("C:\\Project\\Texture.paa", 4, 4, pixels);
            PaintRecoveryStore.Write(directory, document);
            string file = PaintRecoveryStore.GetFilePath(directory, "c:\\project\\texture.paa");

            Assert.True(File.Exists(file));
            Assert.True(PaintRecoveryStore.TryRead(file, out var snapshot));
            Assert.NotNull(snapshot);
            Assert.Equal(document.Path, snapshot.Path);
            Assert.Equal(document.Width, snapshot.Width);
            Assert.Equal(document.Height, snapshot.Height);
            Assert.Equal(document.Pixels, snapshot.Pixels);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void DeleteRemovesOnlyRequestedTextureRecovery()
    {
        string directory = NewDirectory();
        try
        {
            var a = new PaintDocument("C:\\Project\\a.paa", 4, 4, new byte[64]);
            var b = new PaintDocument("C:\\Project\\b.paa", 4, 4, new byte[64]);
            PaintRecoveryStore.Write(directory, a); PaintRecoveryStore.Write(directory, b);

            PaintRecoveryStore.Delete(directory, new[] { a.Path });

            Assert.False(File.Exists(PaintRecoveryStore.GetFilePath(directory, a.Path)));
            Assert.True(File.Exists(PaintRecoveryStore.GetFilePath(directory, b.Path)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void TruncatedRecoveryIsRejected()
    {
        string directory = NewDirectory();
        try
        {
            string file = Path.Combine(directory, "broken.bgra");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            Assert.False(PaintRecoveryStore.TryRead(file, out _));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ReTex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}
