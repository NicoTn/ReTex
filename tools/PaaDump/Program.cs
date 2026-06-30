using ReTex.Core.Paa;

// Decodes a .paa and writes a 32-bit BMP, reporting dimensions and a non-zero pixel ratio.
// Usage: PaaDump <in.paa> <out.bmp>

if (args.Length < 2) { Console.Error.WriteLine("Usage: PaaDump <in.paa> <out.bmp>"); return 1; }

var img = PaaImage.LoadFile(args[0]);
Console.WriteLine($"Decoded: {img.Width}x{img.Height}, {img.Bgra.Length} bytes");

long nonBlack = 0;
for (int i = 0; i < img.Bgra.Length; i += 4)
    if (img.Bgra[i] != 0 || img.Bgra[i + 1] != 0 || img.Bgra[i + 2] != 0) nonBlack++;
Console.WriteLine($"Non-black pixels: {nonBlack}/{img.Width * img.Height} ({100.0 * nonBlack / (img.Width * img.Height):0.0}%)");

WriteBmp32(args[1], img.Width, img.Height, img.Bgra);
Console.WriteLine($"Wrote {args[1]}");
return 0;

static void WriteBmp32(string path, int w, int h, byte[] bgra)
{
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    int imgSize = w * h * 4;
    bw.Write((byte)'B'); bw.Write((byte)'M');
    bw.Write(54 + imgSize); bw.Write(0); bw.Write(54);   // file header
    bw.Write(40); bw.Write(w); bw.Write(-h);             // info header (negative h = top-down)
    bw.Write((short)1); bw.Write((short)32);
    bw.Write(0); bw.Write(imgSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
    bw.Write(bgra);
}
