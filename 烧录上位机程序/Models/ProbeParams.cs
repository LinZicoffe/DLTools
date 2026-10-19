namespace 烧录上位机程序.Models;

/// <summary>
/// 探测器连接参数（FlashWorker 组装后传给 JLinkBackend.Connect；
/// 固件文件相关参数由 Program/Verify 的形参直接传递，不在此列）。
/// </summary>
public class ProbeParams
{
    public string DeviceName { get; init; } = "";
    public string Interface { get; init; } = "SWD";
    public int SpeedKhz { get; init; } = 4000;
    public string Serial { get; init; } = "";
    public string RemoteAddr { get; init; } = "";
}
