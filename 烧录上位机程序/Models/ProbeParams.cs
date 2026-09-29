namespace 烧录上位机程序.Models;

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
    public bool ExtraVerify { get; init; } = false;
    public string Serial { get; init; } = "";
    public string BurnerKind { get; init; } = "jlink";
    public string RemoteAddr { get; init; } = "";
    public bool EraseOnly { get; init; } = false;
}

public class FlashFileInfo
{
    public string Format { get; init; } = "";
    public int AddrStart { get; init; } = 0;
    public int AddrEnd { get; init; } = 0;
    public int TotalBytes { get; init; } = 0;
    public string Notes { get; init; } = "";
}

public class DeviceInfo
{
    public string JlinkFirmware { get; set; } = "";
    public string JlinkHardware { get; set; } = "";
    public string JlinkSerial { get; set; } = "";
    public string CoreName { get; set; } = "";
    public string TargetDevice { get; set; } = "";
    public string Interface { get; set; } = "";
    public int SpeedKhz { get; set; } = 4000;
}

public class RttConnectResult
{
    public string ActualSerial { get; set; } = "";
    public int NumUpChannels { get; set; } = 1;
    public Dictionary<string, object> DeviceInfo { get; set; } = new();
}
