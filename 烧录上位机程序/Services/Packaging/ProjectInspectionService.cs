using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

/// <summary>Discovers candidates and evaluates their effective MSBuild properties without executing targets.</summary>
public sealed class ProjectInspectionService(IExternalProcessRunner? runner = null)
{
    private readonly IExternalProcessRunner _runner = runner ?? new ExternalProcessRunner();
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", ".mimosa", ".dltools-backups" };
    private const string Properties = "MSBuildVersion,TargetFramework,TargetFrameworks,TargetFrameworkIdentifier,TargetFrameworkVersion,UseWPF,UseWindowsForms,OutputType,AssemblyName,TargetName,TargetFileName,Product,Version,PlatformTarget,RuntimeIdentifier,RuntimeIdentifiers,Prefer32Bit,ApplicationIcon,TargetDir,OutDir,OutputPath,Configurations,Platforms,ProjectTypeGuids,IsTestProject,FrameworkPathOverride,TargetFrameworkRootPath,TargetFrameworkDirectory";

    public async Task<IReadOnlyList<ProjectDescriptor>> InspectAsync(string source, BuildTools tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = Path.GetFullPath(source);
        if (File.Exists(source) || Directory.Exists(source)) RejectLink(source);
        var root = Directory.Exists(source) ? source : Path.GetDirectoryName(source)!;
        var candidates = new List<ProjectDescriptor>();
        if (Directory.Exists(source))
        {
            var files = Scan(source, cancellationToken).ToArray();
            foreach (var solution in files.Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))
                candidates.AddRange(SolutionProjectReader.Read(solution, root));
            var referenced = candidates.Select(p => p.ProjectPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            candidates.AddRange(files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !referenced.Contains(f)).Select(p => Create(p, root)));
        }
        else if (!File.Exists(source)) throw new FileNotFoundException("未找到所选项目或解决方案。", source);
        else if (Path.GetExtension(source).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            candidates.AddRange(SolutionProjectReader.Read(source, root));
        else if (Path.GetExtension(source).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Create(source, root));
        else throw new NotSupportedException("请选择项目文件夹、.sln 或 C# .csproj 文件。");
        if (candidates.Count == 0) throw new InvalidOperationException("所选路径中未发现 C# 项目。已跳过构建目录和链接目录。");

        var results = new List<ProjectDescriptor>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidate.IsSupported) { results.Add(candidate); continue; }
            var failedCandidate = candidate;
            try
            {
                var xml = ReadProject(candidate.ProjectPath);
                var initial = candidate with { IsSdkStyle = IsSdk(xml) };
                if (candidate.SolutionPath is null) initial = PickDirectConfiguration(initial, xml);
                failedCandidate = WithUnverifiedFrameworkHint(initial, xml);
                var evaluated = await QueryAsync(initial, tools, xml, cancellationToken).ConfigureAwait(false);
                // Platforms may be imported from Directory.Build.props rather than written in the project.
                // A direct import has no solution mapping to choose that platform on the user's behalf.
                var declaredPlatforms = Split(evaluated.Get("Platforms"));
                if (candidate.SolutionPath is null && initial.Platform == "AnyCPU" && declaredPlatforms.Count != 0
                    && !declaredPlatforms.Any(p => SolutionProjectReader.NormalizePlatform(p).Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)))
                {
                    initial = initial with { Platform = SolutionProjectReader.NormalizePlatform(declaredPlatforms[0]) };
                    evaluated = await QueryAsync(initial, tools, xml, cancellationToken).ConfigureAwait(false);
                }
                var frameworks = Split(evaluated.Get("TargetFrameworks"));
                if (frameworks.Count == 0) frameworks = [evaluated.Get("TargetFramework")];
                foreach (var framework in frameworks)
                {
                    var inner = initial with { TargetFramework = framework };
                    try
                    {
                        var values = frameworks.Count == 1 && string.IsNullOrEmpty(evaluated.Get("TargetFrameworks"))
                            ? evaluated : await QueryAsync(inner, tools, xml, cancellationToken).ConfigureAwait(false);
                        results.Add(Describe(inner, values, xml));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        results.Add(inner with { UnsupportedReason = ex.Message,
                            Evidence = $"目标框架 {framework} 的 MSBuild 评估未完成；其余目标框架单独评估。" });
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add(failedCandidate with { UnsupportedReason = ex.Message }); }
        }
        return results;
    }

    public async Task<ProjectDescriptor> EvaluateAsync(ProjectDescriptor project, BuildTools tools,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var xml = ReadProject(project.ProjectPath);
            var basis = project with { IsSdkStyle = IsSdk(xml), UnsupportedReason = "" };
            return Describe(basis, await QueryAsync(basis, tools, xml, cancellationToken).ConfigureAwait(false), xml);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return project with { UnsupportedReason = ex.Message }; }
    }

    private async Task<Evaluation> QueryAsync(ProjectDescriptor project, BuildTools tools, XDocument xml,
        CancellationToken cancellationToken, bool forceFrameworkTool = false)
    {
        bool framework = forceFrameworkTool || !project.IsSdkStyle || LooksLikeFramework(project.TargetFramework)
            || (string.IsNullOrEmpty(project.TargetFramework) && xml.Descendants().Any(e =>
                e.Name.LocalName == "TargetFramework" && LooksLikeFramework(e.Value)));
        var tool = framework ? tools.MsBuildPath : tools.DotNetPath;
        if (string.IsNullOrWhiteSpace(tool) || !File.Exists(tool))
            throw new InvalidOperationException(framework ? "需要 Visual Studio MSBuild 17.8+，请设置 MSBuild 工具路径。"
                : "需要 .NET SDK（MSBuild 17.8+），请设置 dotnet 工具路径。");
        var args = new List<string>();
        if (!framework) args.Add("msbuild");
        args.AddRange([project.ProjectPath, "-nologo", "-nr:false", "-p:UseSharedCompilation=false",
            "-getProperty:" + Properties, "-getItem:Reference"]);
        args.AddRange(SolutionBuildContext.Arguments(project));
        if (!string.IsNullOrEmpty(project.TargetFramework)) args.Add("-p:TargetFramework=" + project.TargetFramework);
        var result = await _runner.RunAsync(tool, args, Path.GetDirectoryName(project.ProjectPath)!,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"MSBuild 项目评估失败（退出码 {result.ExitCode}）：\n{result.StandardError}\n{result.StandardOutput}".Trim());
        var text = result.StandardOutput;
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("MSBuild 未返回属性 JSON；需要 MSBuild 17.8+。\n" + text);
        try
        {
            using var json = JsonDocument.Parse(text[start..(end + 1)]);
            if (!json.RootElement.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("MSBuild 未返回有效的 Properties 属性节点。\n" + text);
            var values = properties.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            if (!System.Version.TryParse(values.GetValueOrDefault("MSBuildVersion"), out var version)
                || version < new System.Version(17, 8))
                throw new InvalidOperationException("需要 MSBuild 17.8 或更高版本进行属性评估。");
            var references = new List<string>();
            if (json.RootElement.TryGetProperty("Items", out var items) && items.TryGetProperty("Reference", out var refs))
                references.AddRange(refs.EnumerateArray().Select(r => r.TryGetProperty("Identity", out var identity) ? identity.ToString() : ""));
            // An imported props file can supply net48. Once evaluated, switch to full MSBuild too.
            if (!framework && (LooksLikeFramework(values.GetValueOrDefault("TargetFramework", ""))
                || values.GetValueOrDefault("TargetFrameworkIdentifier", "").Equals(".NETFramework", StringComparison.OrdinalIgnoreCase)))
                return await QueryAsync(project, tools, xml, cancellationToken, forceFrameworkTool: true).ConfigureAwait(false);
            return new Evaluation(values, references);
        }
        catch (JsonException ex) { throw new InvalidOperationException("MSBuild 属性 JSON 无法解析：" + ex.Message + "\n" + text, ex); }
    }

    private static ProjectDescriptor Describe(ProjectDescriptor project, Evaluation data, XDocument xml)
    {
        var p = data.Get;
        bool framework = p("TargetFrameworkIdentifier").Equals(".NETFramework", StringComparison.OrdinalIgnoreCase)
            || LooksLikeFramework(p("TargetFramework")) || (!project.IsSdkStyle && p("TargetFrameworkVersion").StartsWith('v'));
        var tfm = p("TargetFramework");
        var version = p("TargetFrameworkVersion");
        if (framework && string.IsNullOrEmpty(tfm)) tfm = "net" + version.TrimStart('v', 'V').Replace(".", "");
        bool wpf = IsTrue(p("UseWPF")) || (framework && (data.References.Any(r => ReferenceIs(r, "PresentationFramework"))
            || p("ProjectTypeGuids").Contains("60DC8134-EBA5-43B8-BCC9-BB4BC16C2548", StringComparison.OrdinalIgnoreCase)));
        bool forms = IsTrue(p("UseWindowsForms")) || (framework && data.References.Any(r => ReferenceIs(r, "System.Windows.Forms")));
        var cpu = SolutionProjectReader.NormalizePlatform(p("PlatformTarget"));
        if (string.IsNullOrEmpty(cpu)) cpu = "AnyCPU";
        var rid = p("RuntimeIdentifier").Trim();
        var rids = Split(p("RuntimeIdentifiers"));
        var configs = Split(p("Configurations"));
        var platforms = Split(p("Platforms")).Select(SolutionProjectReader.NormalizePlatform).ToList();
        var declared = DeclaredConfigurations(xml);
        configs = configs.Concat(declared.Select(d => d.Configuration)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        platforms = platforms.Concat(declared.Select(d => d.Platform)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string reason = "";
        if (IsTrue(p("IsTestProject"))) reason = "测试项目不能作为桌面安装包入口。";
        else if (!p("OutputType").Equals("WinExe", StringComparison.OrdinalIgnoreCase)
            && !p("OutputType").Equals("Exe", StringComparison.OrdinalIgnoreCase)) reason = "此项目是库或没有可执行入口（OutputType 非 WinExe/Exe）。";
        else if (!wpf && !forms) reason = "此项目不是受支持的 C# WPF 或 WinForms 桌面应用。";
        else if (!new[] { "AnyCPU", "x86", "x64" }.Contains(cpu, StringComparer.OrdinalIgnoreCase)) reason = $"已识别 CPU 架构 {cpu}，首版只支持 AnyCPU、x86 和 x64。";
        else if (!string.IsNullOrEmpty(rid) && !IsSupportedRid(rid)) reason = $"项目固定 RuntimeIdentifier={rid}，首版仅支持 win-x86、win-x64。";
        else if (!string.IsNullOrEmpty(rid) && !IsCompatibleRid(cpu, rid)) reason = $"项目 PlatformTarget={cpu} 与固定 RuntimeIdentifier={rid} 的 CPU 架构冲突。";
        else if (string.IsNullOrEmpty(rid) && rids.Count != 0 && !rids.Any(r => IsSupportedRid(r) && IsCompatibleRid(cpu, r)))
            reason = $"项目声明的 RuntimeIdentifiers={string.Join(';', rids)} 中没有与 CPU {cpu} 兼容的 win-x86/win-x64。";
        else if (string.IsNullOrEmpty(tfm)) reason = "无法确定目标框架。";
        else if (configs.Count != 0 && !configs.Contains(project.Configuration, StringComparer.OrdinalIgnoreCase)) reason = $"项目未声明所选配置 {project.Configuration}。";
        else if (platforms.Count != 0 && !platforms.Contains(project.Platform, StringComparer.OrdinalIgnoreCase)) reason = $"项目未声明所选平台 {project.Platform}。";

        var projectDir = Path.GetDirectoryName(project.ProjectPath)!;
        var targetDir = p("TargetDir");
        if (string.IsNullOrWhiteSpace(targetDir)) targetDir = !string.IsNullOrWhiteSpace(p("OutDir")) ? p("OutDir") : p("OutputPath");
        if (!string.IsNullOrWhiteSpace(targetDir)) targetDir = Path.GetFullPath(targetDir, projectDir);
        else if (string.IsNullOrEmpty(reason)) reason = "所选配置未提供 TargetDir、OutDir 或 OutputPath，无法安全定位构建输出。";
        var targetingPack = framework ? FindTargetingPack(p, version, projectDir) : "";
        if (framework && !File.Exists(Path.Combine(targetingPack, "mscorlib.dll")) && string.IsNullOrEmpty(reason))
            reason = $"缺少 .NET Framework {version} 开发包（Reference Assemblies），请安装对应 Developer Pack。检查路径：{targetingPack}";
        var icon = p("ApplicationIcon");
        if (!string.IsNullOrWhiteSpace(icon)) icon = Path.GetFullPath(icon, projectDir);
        var assembly = p("AssemblyName");
        if (string.IsNullOrEmpty(assembly)) assembly = Path.GetFileNameWithoutExtension(project.ProjectPath);
        var executableName = framework ? p("TargetFileName") : assembly + ".exe";
        if (string.IsNullOrWhiteSpace(executableName))
            executableName = (string.IsNullOrWhiteSpace(p("TargetName")) ? assembly : p("TargetName")) + ".exe";
        return project with
        {
            TargetFramework = tfm, FrameworkVersion = framework ? version : p("TargetFrameworkVersion"),
            IsNetFramework = framework, UiFramework = wpf ? "WPF" : forms ? "WinForms" : "未识别桌面框架",
            PlatformTarget = cpu, Prefer32Bit = IsTrue(p("Prefer32Bit")), AssemblyName = assembly,
            ResolvedExecutableName = executableName,
            DeclaredRuntimeIdentifier = rid, DeclaredRuntimeIdentifiers = rids,
            ProductName = string.IsNullOrEmpty(p("Product")) ? assembly : p("Product"),
            Version = string.IsNullOrWhiteSpace(p("Version")) ? "1.0.0" : p("Version"),
            ApplicationIcon = icon, TargetDirectory = targetDir, TargetingPackDirectory = targetingPack,
            AvailableConfigurations = configs, AvailablePlatforms = platforms, UnsupportedReason = reason,
            Evidence = $"MSBuild {p("MSBuildVersion")} 有效属性；OutputType={p("OutputType")}；"
                + (project.IsSdkStyle ? $"UseWPF={p("UseWPF")}，UseWindowsForms={p("UseWindowsForms")}" : "传统项目引用/ProjectTypeGuids")
                + (framework && project.IsSdkStyle ? "；Framework 桌面引用也已检查" : "")
                + $"；CPU={cpu}" + (project.SolutionPath is null ? "" : $"；解决方案 {project.SolutionConfiguration} → 项目 {project.Configuration}|{project.Platform}")
                + (string.IsNullOrEmpty(rid) ? (rids.Count == 0 ? "" : $"；声明 RID={string.Join(';', rids)}") : $"；固定 RID={rid}")
                + (framework ? $"；目标机需要 .NET Framework {version}" : "")
        };
    }

    private static string FindTargetingPack(Func<string, string> p, string version, string projectDir)
    {
        // Full MSBuild can replace a missing override with the installed CLR implementation.
        // Runtime mscorlib.dll is not proof that the selected Developer Pack is installed.
        foreach (var property in new[] { "FrameworkPathOverride", "TargetFrameworkDirectory" })
        {
            if (string.IsNullOrWhiteSpace(p(property))) continue;
            var directory = Path.GetFullPath(p(property), projectDir);
            if (!IsFrameworkImplementationDirectory(directory)
                && File.Exists(Path.Combine(directory, "mscorlib.dll"))) return directory;
        }
        var root = p("TargetFrameworkRootPath");
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies", "Microsoft", "Framework");
        return Path.GetFullPath(Path.Combine(root, ".NETFramework", version), projectDir);
    }

    private static bool IsFrameworkImplementationDirectory(string directory)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var framework in new[] { "Framework", "Framework64" })
        {
            var implementationRoot = Path.Combine(windows, "Microsoft.NET", framework);
            if (Path.TrimEndingDirectorySeparator(directory).Equals(implementationRoot, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(implementationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static ProjectDescriptor Create(string path, string root) => new()
        { ProjectPath = path, SourceRoot = root, ProjectName = Path.GetFileNameWithoutExtension(path) };
    private static ProjectDescriptor WithUnverifiedFrameworkHint(ProjectDescriptor project, XDocument xml)
    {
        // This hint is only retained on an outer evaluation failure, never used as an evaluated result.
        var hints = xml.Descendants().Where(e => e.Name.LocalName == "TargetFramework")
            .Select(e => e.Value.Trim()).Where(v => v.Length != 0 && !v.Contains("$(", StringComparison.Ordinal)
                && !v.Contains("@(", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hints.Count == 0)
            hints = xml.Descendants().Where(e => e.Name.LocalName == "TargetFrameworkVersion")
                .Select(e => e.Value.Trim()).Where(v => Regex.IsMatch(v, @"^[vV]\d+(?:\.\d+)+$"))
                .Select(v => "net" + v.TrimStart('v', 'V').Replace(".", "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return hints.Count == 1 ? project with { TargetFramework = hints[0],
            Evidence = $"XML 声明目标框架 {hints[0]}（未完成 MSBuild 评估，导入文件及条件可能改变该值）。" } : project;
    }
    private static bool LooksLikeFramework(string tfm) => Regex.IsMatch(tfm.Trim(), @"^net[1-4]\d{1,2}$", RegexOptions.IgnoreCase);
    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);
    private static bool ReferenceIs(string value, string name) => value.Split(',')[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
    private static bool IsSupportedRid(string value) => value.Equals("win-x86", StringComparison.OrdinalIgnoreCase)
        || value.Equals("win-x64", StringComparison.OrdinalIgnoreCase);
    private static bool IsCompatibleRid(string cpu, string rid) => cpu.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)
        || (cpu.Equals("x86", StringComparison.OrdinalIgnoreCase) && rid.Equals("win-x86", StringComparison.OrdinalIgnoreCase))
        || (cpu.Equals("x64", StringComparison.OrdinalIgnoreCase) && rid.Equals("win-x64", StringComparison.OrdinalIgnoreCase));
    private static List<string> Split(string value) => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static bool IsSdk(XDocument xml) => xml.Root?.Attribute("Sdk") is not null
        || xml.Descendants().Any(e => e.Name.LocalName == "Sdk" || (e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null));
    private static XDocument ReadProject(string path)
    {
        RejectLink(path);
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static IReadOnlyList<(string Configuration, string Platform)> DeclaredConfigurations(XDocument xml) =>
        xml.Descendants().Attributes("Condition").Select(a => Regex.Match(a.Value,
            @"(?i)\$\(Configuration\)\s*\|\s*\$\(Platform\)['\"" ]*==\s*['\"" ](?<c>[^|'\""\r\n]+)\|(?<p>[^'\""\r\n]+)"))
        .Where(m => m.Success).Select(m => (m.Groups["c"].Value.Trim(), SolutionProjectReader.NormalizePlatform(m.Groups["p"].Value))).Distinct().ToList();

    private static ProjectDescriptor PickDirectConfiguration(ProjectDescriptor project, XDocument xml)
    {
        var configurations = DeclaredConfigurations(xml);
        var release = configurations.Where(c => c.Configuration.Equals("Release", StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = release.OrderBy(c => c.Platform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        return chosen == default ? project : project with { Configuration = chosen.Configuration, Platform = chosen.Platform };
    }

    private static IEnumerable<string> Scan(string root, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        RejectLink(root);
        stack.Push(root);
        while (stack.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException($"无法扫描目录 {current}：{ex.Message}", ex); }
            foreach (var path in entries.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(path))) stack.Push(path);
                }
                else if (Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase)) yield return path;
            }
        }
    }

    private static void RejectLink(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"不支持链接路径：{current}");
    }

    private sealed record Evaluation(Dictionary<string, string> Properties, IReadOnlyList<string> References)
    {
        public string Get(string name) => Properties.GetValueOrDefault(name, "");
    }
}
