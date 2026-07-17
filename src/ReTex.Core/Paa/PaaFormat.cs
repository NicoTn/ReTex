namespace ReTex.Core.Paa;

public enum PaaFormat
{
    Dxt1,
    Dxt5,
}

public sealed class PaaMetadata
{
    public IReadOnlyDictionary<string, byte[]> Tags { get; }
    public bool HasSwizzle => Tags.ContainsKey("ZIWS");
    public bool HasProcedure => Tags.ContainsKey("CORP");
    public bool IsPaintable => !HasSwizzle && !HasProcedure;

    public PaaMetadata(IReadOnlyDictionary<string, byte[]>? tags = null) =>
        Tags = tags ?? new Dictionary<string, byte[]>();
}

public sealed record PaaWriteOptions(
    PaaFormat Format,
    bool GenerateMipMaps = true,
    bool UpgradeForAlpha = true);
