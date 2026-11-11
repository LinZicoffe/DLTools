using System;
using System.Collections.Generic;
using System.Threading;
using 烧录上位机程序.Models;
using 烧录上位机程序.Services.JLink;

namespace DLTools.Tests;

/// <summary>
/// FlashWorker / FlashViewModel 测试共用的内存假会话：不触达 JLink_x64.dll。
/// 原生 DLL 路径由真机验收覆盖（docs 硬件检查记录）。
/// </summary>
public sealed class FakeJLinkSession : IJLinkSession
{
    public List<string> Calls { get; } = [];
    public Action<ProbeParams>? ConnectHook;
    public Action<CancellationToken>? ProgramHook;

    public void Connect(ProbeParams p)
    {
        Calls.Add($"connect:{p.DeviceName}:{p.Interface}");
        ConnectHook?.Invoke(p);
    }

    public void Erase(string mode) => Calls.Add($"erase:{mode}");

    public void Program(string filePath, string fileFormat, int binStartAddr, Action<int, int> onProgress, CancellationToken ct)
    {
        Calls.Add($"program:{fileFormat}");
        ProgramHook?.Invoke(ct);
        onProgress?.Invoke(100, 100);
    }

    public void Verify(string filePath, string fileFormat, int binStartAddr, CancellationToken ct) => Calls.Add("verify");

    public void Reset(bool halt, bool run) => Calls.Add($"reset:{halt}:{run}");

    public void Close() => Calls.Add("close");

    public void Dispose() => Close();
}
