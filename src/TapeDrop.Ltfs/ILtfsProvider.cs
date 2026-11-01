namespace TapeDrop.Ltfs;

/// <summary>Details of a mounted LTFS tape.</summary>
/// <param name="Label">LTFS volume label (may be empty on an unlabelled tape).</param>
/// <param name="CapacityBytes">Total usable capacity.</param>
/// <param name="FreeBytes">Free space remaining.</param>
/// <param name="MountPath">Root path (e.g. "L:\") where the tape's files appear.</param>
public sealed record TapeInfo(string Label, long CapacityBytes, long FreeBytes, string MountPath);

/// <summary>Thrown when no tape is loaded, the drive is not ready, or a vendor command fails.</summary>
public sealed class TapeNotReadyException : Exception
{
    public TapeNotReadyException(string message) : base(message) { }
    public TapeNotReadyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The app's only view of a tape drive. Tapes are plain LTFS: once mounted, the tape is
/// just a folder tree, so everything above this interface uses ordinary file I/O.
/// </summary>
public interface ILtfsProvider
{
    /// <summary>Mounts the loaded tape and returns its details.</summary>
    Task<TapeInfo> MountAsync(CancellationToken ct = default);

    /// <summary>Cleanly unmounts, which makes LTFS write its final index to tape.</summary>
    Task UnmountAsync(CancellationToken ct = default);

    /// <summary>Ejects the tape. The tape must be unmounted first.</summary>
    Task EjectAsync(CancellationToken ct = default);
}
