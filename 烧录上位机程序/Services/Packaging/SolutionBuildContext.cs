using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

/// <summary>Builds one entry project while preserving the solution's reference configuration map.</summary>
internal static class SolutionBuildContext
{
    internal static IReadOnlyList<string> Arguments(ProjectDescriptor project)
    {
        var arguments = new List<string>
        {
            Property("Configuration", project.Configuration), Property("Platform", project.Platform)
        };
        if (project.SolutionPath is not { } solution) return arguments;
        PackagingFileSystem.EnsureNoLinks(solution);
        if (!File.Exists(solution)) throw new FileNotFoundException("解决方案文件已不存在，请重新导入。", solution);
        var text = File.ReadAllText(solution);
        var root = new XElement("SolutionConfiguration");
        var entryFound = false;
        foreach (Match match in Regex.Matches(text,
                     "(?m)^Project\\(\"[^\"]+\"\\)\\s*=\\s*\"[^\"]+\",\\s*\"(?<path>[^\"]+)\",\\s*\"(?<id>[^\"]+)\""))
        {
            var relative = match.Groups["path"].Value;
            if (!relative.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.GetFullPath(relative, Path.GetDirectoryName(solution)!);
            var id = match.Groups["id"].Value;
            var mapping = Regex.Match(text, @"(?im)^\s*" + Regex.Escape(id) + @"\."
                + Regex.Escape(project.SolutionConfiguration) + @"\.ActiveCfg\s*=\s*(?<value>[^\r\n]+)");
            if (!mapping.Success) throw new InvalidOperationException($"解决方案 {project.SolutionConfiguration} 缺少项目 {relative} 的 ActiveCfg 映射，请修复后重新导入。");
            var parts = mapping.Groups["value"].Value.Trim().Split('|');
            if (parts.Length != 2) throw new InvalidOperationException("解决方案配置映射无效：" + relative);
            var mapped = parts[0].Trim() + "|" + SolutionProjectReader.NormalizePlatform(parts[1]);
            var enabled = Regex.IsMatch(text, @"(?im)^\s*" + Regex.Escape(id) + @"\."
                + Regex.Escape(project.SolutionConfiguration) + @"\.Build\.0\s*=");
            root.Add(new XElement("ProjectConfiguration", new XAttribute("Project", id),
                new XAttribute("AbsolutePath", path), new XAttribute("BuildProjectInSolution", enabled), mapped));
            if (path.Equals(Path.GetFullPath(project.ProjectPath), StringComparison.OrdinalIgnoreCase))
            {
                entryFound = true;
                if (!mapped.Equals(project.Configuration + "|" + project.Platform, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("所选入口的解决方案配置映射已改变，请重新导入项目。");
            }
        }
        if (!entryFound) throw new InvalidOperationException("解决方案已不再包含所选入口，请重新导入项目。");
        arguments.AddRange([
            Property("SolutionDir", Path.GetDirectoryName(solution)! + Path.DirectorySeparatorChar),
            Property("SolutionPath", solution), Property("SolutionFileName", Path.GetFileName(solution)),
            Property("SolutionName", Path.GetFileNameWithoutExtension(solution)),
            Property("CurrentSolutionConfigurationContents", root.ToString(SaveOptions.DisableFormatting)),
            Property("ShouldUnsetParentConfigurationAndPlatform", "true")
        ]);
        return arguments;
    }

    // Process arguments are already quoted by ExternalProcessRunner. These escapes are for MSBuild's
    // property parser, which otherwise interprets commas/semicolons and expands metadata expressions.
    internal static string Property(string name, string value) => "-p:" + name + "=" + Escape(value);

    private static string Escape(string value) => string.Concat(value.Select(c =>
        "%*?@$();,'\"<>=\r\n".Contains(c) ? "%" + ((int)c).ToString("X2") : c.ToString()));
}
