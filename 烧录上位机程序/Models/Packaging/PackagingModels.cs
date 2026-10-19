using System.IO;

namespace 烧录上位机程序.Models.Packaging;

public enum InstallerMode { Generated, ExistingScript }
public enum PackagingStage { Preparing, Restoring, Building, Collecting, Compiling, Committing, Succeeded, Failed, Cancelled }

public sealed record BuildTools
{
    public string DotNetPath { get; init; } = "";
    public string MsBuildPath { get; init; } = "";
    public string InnoCompilerPath { get; init; } = "";
    public string ChineseLanguagePath => string.IsNullOrEmpty(InnoCompilerPath) ? "" :
        Path.Combine(Path.GetDirectoryName(InnoCompilerPath)!, "Languages", "ChineseSimplified.isl");
}

public sealed record ProjectDescriptor
{
    public string ProjectPath { get; init; } = "";
    public string SourceRoot { get; init; } = "";
    public string? SolutionPath { get; init; }
    public string SolutionConfiguration { get; init; } = "";
    public IReadOnlyList<string> AvailableConfigurations { get; init; } = [];
    public IReadOnlyList<string> AvailablePlatforms { get; init; } = [];
    public string TargetingPackDirectory { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Configuration { get; init; } = "Release";
    public string Platform { get; init; } = "AnyCPU";
    public string TargetFramework { get; init; } = "";
    public string FrameworkVersion { get; init; } = "";
    public bool IsNetFramework { get; init; }
    public bool IsSdkStyle { get; init; }
    public string UiFramework { get; init; } = "";
    public string PlatformTarget { get; init; } = "AnyCPU";
    public string DeclaredRuntimeIdentifier { get; init; } = "";
    public IReadOnlyList<string> DeclaredRuntimeIdentifiers { get; init; } = [];
    public bool Prefer32Bit { get; init; }
    public string AssemblyName { get; init; } = "";
    public string ResolvedExecutableName { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string ApplicationIcon { get; init; } = "";
    public string TargetDirectory { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string UnsupportedReason { get; init; } = "";
    public bool IsSupported => string.IsNullOrEmpty(UnsupportedReason);
    public string MainExecutable => string.IsNullOrWhiteSpace(ResolvedExecutableName) ? AssemblyName + ".exe" : ResolvedExecutableName;
    public string DisplayName => $"{ProjectName} — {UiFramework} / {TargetFramework} / {Configuration}|{Platform}"
        + (string.IsNullOrEmpty(SolutionConfiguration) ? "" : $" [解决方案 {SolutionConfiguration}]");
    public override string ToString() => DisplayName;
}

/// <param name="SourcePath">Absolute file or directory on the packaging machine.</param>
/// <param name="Destination">Path relative to the installation root; files include the target filename.</param>
public sealed record ExtraResourceMapping(string SourcePath, string Destination);

public sealed record PackagingOptions
{
    public ProjectDescriptor Project { get; init; } = new();
    public BuildTools Tools { get; init; } = new();
    public InstallerMode Mode { get; init; }
    public string OutputDirectory { get; init; } = "";
    public string InstallerFileName { get; init; } = "";
    public string AppName { get; init; } = "";
    public string AppVersion { get; init; } = "1.0.0";
    public Guid AppId { get; init; }
    public string IconPath { get; init; } = "";
    public string RuntimeIdentifier { get; init; } = "win-x64";
    public bool SelfContained { get; init; } = true;
    public bool CreateDesktopShortcut { get; init; } = true;
    public bool LaunchAfterInstall { get; init; } = true;
    public string ExistingScriptPath { get; init; } = "";
    public string ScriptInputDirectory { get; init; } = "";
    public IReadOnlyList<ExtraResourceMapping> ExtraResources { get; init; } = [];
}

public sealed record PackagingProgress(PackagingStage Stage, string Message);
public sealed record PackagingConfirmation(string Title, string Message, string Path);
public sealed record PackagingResult(bool Succeeded, bool Cancelled, string? InstallerPath,
    string? Error, string LogPath, IReadOnlyList<string> BackupPaths);

public sealed record ToolDiagnostic(string Name, string Path, bool Available, string Message);
