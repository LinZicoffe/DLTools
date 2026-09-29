namespace 烧录上位机程序.Models;

/// <summary>烧录连接参数（由 FlashWorker 从 UI 参数组装后传给 JLinkBackend）。</summary>
public class ProbeParams
{
    public string DeviceName { get; init; } = "";
    public string Interface { get; init; } = "SWD";
    public int SpeedKhz { get; init; } = 4000;
    public string FilePath { get; init; } = "";
    public string FileFormat { get; init; } = "hex";
    public int BinStartAddr { get; init; } = 0;
    public string EraseMode { get; init; } = "sector";
    public string PostAction { get; init; } = "reset_run";
    public string Serial { get; init; } = "";
    public string RemoteAddr { get; init; } = "";
    public bool EraseOnly { get; init; } = false;
}
