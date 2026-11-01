using System.Buffers;
using System.Security.Cryptography;

namespace TapeDrop.Core;

public static class Hashing
{
    /// <summary>Streams a file through SHA-256 and returns the lowercase hex digest.</summary>
    public static async Task<string> HashFileAsync(string path, Action<int>? onBytes = null, CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyEngine.BlockSize);
        try
        {
            int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(0, CopyEngine.BlockSize), ct)) > 0)
            {
                hasher.AppendData(buffer, 0, n);
                onBytes?.Invoke(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}
