namespace TapeDrop.Ltfs;

/// <summary>
/// A fake drive that treats a normal folder as a tape. Used by the tests and by the app's
/// demo mode so everything can be tried without hardware.
/// </summary>
public sealed class FolderLtfsProvider : ILtfsProvider
{
    private string? _folder;
    private string _label = "";
    private long _capacity;

    public bool IsMounted { get; private set; }
    public int MountCount { get; private set; }
    public int UnmountCount { get; private set; }

    public FolderLtfsProvider() { }

    public FolderLtfsProvider(string folder, long capacityBytes, string label)
    {
        InsertTape(folder, capacityBytes, label);
    }

    /// <summary>"Inserts" a tape backed by the given folder.</summary>
    public void InsertTape(string folder, long capacityBytes, string label)
    {
        Directory.CreateDirectory(folder);
        _folder = folder;
        _capacity = capacityBytes;
        _label = label;
        IsMounted = false;
    }

    public Task<TapeInfo> MountAsync(CancellationToken ct = default)
    {
        if (_folder is null)
            throw new TapeNotReadyException("No tape is loaded.");

        IsMounted = true;
        MountCount++;
        long used = DirectorySize(_folder);
        return Task.FromResult(new TapeInfo(_label, _capacity, Math.Max(0, _capacity - used), _folder));
    }

    public Task UnmountAsync(CancellationToken ct = default)
    {
        IsMounted = false;
        UnmountCount++;
        return Task.CompletedTask;
    }

    public Task EjectAsync(CancellationToken ct = default)
    {
        if (IsMounted)
            throw new InvalidOperationException("Unmount the tape before ejecting it.");
        _folder = null;
        return Task.CompletedTask;
    }

    private static long DirectorySize(string folder)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            total += new FileInfo(file).Length;
        return total;
    }
}
