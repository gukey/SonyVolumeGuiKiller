using SonyVolumeGuiKillerPcManager.Models;

namespace SonyVolumeGuiKillerPcManager.Services;

public static class AdbErrorTranslator
{
    public static string Explain(AdbResult result, string fallback)
    {
        var output = result.CombinedOutput;

        if (result.TimedOut)
        {
            return "ADB 命令执行超时，请检查电视网络连接后重试。";
        }

        if (output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return
                "❌ 未弹出设备授权窗口或电视尚未确认 RSA 授权。" + Environment.NewLine +
                "建议操作：" + Environment.NewLine +
                "1. 关闭电视网络调试" + Environment.NewLine +
                "2. 重新开启网络调试" + Environment.NewLine +
                "3. 再次 adb connect" + Environment.NewLine +
                "4. 如仍无效，重启电视" + Environment.NewLine +
                "如果电视已弹窗，请勾选“始终允许此计算机调试”后确认。";
        }

        if (output.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("cannot connect", StringComparison.OrdinalIgnoreCase))
        {
            return "无法连接电视 5555，请确认电视 IP 正确、电视与电脑在同一局域网、网络 ADB 已开启。";
        }

        if (output.Contains("offline", StringComparison.OrdinalIgnoreCase))
        {
            return "ADB 设备离线，请重新开启电视网络 ADB，或断开后重新连接。";
        }

        if (output.Contains("run-as", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("not debuggable", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("unknown package", StringComparison.OrdinalIgnoreCase))
        {
            return "无法进入 APK 私有目录。请确认 APK 包名为 com.codex.sonyvolumegui，且当前 APK 支持 run-as 写入 key。";
        }

        return string.IsNullOrWhiteSpace(output) ? fallback : $"{fallback}{Environment.NewLine}{output}";
    }

    public static string ExplainInstallFailure(AdbResult result, string fallback)
    {
        var output = result.CombinedOutput;

        if (output.Contains("INSTALL_FAILED_VERSION_DOWNGRADE", StringComparison.OrdinalIgnoreCase))
        {
            return "版本过低，不允许降级安装。";
        }

        if (output.Contains("INSTALL_FAILED_INVALID_APK", StringComparison.OrdinalIgnoreCase))
        {
            return "APK 损坏或不兼容。";
        }

        if (output.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE", StringComparison.OrdinalIgnoreCase))
        {
            return "电视存储空间不足。";
        }

        return Explain(result, fallback);
    }
}
