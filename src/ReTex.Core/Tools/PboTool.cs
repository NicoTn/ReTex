using System.Diagnostics;

namespace ReTex.Core.Tools;

/// <summary>
/// Wrapper around the pboman3 CLI (pboc.exe) for packing/unpacking PBOs.
///
/// IMPORTANT: pboc does NOT overwrite an existing output PBO - it exits 0 but silently
/// skips. PackAsync deletes the target first so a repack always takes effect.
/// </summary>
public sealed class PboTool
{
    public string PbocPath { get; }

    public PboTool(string pbocPath)
    {
        if (!File.Exists(pbocPath))
            throw new FileNotFoundException("pboc.exe not found", pbocPath);
        PbocPath = pbocPath;
    }

    /// <summary>Locates pboc.exe at its default install location (%LOCALAPPDATA%\PBO Manager).</summary>
    public static string? FindDefault()
    {
        var p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PBO Manager", "pboc.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Packs <paramref name="folder"/> into "&lt;folderName&gt;.pbo" under <paramref name="outputDir"/>.</summary>
    public async Task<PboResult> PackAsync(string folder, string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        // pboc won't overwrite - remove the stale target first.
        var target = Path.Combine(outputDir, Path.GetFileName(folder.TrimEnd('\\', '/')) + ".pbo");
        if (File.Exists(target)) File.Delete(target);

        var result = await RunAsync(new[] { "pack", folder, "-o", outputDir }, ct);
        return result with { OutputPath = File.Exists(target) ? target : null };
    }

    /// <summary>Unpacks a PBO into <paramref name="outputDir"/>. With <paramref name="usePrefix"/> it extracts into the $prefix$ tree.</summary>
    public async Task<PboResult> UnpackAsync(string pboFile, string outputDir, bool usePrefix = false, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var args = new List<string> { "unpack", pboFile, "-o", outputDir };
        if (usePrefix) args.Add("-x");
        return await RunAsync(args.ToArray(), ct);
    }

    private async Task<PboResult> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PbocPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        return new PboResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}

public sealed record PboResult(int ExitCode, string StdOut, string StdErr)
{
    public string? OutputPath { get; init; }
    public bool Success => ExitCode == 0;
}
