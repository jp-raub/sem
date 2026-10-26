namespace Sem.Hosting;

/// <summary>Cooperative process-wide exclusion between the web host and offline maintenance.
/// The inode is deliberately retained after close: unlinking a lock permits split ownership.</summary>
public sealed class StorageLease : IDisposable
{
    private readonly FileStream stream;
    private StorageLease(FileStream stream) => this.stream = stream;
    public static StorageLease Acquire(string directory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Storage lease requires Linux.");
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Storage directory must exist.");
        RejectLinks(directory);
        var path = Path.Combine(directory, "sem.runtime.lock");
        RejectLinks(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        try { return new(new FileStream(path, options)); }
        catch (IOException) { throw new InvalidOperationException("Storage is busy; stop the application and workers before maintenance."); }
    }
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); ; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Symbolic links are not allowed in maintenance storage paths.");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
        }
    }
    public void Dispose() => stream.Dispose();
}
