using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Questyle.Desktop.Services;

// State handed from the exiting process to the newly installed one so the
// new window can take over the update card at the same screen pixels.
public sealed record UpdateHandoff(int ProcessId, string ReadyEvent, int CardLeft, int CardTop,
    string Title, string Downloaded, string DownloadedUnit, string Total, string TotalUnit)
{
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));
    public static UpdateHandoff Load(string path) => JsonSerializer.Deserialize<UpdateHandoff>(File.ReadAllText(path))
        ?? throw new InvalidDataException("更新交接信息无效");
}

public sealed class UpdateInstallation(IReadOnlyList<(string Target, string Staged, string? Previous)> moves)
{
    // Moves the new entries back to staging and restores the previous ones.
    public void Rollback()
    {
        foreach (var (target, staged, previous) in moves.Reverse())
        {
            if (UpdateInstaller.Exists(target)) UpdateInstaller.Move(target, staged);
            if (previous != null) UpdateInstaller.Move(previous, target);
        }
    }
}

public static class UpdateInstaller
{
    public const string PackageFolder = "questyle-windows-x64";
    // User data beside the executable is never packaged or replaced.
    private static readonly HashSet<string> Preserved = new(["data", "eq-config", "paired-headphones.json"], StringComparer.OrdinalIgnoreCase);
    public static string Workspace => Path.Combine(AppPaths.DataDirectory, "temp", "update");
    public static string PackagePath => Path.Combine(Workspace, "package.zip");
    public static string HandoffPath => Path.Combine(Workspace, "handoff.json");
    private static string StagingDirectory => Path.Combine(Workspace, "package");
    private static string PreviousDirectory => Path.Combine(Workspace, "previous");

    public static void ResetWorkspace()
    {
        DeleteWorkspace();
        Directory.CreateDirectory(Workspace);
    }
    public static void DeleteWorkspace() { if (Directory.Exists(Workspace)) Directory.Delete(Workspace, true); }

    private static IEnumerable<string> ApplicationFiles(string applicationDirectory) =>
        Directory.EnumerateFileSystemEntries(applicationDirectory).Where(entry => !Preserved.Contains(Path.GetFileName(entry)))
            .SelectMany(entry => Directory.Exists(entry) ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories) : [entry]);

    public static long MeasureInstallation(string applicationDirectory) => ApplicationFiles(applicationDirectory).Sum(file => new FileInfo(file).Length);

    // Developer simulation: the installed application itself becomes the downloaded release ZIP.
    public static void CreatePackage(string applicationDirectory, string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in ApplicationFiles(applicationDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            archive.CreateEntryFromFile(file, PackageFolder + "/" + Path.GetRelativePath(applicationDirectory, file).Replace('\\', '/'), CompressionLevel.NoCompression);
        }
    }

    public static string Extract(string path)
    {
        ZipFile.ExtractToDirectory(path, StagingDirectory);
        var root = Path.Combine(StagingDirectory, PackageFolder);
        if (!File.Exists(Path.Combine(root, "Questyle.exe"))) throw new InvalidDataException("更新包缺少 Questyle.exe");
        return root;
    }

    // Running images can be renamed on NTFS, so each top-level entry is moved aside
    // and replaced while the old process keeps running from the mapped files.
    public static UpdateInstallation Install(string packageRoot, string applicationDirectory)
    {
        var moves = new List<(string Target, string Staged, string? Previous)>();
        var installation = new UpdateInstallation(moves);
        Directory.CreateDirectory(PreviousDirectory);
        try
        {
            foreach (var staged in Directory.EnumerateFileSystemEntries(packageRoot).ToArray())
            {
                var name = Path.GetFileName(staged);
                if (Preserved.Contains(name)) continue;
                var target = Path.Combine(applicationDirectory, name);
                string? previous = null;
                if (Exists(target)) { previous = Path.Combine(PreviousDirectory, name); Move(target, previous); }
                moves.Add((target, staged, previous));
                Move(staged, target);
            }
        }
        catch { installation.Rollback(); throw; }
        return installation;
    }

    internal static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    internal static void Move(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
    }
}
