using System.IO;
using System.Text.RegularExpressions;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

public sealed class BuildToolLocator(IExternalProcessRunner? runner = null)
{
    private readonly IExternalProcessRunner _runner = runner ?? new ExternalProcessRunner();

    public async Task<BuildTools> LocateAsync(BuildTools? overrides = null, CancellationToken cancellationToken = default)
    {
        var dotnet = Choose(overrides?.DotNetPath, FindOnPath("dotnet.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
        var inno = Choose(overrides?.InnoCompilerPath, FindOnPath("ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"));
        var msbuild = Choose(overrides?.MsBuildPath, FindOnPath("MSBuild.exe"));
        if (string.IsNullOrEmpty(msbuild))
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
            {
                try
                {
                    var result = await _runner.RunAsync(vswhere,
                        ["-utf8", "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"],
                        Path.GetDirectoryName(vswhere)!, cancellationToken: cancellationToken);
                    if (result.ExitCode == 0)
                        msbuild = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim()).FirstOrDefault(File.Exists) ?? "";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                {
                    // A broken optional VS discovery tool must not hide dotnet/Inno or disable modern projects.
                    msbuild = "";
                }
            }
        }
        return new BuildTools { DotNetPath = dotnet, MsBuildPath = msbuild, InnoCompilerPath = inno };
    }

    public async Task<IReadOnlyList<ToolDiagnostic>> DiagnoseAsync(BuildTools tools, CancellationToken cancellationToken = default)
    {
        var diagnostics = await Task.WhenAll(
            ProbeAsync(".NET SDK", tools.DotNetPath, ["--list-sdks"], s =>
                Regex.IsMatch(s, @"(?m)^\d+\.\d+\.\d+.*\["), "未找到已安装的 .NET SDK。", cancellationToken),
            ProbeAsync("Visual Studio MSBuild", tools.MsBuildPath, ["-nologo", "-version"], s =>
                Regex.Matches(s, @"\b\d+\.\d+(?:\.\d+){0,2}\b").Select(m => m.Value)
                    .Any(v => Version.TryParse(v, out var version) && version >= new Version(17, 8)),
                "需要 Visual Studio MSBuild 17.8 或更新版本。", cancellationToken),
            ProbeAsync("Inno Setup", tools.InnoCompilerPath, ["/?"], s =>
                Regex.IsMatch(s, @"Inno Setup\s+(?:[6-9]|[1-9]\d+)\b", RegexOptions.IgnoreCase),
                "需要 Inno Setup 6 或更新版本。", cancellationToken, helpExitCodeOne: true));
        return [.. diagnostics, new ToolDiagnostic("Inno 简体中文语言文件", tools.ChineseLanguagePath,
            File.Exists(tools.ChineseLanguagePath), File.Exists(tools.ChineseLanguagePath)
                ? "已找到 ChineseSimplified.isl" : "请将 ChineseSimplified.isl 放到 Inno Setup 的 Languages 目录。")];
    }

    private async Task<ToolDiagnostic> ProbeAsync(string name, string path, IReadOnlyList<string> args,
        Func<string, bool> accepts, string requirement, CancellationToken cancellationToken, bool helpExitCodeOne = false)
    {
        if (!File.Exists(path)) return new(name, path, false, $"未找到工具。{requirement}");
        try
        {
            var result = await _runner.RunAsync(path, args, Path.GetDirectoryName(path)!, cancellationToken: cancellationToken);
            var output = result.StandardOutput + "\n" + result.StandardError;
            // ISCC /? uses exit code 1 for a successful help request (there is no script to compile).
            bool available = (result.ExitCode == 0 || (helpExitCodeOne && result.ExitCode == 1)) && accepts(output);
            return new(name, path, available, available ? output.Trim() : requirement + "\n" + output.Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(name, path, false, ex.Message); }
    }

    public static string FindOnPath(string filename)
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = Path.Combine(entry.Trim().Trim('"'), filename);
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) return Path.GetFullPath(path);
            }
            catch (ArgumentException) { }
        }
        return "";
    }

    private static string Choose(string? specified, params string[] candidates) =>
        !string.IsNullOrWhiteSpace(specified) ? specified.Trim() : candidates.FirstOrDefault(File.Exists) ?? "";
}
