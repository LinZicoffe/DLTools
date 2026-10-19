using System.IO;
using System.Text.RegularExpressions;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

internal static class SolutionProjectReader
{
    internal static IReadOnlyList<ProjectDescriptor> Read(string path, string sourceRoot)
    {
        var text = File.ReadAllText(path);
        var configurations = Regex.Matches(text,
            @"(?m)^\s*(?<config>[^\r\n=]+\|[^\r\n=]+)\s*=\s*[^\r\n]+\r?$")
            .Select(m => m.Groups["config"].Value.Trim())
            .Where(s => s.StartsWith("Release|", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.Equals("Release|Any CPU", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        var result = new List<ProjectDescriptor>();
        foreach (Match match in Regex.Matches(text,
            "(?m)^Project\\(\"[^\"]+\"\\)\\s*=\\s*\"(?<name>[^\"]+)\",\\s*\"(?<path>[^\"]+)\",\\s*\"(?<id>[^\"]+)\""))
        {
            var relative = match.Groups["path"].Value;
            if (!relative.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) continue;
            var projectPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative));
            var basis = new ProjectDescriptor
            {
                ProjectPath = projectPath, ProjectName = match.Groups["name"].Value,
                SourceRoot = sourceRoot, SolutionPath = path
            };
            if (!relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(basis with { UnsupportedReason = "首版仅支持 C# .csproj 项目。" });
                continue;
            }
            if (configurations.Count == 0)
            {
                result.Add(basis with { UnsupportedReason = "解决方案未声明 Release 构建配置。" });
                continue;
            }
            foreach (var configuration in configurations)
            {
                var mapping = Regex.Match(text, @"(?im)^\s*" + Regex.Escape(match.Groups["id"].Value)
                    + @"\." + Regex.Escape(configuration) + @"\.ActiveCfg\s*=\s*(?<value>[^\r\n]+)");
                if (!mapping.Success || mapping.Groups["value"].Value.Trim().Split('|') is not { Length: 2 } parts)
                {
                    result.Add(basis with { SolutionConfiguration = configuration,
                        UnsupportedReason = $"解决方案 {configuration} 缺少项目 ActiveCfg 映射。" });
                    continue;
                }
                result.Add(basis with { SolutionConfiguration = configuration,
                    Configuration = parts[0].Trim(), Platform = NormalizePlatform(parts[1]) });
            }
        }
        return result;
    }

    internal static string NormalizePlatform(string value) =>
        value.Trim().Equals("Any CPU", StringComparison.OrdinalIgnoreCase) ? "AnyCPU" : value.Trim();
}
