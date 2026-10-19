namespace DLTools.Tests;

public sealed class TestWorkspace : IDisposable
{
    public string Root { get; }
    public TestWorkspace(string? baseDirectory = null)
    {
        Root = Path.Combine(baseDirectory ?? Path.GetTempPath(), "DLTools.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }
    public string Write(string relative, string contents)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
    public void Dispose()
    {
        // All files under this fresh GUID root are owned by this test; never follow junctions.
        DeleteOwnedDirectory(Root);
    }
    private static void DeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (item is DirectoryInfo directory && !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                DeleteOwnedDirectory(directory.FullName);
            else item.Delete();
        }
        Directory.Delete(path);
    }
}
