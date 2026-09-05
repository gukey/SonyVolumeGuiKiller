namespace SonyVolumeGuiKillerPcManager.Models;

public sealed class DeviceStatus
{
    public string TvIp { get; set; } = string.Empty;
    public int DebugPort { get; set; } = 5555;
    public bool AdbConnected { get; set; }
    public bool AdbAuthorized { get; set; }
    public string DeviceState { get; set; } = "未连接";
    public bool PackageInstalled { get; set; }
    public string PackageVersionName { get; set; } = "-";
    public string PackageVersionCode { get; set; } = "-";
    public string PackagePath { get; set; } = "-";
    public bool RunAsAvailable { get; set; }
    public bool AdbKeyInstalled { get; set; }
    public bool AccessibilityEnabled { get; set; }
    public bool AccessibilityServiceEnabled { get; set; }
    public string CurrentAccessibilityServices { get; set; } = "-";
    public bool TcpPort5555Enabled { get; set; }
    public bool VolumeControllerNull { get; set; }
    public string VolumeControllerRawLine { get; set; } = "-";
    public string LastLogLines { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
}
