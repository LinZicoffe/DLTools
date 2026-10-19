using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace 烧录上位机程序.Services.Packaging;

/// <summary>Checks effective ISPP output without recording script text or authorization contents in logs.</summary>
public sealed class ExistingScriptValidator(IExternalProcessRunner? runner = null)
{
    private readonly IExternalProcessRunner _runner = runner ?? new ExternalProcessRunner();

    public async Task<string> ValidateAsync(string scriptPath, string compilerPath, string runDirectory,
        CancellationToken cancellationToken = default)
    {
        scriptPath = PackagingFileSystem.FullPath(scriptPath);
        runDirectory = PackagingFileSystem.FullPath(runDirectory);
        PackagingFileSystem.EnsureNoLinks(scriptPath);
        PackagingFileSystem.EnsureNoLinks(runDirectory);
        var original = await File.ReadAllBytesAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(original));
        var token = Guid.NewGuid().ToString("N");
        // A copied root in the same directory retains SourcePath and the root's empty __PATHFILENAME__.
        // Including the original in a wrapper would change those values inside the original script.
        var temporaryRoot = Path.Combine(Path.GetDirectoryName(scriptPath)!, ".dltools-preflight-" + token + ".iss");
        var expanded = Path.Combine(runDirectory, ".preprocessed-" + token + ".iss");
        var preflightOutput = Path.Combine(runDirectory, "script-preflight-" + token);
        var rootOwned = false;
        var expandedOwned = false;
        Exception? operationError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(preflightOutput);
            // Reserve our output exclusively before SaveToFile overwrites this invocation's empty file.
            using (new FileStream(expanded, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) expandedOwned = true;
            var footer = "\r\n\r\n#pragma parseroption -p+\r\n#expr SaveToFile(\""
                + expanded.Replace("\"", "\"\"", StringComparison.Ordinal)
                + "\")\r\n#pragma option -c-\r\n";
            using (var rootFile = new FileStream(temporaryRoot, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                rootOwned = true;
                var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Decode(original) + footer)).ToArray();
                await rootFile.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                rootFile.Flush(flushToDisk: true);
            }
            // Suppress all preprocessor stdout/stderr from application logs: it may contain expanded secrets.
            var result = await _runner.RunAsync(compilerPath,
                ["/O" + preflightOutput, "/Fpreflight", temporaryRoot], Path.GetDirectoryName(scriptPath)!,
                log: null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // ISCC 6 returns 2 (Compile aborted) after a successful ISPP-only -c- pass.
            if (result.ExitCode is not 0 and not 2 || !File.Exists(expanded) || new FileInfo(expanded).Length == 0
                || Directory.EnumerateFileSystemEntries(preflightOutput).Any())
                throw new NotSupportedException("无法通过 Inno Setup ISPP 安全检查有效脚本。首版需要支持 SaveToFile 的 ISPP；不支持 builtin 预处理器、提前终止预处理或无法导出有效内容的脚本。预处理诊断未写入日志，以免包含授权信息。");
            PackagingFileSystem.EnsureNoLinks(expanded);
            CheckExpandedScript(Decode(await File.ReadAllBytesAsync(expanded, cancellationToken).ConfigureAwait(false)));
            EnsureUnchanged(scriptPath, fingerprint);
            return fingerprint;
        }
        catch (Exception error)
        {
            operationError = error;
            throw;
        }
        finally
        {
            var cleanupErrors = new List<string>();
            foreach (var path in (rootOwned ? new[] { temporaryRoot } : Array.Empty<string>())
                         .Concat(expandedOwned ? new[] { expanded } : Array.Empty<string>()))
            {
                try { PackagingFileSystem.EnsureNoLinks(path); File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { cleanupErrors.Add(path + "：" + error.Message); }
            }
            if (cleanupErrors.Count != 0)
                throw new IOException("无法清理本次脚本检查临时文件，请人工删除这些位置：\n" + string.Join("\n", cleanupErrors), operationError);
        }
    }

    public static void EnsureUnchanged(string scriptPath, string fingerprint)
    {
        PackagingFileSystem.EnsureNoLinks(scriptPath);
        using var file = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(fingerprint, StringComparison.Ordinal))
            throw new IOException("安装脚本在检查后发生变化，已停止打包，请重新开始。");
    }

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0).GetString(bytes);
        }
    }

    private static void CheckExpandedScript(string text)
    {
        var section = "";
        var sawSetup = false;
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                sawSetup |= section.Equals("Setup", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (HasSourceConstant(line))
                throw new NotSupportedException("已有脚本包含运行时 {src} 引用，可能需要安装包旁的附属文件。首版仅支持独立的单 EXE 安装包，请将所需文件编译进安装包后重试。");
            if (!section.Equals("Files", StringComparison.OrdinalIgnoreCase)) continue;
            var parameters = ParseParameters(line);
            if (!parameters.ContainsKey("Source"))
                throw new NotSupportedException("无法完整识别预处理后的 [Files] 条目。首版无法确认此脚本只需要单 EXE，已停止打包。");
            if (parameters.TryGetValue("Flags", out var flags)
                && flags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Any(f => f.Equals("external", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("download", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("extractarchive", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("已有脚本使用 external、download 或 extractarchive，安装时需要包外文件或下载。首版仅支持独立的单 EXE 安装包，已停止打包。");
        }
        if (!sawSetup) throw new NotSupportedException("预处理结果中没有有效 [Setup] 段，无法确认脚本的单 EXE 安装行为。");
    }

    private static bool HasSourceConstant(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            if (i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
            if (text.AsSpan(i).StartsWith("{src}", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static Dictionary<string, string> ParseParameters(string line)
    {
        var fields = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && line[i] == ';') { fields.Add(line[start..i]); start = i + 1; }
        }
        if (quoted) throw new NotSupportedException("无法识别 [Files] 条目的引号结构，已停止打包。");
        fields.Add(line[start..]);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var match = Regex.Match(field, @"^\s*(?<key>[A-Za-z][A-Za-z0-9]*)\s*:\s*(?<value>.*?)\s*$");
            if (!match.Success) throw new NotSupportedException("无法识别 [Files] 条目的参数结构，已停止打包。");
            var value = match.Groups["value"].Value;
            if (value.StartsWith('"') && value.EndsWith('"')) value = value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            if (!values.TryAdd(match.Groups["key"].Value, value)) throw new NotSupportedException("[Files] 条目包含重复参数，无法确认单 EXE 行为。");
        }
        return values;
    }
}
