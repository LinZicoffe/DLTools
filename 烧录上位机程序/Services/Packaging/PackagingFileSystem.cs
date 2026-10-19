using System.IO;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

public static class PackagingFileSystem
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".sln", ".slnx", ".vb", ".vbproj", ".fs", ".fsproj", ".vcxproj", ".xaml", ".props", ".targets" };

    public static string NormalizeInstallerFileName(string name)
    {
        ValidateSegment(name);
        var result = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        ValidateSegment(result);
        return result;
    }

    public static string SafeFileSegment(string name)
    {
        var safe = new string(name.Select(c => c < 32 || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(safe) || safe is "." or "..") safe = "Application";
        if (IsReserved(safe)) safe = "_" + safe;
        ValidateSegment(safe);
        return safe;
    }

    internal static void ValidateSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < 32 || Path.GetInvalidFileNameChars().Contains(c)) || IsReserved(name))
            throw new ArgumentException($"不是合法的 Windows 文件名：{name}");
    }

    private static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             "123456789¹²³".Contains(stem[3]));
    }

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("目录不能为空。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("不支持设备路径或扩展路径。");
        var root = Path.GetPathRoot(full)!;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            ValidateSegment(segment);
        return full;
    }

    internal static bool IsWithin(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void EnsureNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            // GetAttributes also detects dangling directory links; Exists intentionally does not.
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"不允许链接或重解析点：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }

    public static string ValidateMutableDirectory(string path, ProjectDescriptor project)
    {
        var full = FullPath(path);
        EnsureNoLinks(full);
        if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"不能替换磁盘根目录：{full}");
        foreach (var source in new[] { project.SourceRoot, Path.GetDirectoryName(project.ProjectPath) })
        {
            if (!string.IsNullOrEmpty(source) && IsWithin(FullPath(source), full))
                throw new IOException($"不能替换源码根目录或其祖先：{full}");
        }
        if (File.Exists(full)) throw new IOException($"目录位置存在文件：{full}");
        if (Directory.Exists(full)) CheckTree(full, rejectSource: true);
        return full;
    }

    internal static void CheckTree(string root, bool rejectSource = false)
    {
        EnsureNoLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            EnsureNoLinks(entry);
            if (Directory.Exists(entry)) CheckTree(entry, rejectSource);
            else if (rejectSource && SourceExtensions.Contains(Path.GetExtension(entry)))
                throw new IOException($"待替换目录含有源码文件：{entry}");
        }
    }

    public static void EnsureDisjoint(params string[] directories)
    {
        var full = directories.Select(FullPath).ToArray();
        for (var i = 0; i < full.Length; i++)
            for (var j = i + 1; j < full.Length; j++)
                if (IsWithin(full[i], full[j]) || IsWithin(full[j], full[i]))
                    throw new IOException($"目录不能相同或互相嵌套：{full[i]}；{full[j]}");
    }

    public static void CopyTree(string source, string destination, bool excludeDebug = true)
    {
        source = FullPath(source);
        destination = FullPath(destination);
        EnsureDisjoint(source, destination);
        CheckTree(source);
        EnsureNoLinks(destination);
        var files = new List<(string Source, string Target)>();
        var directories = new List<string>();
        PlanCopy(source, destination, excludeDebug, files, directories);
        ApplyCopies(files, directories);
    }

    private static void PlanCopy(string source, string target, bool excludeDebug,
        List<(string Source, string Target)> files, List<string> directories)
    {
        EnsureNoLinks(source);
        EnsureNoLinks(target);
        if (File.Exists(target)) throw new IOException($"资源目录与现有文件冲突：{target}");
        directories.Add(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            EnsureNoLinks(entry);
            var name = Path.GetFileName(entry);
            ValidateSegment(name);
            var next = Path.Combine(target, name);
            if (Directory.Exists(entry)) PlanCopy(entry, next, excludeDebug, files, directories);
            else if (!excludeDebug || !(name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.Contains(".vshost.", StringComparison.OrdinalIgnoreCase)))
            {
                EnsureNoLinks(next);
                if (File.Exists(next) || Directory.Exists(next)) throw new IOException($"资源与现有文件冲突：{next}");
                files.Add((entry, next));
            }
        }
    }

    private static void ApplyCopies(List<(string Source, string Target)> files, List<string> directories)
    {
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (!destinations.Add(file.Target) || directories.Contains(file.Target, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"资源目标重复：{file.Target}");
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnsureNoLinks(directory);
            Directory.CreateDirectory(directory);
        }
        foreach (var file in files)
        {
            EnsureNoLinks(file.Source);
            EnsureNoLinks(file.Target);
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            File.Copy(file.Source, file.Target, overwrite: false);
        }
    }

    public static void AddResources(string staging, IReadOnlyList<ExtraResourceMapping> resources)
    {
        staging = FullPath(staging);
        EnsureNoLinks(staging);
        var files = new List<(string Source, string Target)>();
        var directories = new List<string>();
        foreach (var resource in resources)
        {
            var source = FullPath(resource.SourcePath);
            EnsureNoLinks(source);
            var isDirectory = Directory.Exists(source);
            var relative = resource.Destination;
            if (!isDirectory && relative.EndsWith(Path.DirectorySeparatorChar) || !isDirectory && relative.EndsWith(Path.AltDirectorySeparatorChar))
                throw new ArgumentException("文件资源目标必须包含明确文件名。");
            if (Path.IsPathRooted(relative) || (!isDirectory && (string.IsNullOrWhiteSpace(relative) || relative == ".")))
                throw new ArgumentException("额外资源目标必须是安装目录内的相对路径；文件需要完整目标文件名。");
            var parts = relative.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
                if (!(isDirectory && parts.Length == 1 && part == ".")) ValidateSegment(part);
            var target = FullPath(Path.Combine(staging, relative));
            if (!IsWithin(target, staging)) throw new IOException("额外资源路径越界。");
            EnsureNoLinks(target);
            if (isDirectory)
            {
                EnsureDisjoint(source, staging);
                CheckTree(source);
                PlanCopy(source, target, false, files, directories);
            }
            else
            {
                if (!File.Exists(source)) throw new FileNotFoundException("额外资源不存在。", source);
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"资源与构建产物冲突：{target}");
                directories.Add(Path.GetDirectoryName(target)!);
                files.Add((source, target));
            }
        }
        ApplyCopies(files, directories);
    }
}



