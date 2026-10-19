using System.Security;

namespace DLTools.Tests;

public static class ProjectFixtures
{
    public static string Sdk(TestWorkspace workspace, string folder, bool wpf = true,
        string framework = "net8.0-windows", string extraProperties = "", bool executable = true)
    {
        var projectName = Path.GetFileName(folder);
        var frameworkElement = framework.Contains(';') ? "TargetFrameworks" : "TargetFramework";
        var path = workspace.Write(Path.Combine(folder, projectName + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>{{(executable ? "WinExe" : "Library")}}</OutputType>
                <{{frameworkElement}}>{{framework}}</{{frameworkElement}}>
                <{{(wpf ? "UseWPF" : "UseWindowsForms")}}>true</{{(wpf ? "UseWPF" : "UseWindowsForms")}}>
                <AssemblyName>{{SecurityElement.Escape(projectName)}}</AssemblyName>
                <Version>2.3.4</Version>
                <PlatformTarget>AnyCPU</PlatformTarget>
                {{extraProperties}}
              </PropertyGroup>
            </Project>
            """);
        WriteProgram(workspace, folder, wpf);
        workspace.Write(Path.Combine(folder, "runtime-data.xml"), "<runtime>keep me</runtime>");
        // This is runtime content, not documentation, and must survive the packaging pipeline.
        var project = File.ReadAllText(path).Replace("</Project>", """
            <ItemGroup>
              <Content Include="runtime-data.xml"><CopyToOutputDirectory>Always</CopyToOutputDirectory></Content>
            </ItemGroup>
            </Project>
            """);
        File.WriteAllText(path, project);
        return path;
    }

    public static string Framework(TestWorkspace workspace, string folder, bool wpf = false,
        string extraProperties = "", string additionalItems = "")
    {
        var name = Path.GetFileName(folder);
        var references = wpf
            ? "<Reference Include=\"WindowsBase\"/><Reference Include=\"PresentationCore\"/><Reference Include=\"PresentationFramework\"/><Reference Include=\"System.Xaml\"/>"
            : "<Reference Include=\"System.Windows.Forms\"/><Reference Include=\"System.Drawing\"/>";
        var path = workspace.Write(Path.Combine(folder, name + ".csproj"), $$"""
            <Project ToolsVersion="14.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
                <Platform Condition="'$(Platform)' == ''">AnyCPU</Platform>
                <OutputType>WinExe</OutputType><TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                <ProjectGuid>{BCDE1234-1111-2222-3333-123456789000}</ProjectGuid>
                <AssemblyName>{{SecurityElement.Escape(name)}}</AssemblyName>
                <RootNamespace>FixtureApp</RootNamespace>
                <OutputPath>bin\$(Configuration)\</OutputPath>
                <PlatformTarget>AnyCPU</PlatformTarget>
                {{extraProperties}}
              </PropertyGroup>
              <ItemGroup><Reference Include="System"/><Reference Include="System.Core"/>
                {{references}}<Compile Include="Program.cs"/>
                <Content Include="runtime-data.xml"><CopyToOutputDirectory>Always</CopyToOutputDirectory></Content>
                {{additionalItems}}
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets"/>
            </Project>
            """);
        WriteProgram(workspace, folder, wpf);
        workspace.Write(Path.Combine(folder, "runtime-data.xml"), "<runtime>keep me</runtime>");
        return path;
    }

    private static void WriteProgram(TestWorkspace workspace, string folder, bool wpf)
    {
        var create = wpf ? "var app = new System.Windows.Application(); var window = new System.Windows.Window();"
            : "var window = new System.Windows.Forms.Form();";
        var run = wpf ? "app.Run(window);" : "System.Windows.Forms.Application.Run(window);";
        workspace.Write(Path.Combine(folder, "Program.cs"), $$"""
            namespace FixtureApp
            {
                internal static class Program
                {
                    [System.STAThread]
                    private static void Main(string[] args)
                    {
                        {{create}}
                        if (args.Length == 2 && args[0] == "--probe")
                        {
                            System.IO.File.WriteAllText(args[1], "started");
                            return;
                        }
                        {{run}}
                    }
                }
            }
            """);
    }
}
