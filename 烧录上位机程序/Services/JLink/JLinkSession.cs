using System;
using System.Threading;
using 烧录上位机程序.Models;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// 默认烧录会话：按次创建 DllLoader 与 JLinkBackend，关闭时释放两者。
/// JLinkBackend.Dispose 只关闭 DLL 会话、不释放 LoadLibrary 句柄，
/// 因此 DLL 句柄必须由本类在 Close 中随会话一并释放。
/// </summary>
public sealed class JLinkSession : IJLinkSession
{
    private readonly DllLoader _loader;
    private readonly JLinkBackend _backend;

    public JLinkSession(Action<string, string> log)
    {
        // 单次加载失败只影响本次烧录；驱动装好后可重试（与旧 RunFlash 行为一致）
        _loader = new DllLoader();
        log("info", $"JLink_x64.dll 版本: {_loader.GetDLLVersion()}");
        _backend = new JLinkBackend(_loader, log);
    }

    public void Connect(ProbeParams p) => _backend.Connect(p);

    public void Erase(string mode) => _backend.Erase(mode);

    public void Program(string filePath, string fileFormat, int binStartAddr, Action<int, int> onProgress, CancellationToken ct)
        => _backend.Program(filePath, fileFormat, binStartAddr, onProgress, ct);

    public void Verify(string filePath, string fileFormat, int binStartAddr, CancellationToken ct)
        => _backend.Verify(filePath, fileFormat, binStartAddr, ct);

    public void Reset(bool halt, bool run) => _backend.Reset(halt, run);

    public void Close()
    {
        // JLinkBackend.Close 内部已吞异常；仍用 finally 保证 DLL 句柄必然释放
        try { _backend.Close(); }
        finally { _loader.Dispose(); }
    }

    public void Dispose() => Close();
}
