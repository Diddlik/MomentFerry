namespace MomentFerry.Core.Domain;

public sealed record SharePreset(
    string Id,
    string DisplayName,
    IReadOnlyList<string> IgnorePatterns,
    int StabilitySeconds = 30);

public static class SharePresets
{
    public static readonly SharePreset Generic = new(
        "generic",
        "Generic",
        Array.Empty<string>());

    // "_.pending-<id>-<name>" is what a Resilio transfer in progress is called, and "*.!sync" does not
    // cover it. Indexing one is harmless while it grows, but a stalled download looks stable after the
    // share's window and would be routed as a real file: the copy would verify against the truncated
    // content's own checksum and Safe Move would then remove the partial.
    public static readonly SharePreset Resilio = new(
        "resilio",
        "Resilio Sync",
        new[] { ".sync/**", "*.!sync", "_.pending-*" });

    public static readonly SharePreset Syncthing = new(
        "syncthing",
        "Syncthing",
        new[] { ".stfolder/**", ".stversions/**", "~syncthing~*" });

    public static readonly SharePreset Synology = new(
        "synology",
        "Synology NAS",
        new[] { "@eaDir/**", "#recycle/**" });

    public static IReadOnlyList<SharePreset> All { get; } =
        new[] { Generic, Resilio, Syncthing, Synology };
}
