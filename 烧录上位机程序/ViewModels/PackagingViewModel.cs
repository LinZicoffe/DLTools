using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 烧录上位机程序.Models.Packaging;
using 烧录上位机程序.Services;
using 烧录上位机程序.Services.Packaging;

namespace 烧录上位机程序.ViewModels;

public partial class ResourceMappingViewModel : ObservableObject
{
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _destination = "";
}

public partial class PackagingViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private PackagingProfileStore? _profiles;
    private Task? _ownedTask;
    private ProjectDescriptor? _profileFor;
    private Guid _loadedStableAppId;
    private bool _initialized;
    private bool _suppressSelection;
    private bool _applyingProfile;
    private string _previousDefaultName = "";
    private readonly Queue<string> _logLines = new();
    private const int LogLineLimit = 2000;

    public ObservableCollection<ProjectDescriptor> Candidates { get; } = [];
    public ObservableCollection<string> Configurations { get; } = [];
    public ObservableCollection<string> Platforms { get; } = [];
    public ObservableCollection<string> RuntimeIdentifiers { get; } = [];
    public ObservableCollection<ResourceMappingViewModel> Resources { get; } = [];
    public IReadOnlyList<string> ModeNames { get; } = ["自动生成模板", "使用已有脚本"];

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private ProjectDescriptor? _selectedCandidate;
    [ObservableProperty] private string _configuration = "Release";
    [ObservableProperty] private string _platform = "AnyCPU";
    [ObservableProperty] private string _projectSummary = "导入项目后，在此查看界面框架、运行时和 CPU 的识别依据。";
    [ObservableProperty] private string _selectionMessage = "支持本地文件夹、.sln 和 .csproj；多个入口或目标框架需要手动选择。";
    [ObservableProperty] private string _dotNetPath = "";
    [ObservableProperty] private string _msBuildPath = "";
    [ObservableProperty] private string _innoCompilerPath = "";
    [ObservableProperty] private string _toolStatus = "尚未检查工具。现代 .NET 需要 SDK；Framework 需要 VS MSBuild 和对应开发包。";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _installerFileName = "";
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _appVersion = "1.0.0";
    [ObservableProperty] private string _appIdText = "";
    [ObservableProperty] private string _iconPath = "";
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private string _runtimeIdentifier = "win-x64";
    [ObservableProperty] private bool _selfContained = true;
    [ObservableProperty] private bool _createDesktopShortcut = true;
    [ObservableProperty] private bool _launchAfterInstall = true;
    [ObservableProperty] private string _existingScriptPath = "";
    [ObservableProperty] private string _scriptInputDirectory = "";
    [ObservableProperty] private ResourceMappingViewModel? _selectedResource;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "准备就绪";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _lastInstallerPath = "";
    [ObservableProperty] private string _lastLogPath = "";
    [ObservableProperty] private string _profileError = "";

    public bool IsIdle => !IsBusy;
    public bool IsGeneratedMode => ModeIndex == 0;
    public bool IsExistingMode => ModeIndex == 1;
    public bool IsDirectProject => SelectedCandidate is { SolutionPath: null };
    public bool IsModernProject => SelectedCandidate is { IsSupported: true, IsNetFramework: false };
    public bool CanStart => !IsBusy && string.IsNullOrEmpty(ProfileError) && SelectedCandidate is { IsSupported: true } selected
        && ReferenceEquals(_profileFor, selected)
        && (selected.SolutionPath is not null || (Configuration.Trim() == selected.Configuration && NormalizePlatform(Platform) == NormalizePlatform(selected.Platform)));
    public bool HasOutput => File.Exists(LastInstallerPath);
    public bool HasLog => File.Exists(LastLogPath);
    public string MainExecutable => SelectedCandidate?.MainExecutable ?? "尚未选择项目";
    public string RuntimeExplanation => SelectedCandidate is { } project
        ? project.IsNetFramework
            ? $"目标机必须安装 .NET Framework {project.FrameworkVersion}；Framework 不支持独立部署，保持项目 CPU 设置 {project.PlatformTarget}。"
            : SelfContained ? "独立部署：携带所选架构的 .NET 运行时，安装包体积较大。" : $"依赖运行时：目标机需安装匹配 {project.TargetFramework} 和 {RuntimeIdentifier} 的 .NET Desktop Runtime。"
        : "请选择项目查看运行时要求。";

    partial void OnSourcePathChanged(string value)
    {
        if (_applyingProfile) return;
        _suppressSelection = true;
        SelectedCandidate = null;
        Candidates.Clear();
        _suppressSelection = false;
        SelectionMessage = "导入路径已修改，请重新识别；旧项目不能继续打包。";
    }

    partial void OnSelectedCandidateChanged(ProjectDescriptor? value)
    {
        _profileFor = null;
        _loadedStableAppId = Guid.Empty;
        AppIdText = "";
        OnPropertyChanged(nameof(IsDirectProject));
        OnPropertyChanged(nameof(IsModernProject));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(MainExecutable));
        OnPropertyChanged(nameof(RuntimeExplanation));
        Configurations.Clear();
        Platforms.Clear();
        RuntimeIdentifiers.Clear();
        if (value is null) { ProjectSummary = "尚未选择项目。"; return; }
        Configuration = value.Configuration;
        Platform = value.Platform;
        foreach (var item in value.AvailableConfigurations) Configurations.Add(item);
        foreach (var item in value.AvailablePlatforms) Platforms.Add(item);
        foreach (var item in PackagingService.AllowedRuntimeIdentifiers(value)) RuntimeIdentifiers.Add(item);
        ProjectSummary = Describe(value);
        if (!_suppressSelection) _ = LoadProfileAsync(value);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanStart));
    }
    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsGeneratedMode));
        OnPropertyChanged(nameof(IsExistingMode));
    }
    partial void OnConfigurationChanged(string value) => OnPropertyChanged(nameof(CanStart));
    partial void OnPlatformChanged(string value) => OnPropertyChanged(nameof(CanStart));
    partial void OnSelfContainedChanged(bool value) => OnPropertyChanged(nameof(RuntimeExplanation));
    partial void OnRuntimeIdentifierChanged(string value) => OnPropertyChanged(nameof(RuntimeExplanation));
    partial void OnProfileErrorChanged(string value) => OnPropertyChanged(nameof(CanStart));
    partial void OnLastInstallerPathChanged(string value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnLastLogPathChanged(string value) => OnPropertyChanged(nameof(HasLog));
    partial void OnAppNameChanged(string value) => UpdateDefaultFileName();
    partial void OnAppVersionChanged(string value) => UpdateDefaultFileName();

    private void UpdateDefaultFileName()
    {
        if (_applyingProfile) return;
        var next = PackagingProfileStore.DefaultFileName(AppName, AppVersion);
        if (InstallerFileName == _previousDefaultName || string.IsNullOrWhiteSpace(InstallerFileName)) InstallerFileName = next;
        _previousDefaultName = next;
    }

    public async Task InitializeAsync()
    {
        if (_initialized || IsBusy) return;
        PackagingProfileStore? store = null;
        BuildTools? tools = null;
        var success = await RunOwnedAsync("正在检查打包环境", async token =>
        {
            store = new PackagingProfileStore();
            var savedTools = store.LastTools;
            var overrides = new BuildTools
            {
                DotNetPath = string.IsNullOrWhiteSpace(DotNetPath) ? savedTools.DotNetPath : DotNetPath,
                MsBuildPath = string.IsNullOrWhiteSpace(MsBuildPath) ? savedTools.MsBuildPath : MsBuildPath,
                InnoCompilerPath = string.IsNullOrWhiteSpace(InnoCompilerPath) ? savedTools.InnoCompilerPath : InnoCompilerPath
            };
            tools = await new BuildToolLocator().LocateAsync(overrides, token);
        });
        if (!success || store is null || tools is null)
        {
            // A damaged profile must not silently generate another installation identity.
            if (Status.StartsWith("失败：", StringComparison.Ordinal))
                ProfileError = Status + "\n请保留并修复 %APPDATA%\\JLinkRTTViewer\\packaging\\profiles.json 后重新打开程序。";
            return;
        }
        _profiles = store;
        _applyingProfile = true;
        if (string.IsNullOrWhiteSpace(SourcePath)) SourcePath = store.LastSourcePath ?? "";
        _applyingProfile = false;
        ApplyTools(tools);
        _initialized = true;
        await CheckToolsAsync();
    }

    [RelayCommand] private async Task DetectToolsAsync()
    {
        BuildTools? tools = null;
        if (await RunOwnedAsync("正在自动定位工具", async token => tools = await new BuildToolLocator().LocateAsync(cancellationToken: token)))
        {
            ApplyTools(tools!);
            await CheckToolsAsync();
        }
    }

    [RelayCommand] private async Task CheckToolsAsync()
    {
        var tools = SnapshotTools();
        IReadOnlyList<ToolDiagnostic>? results = null;
        if (!await RunOwnedAsync("正在检查工具", async token => results = await new BuildToolLocator().DiagnoseAsync(tools, token))) return;
        ToolStatus = string.Join("\n", results!.Select(item =>
            $"{(item.Available ? "✓" : "缺少/不可用")} {item.Name}：{(item.Available ? "已就绪" : FirstLines(item.Message, 3))}\n  {(string.IsNullOrEmpty(item.Path) ? "未定位，请手动设置路径" : item.Path)}"));
    }

    [RelayCommand] private async Task InspectAsync()
    {
        if (!_initialized) await InitializeAsync();
        if (_profiles is null || !string.IsNullOrEmpty(ProfileError)) return;
        var source = SourcePath.Trim();
        var tools = SnapshotTools();
        IReadOnlyList<ProjectDescriptor>? candidates = null;
        _suppressSelection = true;
        SelectedCandidate = null;
        Candidates.Clear();
        _suppressSelection = false;
        if (!await RunOwnedAsync("正在识别项目", async token =>
        {
            _profiles.SaveToolsAndSource(tools, source);
            candidates = await new ProjectInspectionService().InspectAsync(source, tools, token);
        })) return;
        foreach (var candidate in candidates!) Candidates.Add(candidate);
        var supported = Candidates.Where(candidate => candidate.IsSupported).ToArray();
        SelectionMessage = supported.Length == 1 ? "已选中唯一受支持的入口；请确认目标框架及识别结果。"
            : supported.Length > 1 ? "发现多个可执行项目或目标框架，请在下拉列表中手动选择。"
            : "未发现可打包入口。可选中候选查看具体原因，并检查工具路径。";
        if (supported.Length == 1) SelectedCandidate = supported[0];
    }

    [RelayCommand] private async Task ReevaluateAsync()
    {
        var project = SelectedCandidate;
        if (project is null || project.SolutionPath is not null) return;
        var tools = SnapshotTools();
        var requested = project with { Configuration = Configuration.Trim(), Platform = Platform.Trim() };
        ProjectDescriptor? evaluated = null;
        if (!await RunOwnedAsync("正在重新评估配置", async token =>
            evaluated = await new ProjectInspectionService().EvaluateAsync(requested, tools, token))) return;
        var index = Candidates.IndexOf(project);
        _suppressSelection = true;
        if (index >= 0) Candidates[index] = evaluated!;
        SelectedCandidate = evaluated;
        _suppressSelection = false;
        if (evaluated is { IsSupported: true }) await LoadProfileAsync(evaluated);
    }

    private async Task LoadProfileAsync(ProjectDescriptor project)
    {
        if (!project.IsSupported || _profiles is null || !string.IsNullOrEmpty(ProfileError)) return;
        PackagingProfile? profile = null;
        if (!await RunOwnedAsync("正在读取项目配置", _ => { profile = _profiles.GetOrCreate(project); return Task.CompletedTask; }))
        {
            if (Status.StartsWith("失败：", StringComparison.Ordinal))
                ProfileError = Status + "\n项目配置未加载，已阻止打包。请保留并修复配置文件：" + _profiles.FilePath;
            return;
        }
        if (!ReferenceEquals(SelectedCandidate, project)) return;
        _applyingProfile = true;
        try
        {
            if (profile is null || profile.OutputDirectory is null || profile.InstallerFileName is null
                || profile.AppName is null || profile.AppVersion is null || profile.IconPath is null
                || profile.RuntimeIdentifier is null || profile.ExistingScriptPath is null || profile.ScriptInputDirectory is null
                || profile.ExtraResources is null || profile.ExtraResources.Any(item => item is null || item.SourcePath is null || item.Destination is null)
                || profile.Mode is not InstallerMode.Generated and not InstallerMode.ExistingScript)
                throw new IOException("项目打包配置字段无效，请保留并修复原配置文件：" + _profiles.FilePath);
            OutputDirectory = profile.OutputDirectory;
            InstallerFileName = profile.InstallerFileName;
            AppName = profile.AppName;
            AppVersion = profile.AppVersion;
            AppIdText = profile.AppId.ToString("D");
            IconPath = profile.IconPath;
            ModeIndex = (int)profile.Mode;
            RuntimeIdentifier = RuntimeIdentifiers.Contains(profile.RuntimeIdentifier) ? profile.RuntimeIdentifier
                : RuntimeIdentifiers.FirstOrDefault() ?? "";
            SelfContained = profile.SelfContained;
            CreateDesktopShortcut = profile.CreateDesktopShortcut;
            LaunchAfterInstall = profile.LaunchAfterInstall;
            ExistingScriptPath = profile.ExistingScriptPath;
            ScriptInputDirectory = profile.ScriptInputDirectory;
            Resources.Clear();
            foreach (var item in profile.ExtraResources) Resources.Add(new() { SourcePath = item.SourcePath, Destination = item.Destination });
            _previousDefaultName = PackagingProfileStore.DefaultFileName(AppName, AppVersion);
            _loadedStableAppId = profile.AppId;
            _profileFor = project;
            OnPropertyChanged(nameof(CanStart));
        }
        catch (Exception error)
        {
            _profileFor = null;
            AppIdText = "";
            ProfileError = "项目配置未加载，已阻止打包：" + error.Message;
            Status = "项目配置读取失败";
            ResultText = ProfileError;
            OnPropertyChanged(nameof(CanStart));
        }
        finally { _applyingProfile = false; }
    }

    [RelayCommand] private async Task StartAsync()
    {
        if (!CanStart) return;
        PackagingOptions options;
        try
        {
            if (!Guid.TryParse(AppIdText, out var appId) || appId == Guid.Empty)
            {
                if (IsExistingMode && _loadedStableAppId != Guid.Empty) appId = _loadedStableAppId;
                else throw new ArgumentException("AppId 必须是有效的非空 GUID。已有项目请保持原标识以支持升级。");
            }
            options = new PackagingOptions
            {
                Project = SelectedCandidate!, Tools = SnapshotTools(), Mode = (InstallerMode)ModeIndex,
                OutputDirectory = OutputDirectory.Trim(), InstallerFileName = PackagingFileSystem.NormalizeInstallerFileName(InstallerFileName.Trim()),
                AppName = AppName.Trim(), AppVersion = AppVersion.Trim(), AppId = appId, IconPath = IconPath.Trim(),
                RuntimeIdentifier = RuntimeIdentifier, SelfContained = SelfContained,
                CreateDesktopShortcut = CreateDesktopShortcut, LaunchAfterInstall = LaunchAfterInstall,
                ExistingScriptPath = ExistingScriptPath.Trim(), ScriptInputDirectory = ScriptInputDirectory.Trim(),
                ExtraResources = IsGeneratedMode ? Resources.Select(item => new ExtraResourceMapping(item.SourcePath.Trim(), item.Destination.Trim())).ToArray() : []
            };
        }
        catch (Exception error) { Status = "失败：" + error.Message; ResultText = Status; return; }
        ResultText = "";
        LastInstallerPath = "";
        LastLogPath = "";
        _logLines.Clear();
        LogText = "";
        var sourceSnapshot = SourcePath;
        PackagingResult? result = null;
        var progress = new Progress<PackagingProgress>(item =>
        {
            Status = StageName(item.Stage);
            AppendLog(item.Message);
        });
        if (!await RunOwnedAsync("准备打包", async token =>
        {
            _profiles!.Save(options);
            _profiles.SaveToolsAndSource(options.Tools, sourceSnapshot);
            result = await new PackagingService().PackageAsync(options, progress, confirmation => ConfirmAsync(confirmation, token), token);
        })) return;
        if (result is null) return;
        LastInstallerPath = result.InstallerPath ?? "";
        LastLogPath = result.LogPath;
        Status = result.Succeeded ? "打包成功" : result.Cancelled ? "已取消" : "打包失败";
        ResultText = (result.Succeeded ? "安装包：" + result.InstallerPath : result.Error ?? Status)
            + (result.Succeeded && !string.IsNullOrEmpty(result.Error) ? "\n提示：" + result.Error : "")
            + (string.IsNullOrEmpty(result.LogPath) ? "" : "\n完整日志：" + result.LogPath)
            + (result.BackupPaths.Count == 0 ? "" : "\n保留的备份/恢复路径：\n" + string.Join("\n", result.BackupPaths));
        AppendLog(ResultText);
    }

    private async Task<bool> ConfirmAsync(PackagingConfirmation confirmation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var accepted = await _dispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            var window = Application.Current.MainWindow;
            if (window is MainWindow main && !await main.WaitForCloseDecisionAsync(token)) return false;
            token.ThrowIfCancellationRequested();
            if (window is null || !window.IsEnabled) return false;
            return MessageBox.Show(window, confirmation.Message + "\n\n路径：" + confirmation.Path,
                confirmation.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }).Task.Unwrap();
        token.ThrowIfCancellationRequested();
        return accepted;
    }

    private async Task<bool> RunOwnedAsync(string stage, Func<CancellationToken, Task> operation)
    {
        if (IsBusy || Application.Current.MainWindow is { IsEnabled: false }) return false;
        IsBusy = true;
        Status = stage;
        try
        {
            if (!BackgroundTaskHost.Shared.TryStart(BackgroundOperationKind.Packaging, operation, out var task))
            {
                Status = "另一个后台任务正在运行，请等待任务真正结束。";
                return false;
            }
            _ownedTask = task;
            await task;
            if (Status == stage) Status = "准备就绪";
            return true;
        }
        catch (OperationCanceledException) { Status = "已取消，后台进程已结束。"; return false; }
        catch (Exception error) { Status = "失败：" + error.Message; ResultText = Status; AppendLog(Status); return false; }
        finally { _ownedTask = null; IsBusy = false; }
    }

    [RelayCommand] private void Cancel()
    {
        if (IsBusy && _ownedTask is { IsCompleted: false } && ReferenceEquals(BackgroundTaskHost.Shared.CurrentTask, _ownedTask))
        {
            Status = "正在取消，等待进程退出和文件恢复…";
            try { BackgroundTaskHost.Shared.RequestCancellation(); }
            catch (Exception error) { Status = "取消请求失败：" + error.Message; ResultText = Status; }
        }
    }

    [RelayCommand] private void BrowseSourceFile() => Guard(() =>
    {
        var dialog = new OpenFileDialog { Title = "选择 C# 项目或解决方案", Filter = "项目与解决方案|*.csproj;*.sln|C# 项目|*.csproj|解决方案|*.sln" };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) SourcePath = dialog.FileName;
    });
    [RelayCommand] private void BrowseSourceFolder() => ChooseFolder("选择项目文件夹", path => SourcePath = path);
    [RelayCommand] private void BrowseOutputFolder() => ChooseFolder("选择安装包输出目录", path => OutputDirectory = path);
    [RelayCommand] private void BrowseScriptInputFolder() => ChooseFolder("选择与脚本 Source 规则匹配的构建输入目录", path => ScriptInputDirectory = path);
    [RelayCommand] private void BrowseScript() => Guard(() =>
    {
        var dialog = new OpenFileDialog { Title = "选择已有 Inno Setup 脚本", Filter = "Inno Setup 脚本|*.iss" };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        ExistingScriptPath = dialog.FileName;
        ScriptInputDirectory = SelectedCandidate is { IsNetFramework: true } project ? project.TargetDirectory
            : Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "Publish");
    });
    [RelayCommand] private void BrowseIcon() => Guard(() =>
    {
        var dialog = new OpenFileDialog { Title = "选择应用图标", Filter = "Windows 图标|*.ico" };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) IconPath = dialog.FileName;
    });
    [RelayCommand] private void BrowseTool(string? tool) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Title = "选择工具可执行文件", Filter = "可执行文件|*.exe" };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        switch (tool) { case "dotnet": DotNetPath = dialog.FileName; break; case "msbuild": MsBuildPath = dialog.FileName; break; case "inno": InnoCompilerPath = dialog.FileName; break; }
    });
    [RelayCommand] private void AddFiles() => Guard(() =>
    {
        var dialog = new OpenFileDialog { Title = "添加额外资源文件", Multiselect = true, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            foreach (var path in dialog.FileNames) Resources.Add(new() { SourcePath = path, Destination = Path.GetFileName(path) });
    });
    [RelayCommand] private void AddFolder() => ChooseFolder("添加额外资源目录", path => Resources.Add(new() { SourcePath = path, Destination = Path.GetFileName(path) }));
    [RelayCommand] private void RemoveResource() { if (SelectedResource is not null) Resources.Remove(SelectedResource); }
    [RelayCommand] private void OpenOutputDirectory() => Guard(() =>
    {
        if (File.Exists(LastInstallerPath)) Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(LastInstallerPath)!) { UseShellExecute = true });
    });
    [RelayCommand] private void OpenLog() => Guard(() =>
    {
        if (File.Exists(LastLogPath)) Process.Start(new ProcessStartInfo(LastLogPath) { UseShellExecute = true });
    });

    private void ChooseFolder(string title, Action<string> selected) => Guard(() =>
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) selected(dialog.FolderName);
    });
    private void Guard(Action action) { try { if (!IsBusy) action(); } catch (Exception error) { Status = "失败：" + error.Message; ResultText = Status; } }
    private BuildTools SnapshotTools() => new() { DotNetPath = DotNetPath.Trim(), MsBuildPath = MsBuildPath.Trim(), InnoCompilerPath = InnoCompilerPath.Trim() };
    private void ApplyTools(BuildTools tools) { DotNetPath = tools.DotNetPath ?? ""; MsBuildPath = tools.MsBuildPath ?? ""; InnoCompilerPath = tools.InnoCompilerPath ?? ""; }
    private static string NormalizePlatform(string platform) => platform.Replace(" ", "", StringComparison.Ordinal).Trim();
    private static string FirstLines(string text, int count) => string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(count));
    private void AppendLog(string message)
    {
        foreach (var line in message.Split('\n')) _logLines.Enqueue(line.TrimEnd('\r'));
        while (_logLines.Count > LogLineLimit) _logLines.Dequeue();
        LogText = string.Join(Environment.NewLine, _logLines);
    }
    private static string Describe(ProjectDescriptor project)
    {
        var text = new StringBuilder();
        text.AppendLine($"界面：{project.UiFramework}    框架：{project.TargetFramework} {project.FrameworkVersion}    格式：{(project.IsSdkStyle ? "SDK" : "传统项目")}");
        text.AppendLine($"构建配置：{project.Configuration}    项目平台：{project.Platform}    CPU：{project.PlatformTarget}    Prefer32Bit：{project.Prefer32Bit}");
        if (project.SolutionPath is not null) text.AppendLine($"解决方案映射：{project.SolutionConfiguration} → {project.Configuration}|{project.Platform}（从候选列表切换，不直接覆盖映射）");
        text.AppendLine("项目 RID 约束：" + (string.IsNullOrEmpty(project.DeclaredRuntimeIdentifier) ? string.Join("、", project.DeclaredRuntimeIdentifiers) : project.DeclaredRuntimeIdentifier));
        text.AppendLine("目标程序 EXE：" + project.MainExecutable);
        text.AppendLine("项目：" + project.ProjectPath);
        if (project.IsNetFramework) text.AppendLine("Framework 开发包：" + project.TargetingPackDirectory);
        text.AppendLine("识别依据：" + project.Evidence);
        if (!project.IsSupported) text.AppendLine("暂不支持/无法打包：" + project.UnsupportedReason);
        return text.ToString().TrimEnd();
    }
    private static string StageName(PackagingStage stage) => stage switch
    {
        PackagingStage.Preparing => "准备与检查", PackagingStage.Restoring => "还原依赖", PackagingStage.Building => "构建/发布",
        PackagingStage.Collecting => "收集文件", PackagingStage.Compiling => "编译安装包", PackagingStage.Committing => "校验并保存安装包",
        PackagingStage.Succeeded => "安装包已保存，等待任务收尾", PackagingStage.Failed => "任务失败，正在恢复", PackagingStage.Cancelled => "任务取消，正在恢复", _ => stage.ToString()
    };
}
