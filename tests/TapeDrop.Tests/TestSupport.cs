using System.Security.Cryptography;
using TapeDrop.Core;

namespace TapeDrop.Tests;

/// <summary>A throwaway folder that is deleted when the test finishes.</summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("tapedrop-test-").FullName;

    /// <summary>Creates a file (and any folders) under the temp root and returns its full path.</summary>
    public string Make(string relativePath, byte[] data)
    {
        var full = System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        return full;
    }

    public string Sub(string name)
    {
        var full = System.IO.Path.Combine(Root, name);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}

public static class TestData
{
    public static byte[] Random(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

/// <summary>Collects progress synchronously (Progress&lt;T&gt; would post to another thread).</summary>
public sealed class SyncProgress : IProgress<CopyProgress>
{
    public List<CopyProgress> Items { get; } = new();
    public void Report(CopyProgress value) => Items.Add(value);
}
