using System;
using System.Threading;
using 烧录上位机程序.Models;

namespace 烧录上位机程序.Services.JLink;

/// <summary>
/// 一次烧录会话的抽象：connect → erase → program → verify → reset → close 全生命周期。
/// 默认实现 <see cref="JLinkSession"/> 组合 DllLoader 与 JLinkBackend；
/// 单元测试以内存假件替换本接口，即可离线验证 FlashWorker 的编排逻辑而不触达原生 DLL。
/// </summary>
public interface IJLinkSession : IDisposable
{
    void Connect(ProbeParams p);
    void Erase(string mode);
    void Program(string filePath, string fileFormat, int binStartAddr, Action<int, int> onProgress, CancellationToken ct);
    void Verify(string filePath, string fileFormat, int binStartAddr, CancellationToken ct);
    void Reset(bool halt, bool run);
    void Close();
}
