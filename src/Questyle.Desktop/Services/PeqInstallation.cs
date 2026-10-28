using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Questyle.Desktop.Services;

public static class PeqInstallation
{
    private const string MachinePath = @"SOFTWARE\Questyle\Peq";
    private static readonly string[] ChangedNames = [.. new[] { 1, 2, 5, 6, 7, 13, 14, 15 }.Select(n => PeqEndpoint.FxFormat + n), "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5"];
    private static string ResultPath => Path.Combine(AppPaths.DataDirectory, "peq-install-result.json");
    private sealed record OperationResult(bool Success, string? Error);

    public static async Task ChangeAsync(PeqEndpoint endpoint, bool install)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(install ? "--peq-install" : "--peq-restore"); start.ArgumentList.Add(endpoint.Id);
        start.ArgumentList.Add(AppPaths.DataDirectory);
        File.Delete(ResultPath);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        if (!File.Exists(ResultPath)) throw new IOException("PEQ 安装进程未返回结果。");
        var result = JsonSerializer.Deserialize<OperationResult>(File.ReadAllText(ResultPath))!;
        if (!result.Success) throw new IOException(result.Error);
        if (process.ExitCode != 0) throw new IOException("PEQ 安装进程失败。");
    }

    // Called only by the explicit elevated command. Application startup never changes audio bindings.
    public static int RunElevated(string id, bool install)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("安装 PEQ 需要管理员权限。");
            var endpoint = PeqEndpoint.Read(id) ?? throw new InvalidOperationException("未找到对应的 Dongle 播放端点。");
            if (install) Install(endpoint); else Restore(endpoint);
            File.WriteAllText(ResultPath, JsonSerializer.Serialize(new OperationResult(true, null)));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(ResultPath, JsonSerializer.Serialize(new OperationResult(false, ex.ToString())));
            return 1;
        }
    }

    private static RegistryKey OpenEffects(PeqEndpoint endpoint) => Registry.LocalMachine.OpenSubKey(
        PeqEndpoint.RenderPath + "\\" + endpoint.Id + "\\FxProperties", RegistryKeyPermissionCheck.ReadWriteSubTree,
        RegistryRights.ReadKey | RegistryRights.SetValue) ?? throw new IOException("Dongle 音效属性不存在。");

    private static void Install(PeqEndpoint endpoint)
    {
        if (endpoint.Installed) throw new InvalidOperationException("此播放端点已安装 PEQ。");
        using var audio = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio");
        if (audio?.GetValue("DisableProtectedAudioDG") is not int protection || protection != 1)
            throw new InvalidOperationException("当前系统不允许加载未签名的 PEQ 音效组件。");
        using var machine = Registry.LocalMachine.CreateSubKey(MachinePath);
        using var existingBackup = machine.OpenSubKey("Backups\\" + endpoint.Id);
        if (existingBackup != null) throw new InvalidOperationException("此端点存在待恢复的音效备份，请先恢复。");
        using var effects = OpenEffects(endpoint);
        var targetDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Questyle", "peq");
        Directory.CreateDirectory(targetDirectory);
        var source = Path.Combine(AppContext.BaseDirectory, "Questyle.Peq.Native.dll");
        var target = Path.Combine(targetDirectory, "Questyle.Peq.Native.dll");
        if (!File.Exists(target) || !SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target)))) File.Copy(source, target, true);
        Directory.CreateDirectory(PeqEndpoint.SharedDirectory);
        var directory = new DirectoryInfo(PeqEndpoint.SharedDirectory);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(identity: WindowsIdentity.GetCurrent().User!, fileSystemRights: FileSystemRights.Modify,
            inheritanceFlags: InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        machine.SetValue("DataPath", PeqEndpoint.SharedDirectory);
        var configPath = Path.Combine(PeqEndpoint.SharedDirectory, "peq.bin");
        // Initial installation starts bypassed; an explicit Apply action changes the sound.
        if (!File.Exists(configPath)) File.WriteAllBytes(configPath, new PeqSettings(false, 0, []).ToBytes(DateTime.UtcNow.Ticks));
        Register(target);
        using var backup = machine.CreateSubKey("Backups\\" + endpoint.Id);
        var present = ChangedNames.Where(n => effects.GetValue(n) != null).ToArray();
        foreach (var name in present) backup.SetValue(name, effects.GetValue(name)!, effects.GetValueKind(name));
        backup.SetValue("Present", present, RegistryValueKind.MultiString);
        try
        {
            foreach (var name in ChangedNames) effects.DeleteValue(name, false);
            effects.SetValue(PeqEndpoint.FxFormat + "2", PeqEndpoint.Clsid);
            effects.SetValue(ChangedNames[^1], 0, RegistryValueKind.DWord);
        }
        catch
        {
            RestoreValues(effects, backup);
            machine.DeleteSubKeyTree("Backups\\" + endpoint.Id);
            throw;
        }
    }

    private static void Register(string dll)
    {
        using var com = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\CLSID\" + PeqEndpoint.Clsid);
        com.SetValue("", "Questyle Dongle PEQ");
        using var server = com.CreateSubKey("InprocServer32"); server.SetValue("", dll); server.SetValue("ThreadingModel", "Both");
        using var apo = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + PeqEndpoint.Clsid);
        apo.SetValue("FriendlyName", "Questyle Dongle PEQ"); apo.SetValue("Copyright", "Questyle Windows contributors");
        foreach (var (name, value) in new (string, int)[] { ("MajorVersion",1), ("MinorVersion",0), ("Flags",15), ("MinInputConnections",1), ("MaxInputConnections",1), ("MinOutputConnections",1), ("MaxOutputConnections",1), ("MaxInstances",-1), ("NumAPOInterfaces",1) }) apo.SetValue(name,value,RegistryValueKind.DWord);
        apo.SetValue("APOInterface0", "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}");
    }
    private static void Restore(PeqEndpoint endpoint)
    {
        using var machine = Registry.LocalMachine.OpenSubKey(MachinePath, true) ?? throw new IOException("PEQ 安装备份不存在。");
        using var backup = machine.OpenSubKey("Backups\\" + endpoint.Id) ?? throw new IOException("此端点没有音效备份。");
        using var effects = OpenEffects(endpoint);
        if (!endpoint.Installed || ChangedNames.Take(8).Where(n => n != PeqEndpoint.FxFormat + "2").Any(n => effects.GetValue(n) != null))
            throw new IOException("音效绑定已被其他程序修改，无法直接恢复。");
        RestoreValues(effects, backup);
        machine.DeleteSubKeyTree("Backups\\" + endpoint.Id);
        // Keep shared DLL and settings: other explicitly installed dongle endpoints can still use them.
    }
    private static void RestoreValues(RegistryKey effects, RegistryKey backup)
    {
        var present = (string[])backup.GetValue("Present")!;
        foreach (var name in ChangedNames)
        {
            if (present.Contains(name)) effects.SetValue(name, backup.GetValue(name)!, backup.GetValueKind(name));
            else effects.DeleteValue(name, false);
        }
    }

    public static long WriteConfiguration(PeqSettings settings)
    {
        long revision = DateTime.UtcNow.Ticks;
        WriteAtomically(Path.Combine(PeqEndpoint.SharedDirectory, "peq.bin"), settings.ToBytes(revision));
        return revision;
    }

    // The audio engine may be reading the file at any moment. A classic replace fails with access
    // denied while any handle is open; a POSIX-semantics rename replaces the name atomically and
    // leaves open readers on the previous contents.
    public static void WriteAtomically(string path, byte[] contents)
    {
        using var file = CreateFileW(path + ".tmp", 0x40000000 | 0x00010000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero); // GENERIC_WRITE | DELETE, CREATE_ALWAYS
        if (file.IsInvalid) throw new Win32Exception();
        RandomAccess.Write(file, contents, 0);
        // FILE_RENAME_INFO on x64: Flags at 0, RootDirectory at 8, FileNameLength at 16, FileName at 20.
        var name = Encoding.Unicode.GetBytes(Path.GetFullPath(path));
        var information = new byte[20 + name.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(information, 0x1 | 0x2); // REPLACE_IF_EXISTS | POSIX_SEMANTICS
        BinaryPrimitives.WriteInt32LittleEndian(information.AsSpan(16), name.Length);
        name.CopyTo(information, 20);
        if (!SetFileInformationByHandle(file, 22, information, information.Length)) throw new Win32Exception(); // FileRenameInfoEx
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, int size);
}
