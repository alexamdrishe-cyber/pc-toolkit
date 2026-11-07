using PcToolkit.Core.Storage;

namespace PcToolkit.Core.Tests;

public sealed class DiskScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PcToolkit.Tests." + Guid.NewGuid().ToString("N"));

    public DiskScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void MakeFile(string relative, int size)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    [Fact]
    public void SizesAddUpThroughTheTree()
    {
        MakeFile("a.bin", 100);
        MakeFile(@"big\b.bin", 1000);
        MakeFile(@"big\deep\c.bin", 500);
        MakeFile(@"small\d.bin", 10);

        DiskScanResult result = DiskScanner.Scan(_root);

        Assert.Equal(1610, result.Root.Size);
        Assert.Equal(4, result.Root.FileCount);
        Assert.Equal(100, result.Root.OwnFilesSize);
        Assert.Equal("big", result.Root.Children[0].Name);
        Assert.Equal(1500, result.Root.Children[0].Size);
        Assert.Equal(500, result.Root.Children[0].Children[0].Size);
        Assert.Equal("small", result.Root.Children[1].Name);
    }

    [Fact]
    public void LargestFilesAreSortedAndLimited()
    {
        for (int i = 1; i <= 10; i++)
            MakeFile($@"f{i}.bin", i * 10);

        DiskScanResult result = DiskScanner.Scan(_root, largestCount: 3);

        Assert.Equal(new long[] { 100, 90, 80 }, result.LargestFiles.Select(f => f.Size).ToArray());
        Assert.EndsWith("f10.bin", result.LargestFiles[0].Path);
    }

    [Fact]
    public void CancellationStopsTheScan()
    {
        MakeFile("a.bin", 1);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => DiskScanner.Scan(_root, cancellation: cancel.Token));
    }
}
