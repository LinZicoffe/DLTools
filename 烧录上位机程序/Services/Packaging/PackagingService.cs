using System.IO;
using System.Text;
using 烧录上位机程序.Models.Packaging;

namespace 烧录上位机程序.Services.Packaging;

/// <summary>Builds, collects and compiles an installer. It owns all mutations until the final atomic save.</summary>
public sealed class PackagingService(IExternalProcessRunner? runner = null, string? stateDirectory = null)
{
    private readonly IExternalProcessRunner _runner = runner ?? new ExternalProcessRunner();
    private readonly string _stateDirectory = Path.GetFullPath(stateDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JLinkRTTViewer", "packaging"));

    public static IReadOnlyList<string> AllowedRuntimeIdentifiers(ProjectDescriptor project)
    {
        if (project.IsNetFramework) return [];
        IEnumerable<string> values = new[] { "win-x64", "win-x86" };
        if (!string.IsNullOrWhiteSpace(project.DeclaredRuntimeIdentifier))
            values = values.Where(r => r.Equals(project.DeclaredRuntimeIdentifier, StringComparison.OrdinalIgnoreCase));
        else if (project.DeclaredRuntimeIdentifiers.Count != 0)
            values = values.Where(r => project.DeclaredRuntimeIdentifiers.Contains(r, StringComparer.OrdinalIgnoreCase));
        if (!project.PlatformTarget.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase))
            values = values.Where(r => r.Equals("win-" + project.PlatformTarget, StringComparison.OrdinalIgnoreCase));
        return values.ToArray();
    }

