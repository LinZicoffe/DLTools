using Xunit;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services.Packaging;

namespace DLTools.Tests;

public sealed class PackagingFileSystemTests
{
    [Theory]
    [InlineData("setup", "setup.exe")]
    [InlineData("中文 Setup.EXE", "中文 Setup.EXE")]
    public void NormalizeInstallerFileName_AddsOnlyMissingExtension(string input, string expected) =>
        Assert.Equal(expected, PackagingFileSystem.NormalizeInstallerFileName(input));

    [Theory]
    [InlineData("../escape")]
    [InlineData("a\\b")]
    [InlineData("CON.exe")]
    [InlineData("lpt1")]
    [InlineData("COM¹.exe")]
    [InlineData("setup.")]
    [InlineData("file:stream")]
    [InlineData("setup ")]
    [InlineData("bad\nname")]
    [InlineData("")]
    public void NormalizeInstallerFileName_RejectsUnsafeNames(string input) =>
        Assert.Throws<ArgumentException>(() => PackagingFileSystem.NormalizeInstallerFileName(input));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("..", "Application")]
    public void SafeFileSegment_ProducesLegalSegment(string input, string expected) =>
        Assert.Equal(expected, PackagingFileSystem.SafeFileSegment(input));

    [Fact]
    public void ValidateMutableDirectory_ProtectsSourceAndAncestorsButAllowsBin()
    {
        using var ws = new TestWorkspace();
        var project = new ProjectDescriptor { ProjectPath = ws.Write("source/fixture.csproj", "<Project/>"), SourceRoot = Path.Combine(ws.Root, "source") };
        Assert.Throws<IOException>(() => PackagingFileSystem.ValidateMutableDirectory(ws.Root, project));
        Assert.Throws<IOException>(() => PackagingFileSystem.ValidateMutableDirectory(project.SourceRoot, project));
        Assert.Throws<IOException>(() => PackagingFileSystem.ValidateMutableDirectory(Path.GetPathRoot(ws.Root)!, project));
        var output = Path.Combine(project.SourceRoot, "bin/Release");
        Assert.Equal(Path.GetFullPath(output), PackagingFileSystem.ValidateMutableDirectory(output, project));
        ws.Write("source/bin/Release/unexpected.cs", "class Source {}");
        Assert.Throws<IOException>(() => PackagingFileSystem.ValidateMutableDirectory(output, project));
    }

    [Fact]
    public void EnsureNoLinks_RejectsJunctionAndMissingDescendant()
    {
        using var ws = new TestWorkspace();
        var actual = Path.Combine(ws.Root, "actual");
        Directory.CreateDirectory(actual);
        var link = Path.Combine(ws.Root, "link");
        // Windows junctions do not need Developer Mode or symbolic-link privileges.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { ArgumentList = { "/c", "mklink", "/J", link, actual }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "Junction creation failed; link coverage was not executed: " + process.StandardError.ReadToEnd());
        Assert.Throws<IOException>(() => PackagingFileSystem.EnsureNoLinks(link));
        Assert.Throws<IOException>(() => PackagingFileSystem.EnsureNoLinks(Path.Combine(link, "missing/child")));
        Assert.Throws<IOException>(() => PackagingFileSystem.CopyTree(ws.Root, Path.Combine(ws.Root, "copy")));
    }

    [Fact]
    public void CopyTree_PreservesRuntimeNativeResourcesAndRejectsConflict()
    {
        using var ws = new TestWorkspace();
        foreach (var file in new[] { "app.exe", "app.xml", "app.deps.json", "native/x.dll", "zh-CN/app.resources.dll", "app.pdb", "app.vshost.exe" })
            ws.Write("build/" + file, file);
        var source = Path.Combine(ws.Root, "build");
        var destination = Path.Combine(ws.Root, "stage");
        PackagingFileSystem.CopyTree(source, destination);
        Assert.Equal(5, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(destination, "app.deps.json")));
        Assert.True(File.Exists(Path.Combine(destination, "zh-CN/app.resources.dll")));
        Assert.Throws<IOException>(() => PackagingFileSystem.CopyTree(source, destination));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/escape.txt")]
    [InlineData("C:\\escape.txt")]
    [InlineData("NUL")]
    [InlineData("assets/../escape.txt")]
    [InlineData("assets./file")]
    [InlineData("folder/")]
    [InlineData("")]
    public void AddResources_RejectsInvalidFileTargets(string target)
    {
        using var ws = new TestWorkspace();
        var source = ws.Write("resource.txt", "resource");
        Assert.ThrowsAny<Exception>(() => PackagingFileSystem.AddResources(Path.Combine(ws.Root, "stage"), [new(source, target)]));
        Assert.False(File.Exists(Path.Combine(ws.Root, "escape.txt")));
    }

    [Fact]
    public void AddResources_MergesDirectoriesWithoutReplacingBuildArtifacts()
    {
        using var ws = new TestWorkspace();
        ws.Write("stage/app.exe", "binary");
        ws.Write("resources/sub/settings.json", "{}");
        PackagingFileSystem.AddResources(Path.Combine(ws.Root, "stage"), [new(Path.Combine(ws.Root, "resources"), ".")]);
        Assert.True(File.Exists(Path.Combine(ws.Root, "stage/sub/settings.json")));
        var collision = ws.Write("other.txt", "changed");
        Assert.Throws<IOException>(() => PackagingFileSystem.AddResources(Path.Combine(ws.Root, "stage"), [new(collision, "app.exe")]));
        Assert.Equal("binary", File.ReadAllText(Path.Combine(ws.Root, "stage/app.exe")));
        Assert.Throws<IOException>(() => PackagingFileSystem.AddResources(Path.Combine(ws.Root, "stage"), [new(collision, "new.txt"), new(collision, "new.txt")]));
        Assert.False(File.Exists(Path.Combine(ws.Root, "stage/new.txt")));
    }
    [Fact]
    public void ValidateMutableDirectory_RejectsWindowsNormalizedAliases()
    {
        using var ws = new TestWorkspace();
        var project = new ProjectDescriptor { ProjectPath = ws.Write("source/fixture.csproj", "<Project/>"), SourceRoot = Path.Combine(ws.Root, "source") };
        Assert.ThrowsAny<Exception>(() => PackagingFileSystem.ValidateMutableDirectory(project.SourceRoot + ".", project));
        Assert.ThrowsAny<Exception>(() => PackagingFileSystem.ValidateMutableDirectory(project.SourceRoot + " ", project));
        Assert.Throws<ArgumentException>(() => PackagingFileSystem.NormalizeInstallerFileName(new string('a', 253)));
    }
}


