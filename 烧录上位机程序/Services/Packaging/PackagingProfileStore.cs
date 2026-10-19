using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

public sealed record PackagingProfile
{
    public Guid AppId { get; init; }
    public string OutputDirectory { get; init; } = "";
    public string InstallerFileName { get; init; } = "";
    public string AppName { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string ProjectVersionAtSave { get; init; } = "";
    public string ProjectProductAtSave { get; init; } = "";
    public string IconPath { get; init; } = "";
    public InstallerMode Mode { get; init; }
    public string RuntimeIdentifier { get; init; } = "win-x64";
    public bool SelfContained { get; init; } = true;
    public bool CreateDesktopShortcut { get; init; } = true;
    public bool LaunchAfterInstall { get; init; } = true;
    public string ExistingScriptPath { get; init; } = "";
    public string ScriptInputDirectory { get; init; } = "";
    public IReadOnlyList<ExtraResourceMapping> ExtraResources { get; init; } = [];
}

/// <summary>Separate local packaging preferences; never rewrites the existing flashing preferences.</summary>
public sealed class PackagingProfileStore
{
    private readonly object _gate = new();
    public string FilePath { get; }
    private ProfileDocument _document;

    public PackagingProfileStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JLinkRTTViewer", "packaging", "profiles.json"));
        _document = Read();
    }

    public string LastSourcePath { get { lock (_gate) return _document.LastSourcePath; } }
    public BuildTools LastTools { get { lock (_gate) return _document.Tools; } }

    public PackagingProfile GetOrCreate(ProjectDescriptor project)
    {
        lock (_gate)
        {
            using var profileLock = AcquireProfileLock();
            _document = Read();
            var key = Path.GetFullPath(project.ProjectPath);
            if (!_document.Profiles.TryGetValue(key, out var profile))
            {
                profile = new PackagingProfile
                {
                    AppId = Guid.NewGuid(), AppName = project.ProductName, AppVersion = project.Version,
                    ProjectVersionAtSave = project.Version, ProjectProductAtSave = project.ProductName,
                    InstallerFileName = DefaultFileName(project.ProductName, project.Version),
                    IconPath = project.ApplicationIcon,
                    RuntimeIdentifier = DefaultRuntime(project),
                    OutputDirectory = Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, "Output")
                };
            }
            else
            {
                var name = profile.AppName == profile.ProjectProductAtSave ? project.ProductName : profile.AppName;
                var version = profile.AppVersion == profile.ProjectVersionAtSave ? project.Version : profile.AppVersion;
                var filename = profile.InstallerFileName == DefaultFileName(profile.AppName, profile.AppVersion)
                    ? DefaultFileName(name, version) : profile.InstallerFileName;
                profile = profile with { AppName = name, AppVersion = version, InstallerFileName = filename,
                    ProjectVersionAtSave = project.Version, ProjectProductAtSave = project.ProductName };
                if (profile.AppId == Guid.Empty) throw new IOException("打包配置缺少稳定 AppId，请修复配置后再打包：" + FilePath);
            }
            _document.Profiles[key] = profile;
            Persist();
            return profile;
        }
    }

    public void Save(PackagingOptions options)
    {
        if (options.AppId == Guid.Empty) throw new ArgumentException("不能保存空的 AppId。");
        lock (_gate)
        {
            using var profileLock = AcquireProfileLock();
            _document = Read();
            _document.Profiles[Path.GetFullPath(options.Project.ProjectPath)] = new PackagingProfile
            {
                AppId = options.AppId, OutputDirectory = options.OutputDirectory, InstallerFileName = options.InstallerFileName,
                AppName = options.AppName, AppVersion = options.AppVersion,
                ProjectVersionAtSave = options.Project.Version, ProjectProductAtSave = options.Project.ProductName,
                IconPath = options.IconPath, Mode = options.Mode, RuntimeIdentifier = options.RuntimeIdentifier,
                SelfContained = options.SelfContained, CreateDesktopShortcut = options.CreateDesktopShortcut,
                LaunchAfterInstall = options.LaunchAfterInstall, ExistingScriptPath = options.ExistingScriptPath,
                ScriptInputDirectory = options.ScriptInputDirectory, ExtraResources = options.ExtraResources.ToArray()
            };
            _document.Tools = options.Tools;
            Persist();
        }
    }

    public void SaveToolsAndSource(BuildTools tools, string source)
    {
        lock (_gate)
        {
            using var profileLock = AcquireProfileLock();
            _document = Read();
            _document.Tools = tools;
            _document.LastSourcePath = source;
            Persist();
        }
    }

    public static string DefaultFileName(string name, string version) =>
        PackagingFileSystem.SafeFileSegment(name) + "_v" + PackagingFileSystem.SafeFileSegment(version) + "_Setup.exe";

    private static string DefaultRuntime(ProjectDescriptor project)
    {
        if (!string.IsNullOrEmpty(project.DeclaredRuntimeIdentifier)) return project.DeclaredRuntimeIdentifier;
        var allowed = project.DeclaredRuntimeIdentifiers;
        var preferred = project.PlatformTarget.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "win-x86" : "win-x64";
        return allowed.Count == 0 || allowed.Contains(preferred, StringComparer.OrdinalIgnoreCase) ? preferred
            : allowed.FirstOrDefault(r => r is "win-x86" or "win-x64") ?? preferred;
    }

    private ProfileDocument Read()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath))
                ?? throw new JsonException("Empty document");
            if (document.SchemaVersion != 1 || document.Profiles is null || document.Tools is null)
                throw new JsonException("Unsupported profile document");
            document.Profiles = new Dictionary<string, PackagingProfile>(document.Profiles, StringComparer.OrdinalIgnoreCase);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Never silently replace a broken profile and generate a different upgrade identity.
            throw new IOException("打包配置无法读取，请保留并修复该文件（AppId 不会自动重建）：" + FilePath, ex);
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_document, new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private IDisposable AcquireProfileLock()
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())));
        var mutex = new Mutex(false, @"Local\DLToolsProfiles_" + key);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            throw new IOException("其他工具实例正在保存打包配置，请稍后重试。");
        }
        return new ProfileLock(mutex);
    }

    private sealed class ProfileLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    public sealed class ProfileDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string LastSourcePath { get; set; } = "";
        public BuildTools Tools { get; set; } = new();
        public Dictionary<string, PackagingProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