    public async Task<PackagingResult> PackageAsync(PackagingOptions options, IProgress<PackagingProgress>? progress,
        Func<PackagingConfirmation, Task<bool>> confirm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(confirm);
        var transactions = new List<DirectoryTransaction>();
        var logPath = "";
        RunLog? log = null;
        string? installer = null;
        var committed = false;
        var warnings = new List<string>();
        void Report(PackagingStage stage, string message)
        {
            log?.Write(stage + ": " + message);
            // Observers are UI clients; their failures must not strand file transactions.
            try { progress?.Report(new PackagingProgress(stage, message)); }
            catch (Exception error) { log?.Write("进度通知失败：" + error.Message); }
        }
        async Task<bool> ConfirmReplacement(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nonempty = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
            if (nonempty)
            {
                if (!await confirm(new PackagingConfirmation("备份并替换构建目录",
                        "此目录非空。继续将先把原目录移动到同卷备份，再生成本次构建文件；失败或取消时恢复原目录。", path)).ConfigureAwait(false))
                    throw new OperationCanceledException("用户取消替换构建目录。", cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return nonempty;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-evaluation rejects stale architecture/framework/output assumptions before any source-adjacent mutation.
            var project = await new ProjectInspectionService(_runner).EvaluateAsync(options.Project, options.Tools, cancellationToken)
                .ConfigureAwait(false);
            if (!project.IsSupported) throw new InvalidOperationException(project.UnsupportedReason);
            options = options with { Project = project,
                RuntimeIdentifier = options.RuntimeIdentifier.ToLowerInvariant(),
                InstallerFileName = PackagingFileSystem.NormalizeInstallerFileName(options.InstallerFileName) };
            if (!project.IsNetFramework && !AllowedRuntimeIdentifiers(project).Contains(options.RuntimeIdentifier, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"发布目标 {options.RuntimeIdentifier} 与项目 CPU/RID 约束不符。可选：{string.Join("、", AllowedRuntimeIdentifiers(project))}");
            if (options.Mode is not InstallerMode.Generated and not InstallerMode.ExistingScript)
                throw new ArgumentException("未知安装包模式。");
            if (options.Mode == InstallerMode.Generated)
            {
                if (options.AppId == Guid.Empty) throw new ArgumentException("请指定稳定的安装标识 AppId。");
                if (string.IsNullOrWhiteSpace(options.AppName) || options.AppName.Any(char.IsControl)
                    || string.IsNullOrWhiteSpace(options.AppVersion) || options.AppVersion.Any(char.IsControl))
                    throw new ArgumentException("应用名称和版本不能为空或包含控制字符。");
                if (!string.IsNullOrWhiteSpace(options.IconPath))
                {
                    PackagingFileSystem.EnsureNoLinks(options.IconPath);
                    if (!File.Exists(options.IconPath)) throw new FileNotFoundException("未找到安装包图标。", options.IconPath);
                }
            }
            var output = PackagingFileSystem.FullPath(options.OutputDirectory);
            var state = PackagingFileSystem.FullPath(_stateDirectory);
            PackagingFileSystem.EnsureNoLinks(output);
            PackagingFileSystem.EnsureNoLinks(state);
            PackagingFileSystem.EnsureDisjoint(output, state);
            PackagingFileSystem.EnsureDisjoint(state, Path.GetDirectoryName(project.ProjectPath)!);
            var run = Path.Combine(state, "runs", Guid.NewGuid().ToString("N"));
            var publish = Path.Combine(run, "publish");
            var staging = Path.Combine(run, "staging");
            var compiled = Path.Combine(run, "compiled");
            var registry = new RecoveryRegistry(Path.Combine(state, "recovery"));
            registry.EnsureUnblocked(output);
            registry.EnsureUnblocked(Path.GetDirectoryName(project.ProjectPath)!);
            if (!string.IsNullOrWhiteSpace(project.TargetDirectory)) registry.EnsureUnblocked(project.TargetDirectory);
            var mutable = new List<string>();
            if (project.IsNetFramework) mutable.Add(PackagingFileSystem.ValidateMutableDirectory(project.TargetDirectory, project));
            var scriptInput = "";
            if (options.Mode == InstallerMode.ExistingScript)
            {
                var script = PackagingFileSystem.FullPath(options.ExistingScriptPath);
                PackagingFileSystem.EnsureNoLinks(script);
                if (!Path.GetExtension(script).Equals(".iss", StringComparison.OrdinalIgnoreCase) || !File.Exists(script))
                    throw new FileNotFoundException("请选择已存在的 Inno Setup .iss 脚本。", script);
                scriptInput = PackagingFileSystem.ValidateMutableDirectory(options.ScriptInputDirectory, project);
                if (PackagingFileSystem.IsWithin(script, scriptInput))
                    throw new IOException("构建输入目录不能包含所选安装脚本。");
                try
                {
                    PackagingFileSystem.EnsureDisjoint(scriptInput, Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, "obj"));
                    if (!project.IsNetFramework && !string.IsNullOrWhiteSpace(project.TargetDirectory))
                        PackagingFileSystem.EnsureDisjoint(scriptInput, project.TargetDirectory);
                }
                catch (IOException error)
                {
                    throw new IOException("脚本输入不能与项目中间输出目录重叠，现代 .NET 还须使用独立于正常 bin 输出的输入目录。请选择例如脚本旁的 Publish 目录。", error);
                }
                if (!mutable.Contains(scriptInput, StringComparer.OrdinalIgnoreCase)) mutable.Add(scriptInput);
                options = options with { ExistingScriptPath = script, ScriptInputDirectory = scriptInput };
            }
            var backupRoots = mutable.ToDictionary(path => path, path => ChooseBackupRoot(path, project), StringComparer.OrdinalIgnoreCase);
            PackagingFileSystem.EnsureDisjoint([.. mutable, output, state]);
            foreach (var target in mutable)
            {
                registry.EnsureUnblocked(target);
                foreach (var protectedPath in mutable.Concat([output, state]))
                    PackagingFileSystem.EnsureDisjoint(backupRoots[target], protectedPath);
            }
            // A shared backup container is intentional; each transaction exclusively owns a GUID child.
            foreach (var backupRoot in backupRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                PackagingFileSystem.ValidateMutableDirectory(backupRoot, project);

            var context = SolutionBuildContext.Arguments(project);
            Directory.CreateDirectory(run);
            logPath = Path.Combine(run, "packaging.log");
            log = new RunLog(logPath);
            Report(PackagingStage.Preparing, "项目：" + project.DisplayName);
            Report(PackagingStage.Preparing, project.Evidence);
            Report(PackagingStage.Preparing, "任务目录：" + run);
            var diagnostics = await new BuildToolLocator(_runner).DiagnoseAsync(options.Tools, cancellationToken).ConfigureAwait(false);
            var required = project.IsNetFramework ? "Visual Studio MSBuild" : ".NET SDK";
            foreach (var diagnostic in diagnostics.Where(d => d.Name == required || d.Name == "Inno Setup"
                || (options.Mode == InstallerMode.Generated && d.Name == "Inno 简体中文语言文件")))
            {
                Report(PackagingStage.Preparing, diagnostic.Name + "：" + diagnostic.Path + (diagnostic.Available ? " [可用]" : " [不可用]"));
                if (!diagnostic.Available) throw new InvalidOperationException(diagnostic.Name + "：" + diagnostic.Message + "\n工具路径：" + diagnostic.Path);
            }
            log.Check();
            // All replacement decisions happen before any directory has moved.
            var confirmed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in mutable) confirmed[target] = await ConfirmReplacement(target).ConfigureAwait(false);
            foreach (var target in mutable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transaction = DirectoryTransaction.Begin(target, project, backupRoots[target], registry, replacementConfirmed: confirmed[target]);
                transactions.Add(transaction);
                Report(PackagingStage.Preparing, "已准备独占目录：" + target
                    + (transaction.BackupDirectory is null ? "" : "；原目录备份：" + transaction.BackupDirectory));
            }
            log.Check();

            async Task Execute(string tool, IReadOnlyList<string> arguments, string workingDirectory, PackagingStage stage)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(stage, "执行工具：" + tool);
                var result = await _runner.RunAsync(tool, arguments, workingDirectory,
                    line => Report(stage, line), cancellationToken).ConfigureAwait(false);
                Report(stage, "工具已退出，退出码：" + result.ExitCode);
                log.Check();
                if (result.ExitCode != 0) throw new InvalidOperationException($"{stage} 阶段失败（退出码 {result.ExitCode}）。请查看日志：{logPath}");
            }
            var projectDirectory = Path.GetDirectoryName(project.ProjectPath)!;
            if (project.IsNetFramework)
            {
                var common = new List<string> { project.ProjectPath, "-nologo", "-nr:false", "-p:UseSharedCompilation=false" };
                common.AddRange(context);
                if (project.IsSdkStyle) common.Add(SolutionBuildContext.Property("TargetFramework", project.TargetFramework));
                await Execute(options.Tools.MsBuildPath, [.. common, "-t:Restore", "-p:RestorePackagesConfig=true"],
                    projectDirectory, PackagingStage.Restoring).ConfigureAwait(false);
                await Execute(options.Tools.MsBuildPath, [.. common, "-t:Rebuild"], projectDirectory, PackagingStage.Building).ConfigureAwait(false);
                Report(PackagingStage.Collecting, "收集完整 Framework 构建输出：" + project.TargetDirectory);
                PackagingFileSystem.CopyTree(project.TargetDirectory, staging);
            }
            else
            {
                var common = new List<string> { project.ProjectPath, "--disable-build-servers", "-nr:false", "-p:UseSharedCompilation=false" };
                common.AddRange(context);
                common.Add(SolutionBuildContext.Property("TargetFramework", project.TargetFramework));
                common.Add(SolutionBuildContext.Property("RuntimeIdentifier", options.RuntimeIdentifier));
                common.Add(SolutionBuildContext.Property("SelfContained", options.SelfContained ? "true" : "false"));
                common.Add("-p:UseAppHost=true");
                await Execute(options.Tools.DotNetPath, ["restore", .. common], projectDirectory, PackagingStage.Restoring).ConfigureAwait(false);
                await Execute(options.Tools.DotNetPath, ["publish", .. common, "--no-restore", "--output", publish],
                    projectDirectory, PackagingStage.Building).ConfigureAwait(false);
                Report(PackagingStage.Collecting, "收集独占发布目录：" + publish);
                PackagingFileSystem.CopyTree(publish, staging);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var main = PackagingFileSystem.NormalizeInstallerFileName(project.MainExecutable);
            if (!main.Equals(project.MainExecutable, StringComparison.OrdinalIgnoreCase)) throw new IOException("项目入口不是有效的 EXE 文件名。");
            if (!File.Exists(Path.Combine(staging, main))) throw new FileNotFoundException("本次构建缺少入口 EXE，已停止打包。", Path.Combine(staging, main));
            string scriptPath;
            string compilerWorkingDirectory;
            string? scriptFingerprint = null;
            Directory.CreateDirectory(compiled);
            if (options.Mode == InstallerMode.Generated)
            {
                PackagingFileSystem.AddResources(staging, options.ExtraResources);
                if (!string.IsNullOrWhiteSpace(options.IconPath))
                {
                    PackagingFileSystem.AddResources(staging, [new ExtraResourceMapping(options.IconPath, InstallerTemplateBuilder.InstalledIconName)]);
                }
                scriptPath = Path.Combine(run, "generated.iss");
                File.WriteAllText(scriptPath, new InstallerTemplateBuilder().Build(options, staging, compiled), new UTF8Encoding(true));
                compilerWorkingDirectory = run;
            }
            else
            {
                Report(PackagingStage.Collecting, "准备原脚本构建输入：" + scriptInput + "；请确保该路径与脚本 Source/宏约定一致。");
                // Identical Framework output/input already contains precisely this build; do not copy a second time.
                if (!project.IsNetFramework || !scriptInput.Equals(PackagingFileSystem.FullPath(project.TargetDirectory), StringComparison.OrdinalIgnoreCase))
                    PackagingFileSystem.CopyTree(staging, scriptInput);
                scriptPath = options.ExistingScriptPath;
                compilerWorkingDirectory = Path.GetDirectoryName(scriptPath)!;
                Report(PackagingStage.Compiling, "检查有效脚本是否仅生成独立单 EXE；检查内容不会写入日志。");
                scriptFingerprint = await new ExistingScriptValidator(_runner).ValidateAsync(scriptPath,
                    options.Tools.InnoCompilerPath, run, cancellationToken).ConfigureAwait(false);
            }
            log.Check();
            if (scriptFingerprint is not null) ExistingScriptValidator.EnsureUnchanged(scriptPath, scriptFingerprint);
            await Execute(options.Tools.InnoCompilerPath,
                ["/O" + compiled, "/F" + options.InstallerFileName[..^4], scriptPath],
                compilerWorkingDirectory, PackagingStage.Compiling).ConfigureAwait(false);
            if (scriptFingerprint is not null) ExistingScriptValidator.EnsureUnchanged(scriptPath, scriptFingerprint);
            cancellationToken.ThrowIfCancellationRequested();
            PackagingFileSystem.CheckTree(compiled);
            var compilerFiles = Directory.GetFiles(compiled, "*", SearchOption.AllDirectories);
            var expected = Path.Combine(compiled, options.InstallerFileName);
            if (compilerFiles.Length != 1 || !compilerFiles[0].Equals(expected, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(expected) || new FileInfo(expected).Length == 0)
                throw new IOException("首版只支持单 EXE 安装包。编译输出缺少指定安装包，或含有分卷/其他必需附属产物；已停止保存。输出保留于：" + compiled);
            Report(PackagingStage.Committing, "校验并原子保存安装包：" + Path.Combine(output, options.InstallerFileName));
            log.Check();
            installer = await AtomicInstallerWriter.CommitAsync(expected, output, options.InstallerFileName, confirm, cancellationToken)
                .ConfigureAwait(false);
            // This is the commit point. No subsequent observer/log/journal cleanup error may restore old build inputs.
            committed = true;
            foreach (var transaction in transactions)
                if (transaction.Commit() is { } warning) warnings.Add(warning);
            foreach (var warning in warnings) Report(PackagingStage.Succeeded, warning);
            Report(PackagingStage.Succeeded, "安装包已生成：" + installer);
            if (log.Error is { } logError) warnings.Add("安装包已保存，但日志写入失败：" + logError.Message);
            return new PackagingResult(true, false, installer, warnings.Count == 0 ? null : string.Join("\n", warnings), logPath,
                transactions.SelectMany(t => t.PreservedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception error)
        {
            if (committed)
            {
                Report(PackagingStage.Succeeded, "安装包已保存，后续清理出现异常：" + error.Message);
                return new PackagingResult(true, false, installer, error.Message, logPath,
                    transactions.SelectMany(t => t.PreservedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            var recoveryErrors = new List<string>();
            foreach (var transaction in transactions.AsEnumerable().Reverse())
            {
                try { transaction.Rollback(); }
                catch (Exception recovery) { recoveryErrors.Add(recovery.Message); }
            }
            var cancelled = error is OperationCanceledException && recoveryErrors.Count == 0;
            var message = error.Message + (recoveryErrors.Count == 0 ? "" : "\n" + string.Join("\n", recoveryErrors));
            if (log?.Error is { } logError) message += "\n日志写入异常：" + logError.Message;
            Report(cancelled ? PackagingStage.Cancelled : PackagingStage.Failed, message);
            return new PackagingResult(false, cancelled, null, message, logPath,
                transactions.SelectMany(t => t.PreservedPaths).Where(p => Directory.Exists(p) || File.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        finally { log?.Dispose(); }
    }

    private static string ChooseBackupRoot(string target, ProjectDescriptor project)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project.ProjectPath))!;
        var projectParent = Path.GetDirectoryName(projectDirectory);
        string parent;
        if (projectParent is not null && string.Equals(Path.GetPathRoot(target), Path.GetPathRoot(projectParent), StringComparison.OrdinalIgnoreCase))
            parent = projectParent;
        else parent = Path.GetDirectoryName(target) ?? throw new IOException("无法为构建目录选定同卷备份位置。");
        return PackagingFileSystem.ValidateMutableDirectory(Path.Combine(parent, ".dltools-backups"), project);
    }

    private sealed class RunLog : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer;
        internal Exception? Error { get; private set; }
        internal RunLog(string path)
        {
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))
                { AutoFlush = true };
        }
        internal void Write(string line)
        {
            lock (_gate)
            {
                if (Error is not null) return;
                try { _writer.WriteLine(DateTimeOffset.Now.ToString("O") + " " + line); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { Error = error; }
            }
        }
        internal void Check()
        {
            lock (_gate)
            {
                if (Error is not null) throw new IOException("无法继续写入打包日志。", Error);
                try { _writer.Flush(); ((FileStream)_writer.BaseStream).Flush(flushToDisk: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
                { Error = error; throw new IOException("无法持久化打包日志。", error); }
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                try { _writer.Dispose(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { Error ??= error; }
            }
        }
    }
}
