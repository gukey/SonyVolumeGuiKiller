using System.Windows;
using SonyVolumeGuiKillerPcManager.Services;

namespace SonyVolumeGuiKillerPcManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            EmbeddedToolExtractor.EnsureAdbTools();
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法释放内置 ADB 工具：{exception.Message}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
