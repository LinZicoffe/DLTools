using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static 烧录上位机程序.Services.Packaging.WindowsJobNative;

namespace 烧录上位机程序.Services.Packaging;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IExternalProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        Action<string>? log = null, CancellationToken cancellationToken = default);
}

/// <summary>Runs a directly executed program and all its descendants in one non-breakaway Windows job.</summary>
public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("打包进程需要 Windows 10 或更高版本。");
        ValidateText(fileName, nameof(fileName));
        ValidateText(workingDirectory, nameof(workingDirectory));
        ArgumentNullException.ThrowIfNull(arguments);
        var command = new StringBuilder(QuoteArgument(fileName));
        foreach (var argument in arguments)
        {
            ValidateText(argument, nameof(arguments), allowEmpty: true);
            command.Append(' ').Append(QuoteArgument(argument));
        }
        var executable = ResolveExecutable(fileName, workingDirectory);

        using var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw Error("无法创建打包进程 Job Object。");
        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose }
        };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            throw Error("无法启用打包进程退出保护。");

        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true
        };
        using var outputRead = CreateOutputPipe(ref security, out var outputWrite);
        using var outputWriteOwner = outputWrite;
        using var errorRead = CreateOutputPipe(ref security, out var errorWrite);
        using var errorWriteOwner = errorWrite;
        using var input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
        if (input.IsInvalid) throw Error("无法打开子进程标准输入 NUL。");

        using var process = Start(executable, command, workingDirectory, job, input, outputWrite, errorWrite);
        // The parent must close its pipe writers so EOF follows the last descendant exiting.
        outputWrite.Dispose();
        errorWrite.Dispose();
        try
        {
            return await ObserveAsync(job, process, outputRead, errorRead, log, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // This also protects failures while constructing stream readers, before their own observation loop.
            await TerminateAndDrainAsync(job).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ProcessResult> ObserveAsync(SafeFileHandle job, SafeFileHandle process,
        SafeFileHandle outputRead, SafeFileHandle errorRead, Action<string>? log, CancellationToken cancellationToken)
    {
        using var outputStream = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
        using var errorStream = new FileStream(errorRead, FileAccess.Read, 4096, isAsync: false);
        using var outputReader = new StreamReader(outputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var errorReader = new StreamReader(errorStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logLock = new object();
        var outputTask = PumpAsync(outputReader);
        var errorTask = PumpAsync(errorReader);
        try
        {
            while (GetActiveProcessCount(job) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failure.Task.IsCompleted)
                    ExceptionDispatchInfo.Capture(await failure.Task.ConfigureAwait(false)).Throw();
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            var streams = await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (failure.Task.IsCompleted)
                ExceptionDispatchInfo.Capture(await failure.Task.ConfigureAwait(false)).Throw();
            if (!GetExitCodeProcess(process, out var exitCode)) throw Error("无法读取打包进程退出码。");
            return new ProcessResult(unchecked((int)exitCode), streams[0], streams[1]);
        }
        catch
        {
            // File transactions may only restore their directories after this await returns.
            await TerminateAndDrainAsync(job).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        async Task<string> PumpAsync(StreamReader reader)
        {
            var collected = new StringBuilder();
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    collected.AppendLine(line);
                    if (log is null) continue;
                    lock (logLock)
                    {
                        if (failure.Task.IsCompleted) continue;
                        try { log(line); }
                        catch (Exception exception) { failure.TrySetResult(exception); }
                    }
                }
            }
            catch (Exception exception) { failure.TrySetResult(exception); }
            return collected.ToString();
        }
    }

    private static SafeFileHandle Start(string executable, StringBuilder command, string directory,
        SafeFileHandle job, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        UIntPtr size = UIntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        var attributes = Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
        var jobArray = Marshal.AllocHGlobal(IntPtr.Size);
        var handleArray = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                throw Error("无法初始化打包进程启动属性。");
            initialized = true;
            Marshal.WriteIntPtr(jobArray, job.DangerousGetHandle());
            Marshal.WriteIntPtr(handleArray, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handleArray, 2 * IntPtr.Size, error.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, JobListAttribute, jobArray,
                    new UIntPtr((uint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, handleArray,
                    new UIntPtr((uint)(3 * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero))
                throw Error("无法设置打包进程 Job/管道继承属性。");
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = StartfUseStdHandles,
                    StandardInput = input.DangerousGetHandle(), StandardOutput = output.DangerousGetHandle(),
                    StandardError = error.DangerousGetHandle()
                },
                AttributeList = attributes
            };
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow, IntPtr.Zero, directory, ref startup, out var info))
                throw Error($"无法启动打包程序：{executable}");
            using var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            return new SafeFileHandle(info.Process, ownsHandle: true);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(jobArray);
            Marshal.FreeHGlobal(handleArray);
        }
    }

    private static SafeFileHandle CreateOutputPipe(ref SecurityAttributes security, out SafeFileHandle write)
    {
        if (!CreatePipe(out var read, out write, ref security, 0))
        {
            var creationError = Error("无法创建打包日志管道。");
            read.Dispose();
            write.Dispose();
            throw creationError;
        }
        if (SetHandleInformation(read, HandleFlagInherit, 0)) return read;
        var exception = Error("无法设置打包日志管道继承属性。");
        read.Dispose();
        write.Dispose();
        throw exception;
    }

    private static uint GetActiveProcessCount(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var accounting,
                (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
            throw Error("无法查询打包进程组状态。");
        return accounting.ActiveProcesses;
    }

    private static async Task TerminateAndDrainAsync(SafeFileHandle job)
    {
        if (!TerminateJobObject(job, 1) && GetActiveProcessCount(job) != 0)
            throw Error("无法终止打包进程组。");
        while (GetActiveProcessCount(job) != 0)
            await Task.Delay(25).ConfigureAwait(false);
    }

    private static void ValidateText(string text, string name, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(text, name);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0'))
            throw new ArgumentException("进程参数不能为空且不能含 NUL。", name);
    }

    // CommandLineToArgvW / C runtime quoting: double backslashes only before quotes and the closing quote.
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append(character);
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string ResolveExecutable(string name, string workingDirectory)
    {
        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(name, workingDirectory);
        var names = Path.HasExtension(name) ? new[] { name } : new[] { name, name + ".exe" };
        var directories = new[] { workingDirectory }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var candidate in names)
            {
                var path = Path.Combine(directory.Trim('"'), candidate);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        }
        throw new Win32Exception(2, $"找不到打包程序：{name}");
    }
}
