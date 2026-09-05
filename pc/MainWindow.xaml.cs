using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SonyVolumeGuiKillerPcManager.Models;
using SonyVolumeGuiKillerPcManager.Services;

namespace SonyVolumeGuiKillerPcManager;

public partial class MainWindow : Window
{
    private readonly AdbRunner _adbRunner = new();
    private readonly SonyManagerService _manager;
    private readonly ObservableCollection<AppInfo> _apps = [];
    private readonly ICollectionView _appsView;
    private CancellationTokenSource? _operationCancellation;
    private string _lastBackupPath = "-";
    private string _lastBackupSha = "-";
    private string _lastBackupSize = "-";
    private string _lastTvAppBackupPath = "-";

    public MainWindow()
    {
        InitializeComponent();

        _manager = new SonyManagerService(_adbRunner);
        _adbRunner.Log += AppendLog;

        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "backups"));
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "backups", "tv_apps"));
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "local_apk"));

        _appsView = CollectionViewSource.GetDefaultView(_apps);
        _appsView.Filter = FilterApps;
        AppListBox.ItemsSource = _appsView;

        LocalAdbPathText.Text = _adbRunner.AdbPath;
        AdbPathText.Text = $"ADB 路径：{_adbRunner.AdbPath}";
        AdbDirectoryText.Text = $"工具目录：{Path.Combine(AppContext.BaseDirectory, "tools")}";
        SidebarIpTextBox.Text = TvIp;
        AppSearchTextBox.Text = string.Empty;

        ApplyConnectionBadge("未连接", "#94A3B8");
        ApplyAdbServiceStatus(_adbRunner.Exists);
        RenderHealthScore(0);
        ShowDashboardView();
        UpdateAppCountText();

        AppendLog("[INFO] 程序启动完成。");
        AppendLog(_adbRunner.Exists
            ? $"[SUCCESS] 内置 ADB：{_adbRunner.AdbPath}"
            : $"[ERROR] 未找到内置 ADB：{_adbRunner.AdbPath}");

        Loaded += async (_, _) => await LoadAdbVersionAsync();
    }

    private string TvIp => TvIpTextBox.Text.Trim();
    private int PairingPort => ParsePort(PairingPortTextBox.Text, "配对端口");
    private string PairingCode => PairingCodeTextBox.Text.Trim();
    private int DebugPort => ParsePortOrDefault(DebugPortTextBox.Text, 5555, "调试端口");

    private bool _syncingIpText;

    private void TvIpTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SidebarIpTextBox is null)
        {
            return;
        }

        if (_syncingIpText)
        {
            return;
        }

        _syncingIpText = true;
        SidebarIpTextBox.Text = TvIpTextBox.Text;
        _syncingIpText = false;
    }

    private void SidebarIpTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TvIpTextBox is null)
        {
            return;
        }

        if (_syncingIpText)
        {
            return;
        }

        _syncingIpText = true;
        TvIpTextBox.Text = SidebarIpTextBox.Text;
        _syncingIpText = false;
    }
    private async void ConnectButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在连接电视...", async token =>
        {
            DeviceStatus status;
            if (string.IsNullOrWhiteSpace(PairingCodeTextBox.Text) &&
                string.IsNullOrWhiteSpace(PairingPortTextBox.Text))
            {
                AppendLog("[INFO] 未填写配对信息，使用调试端口连接电视（留空时为 5555）。");
                status = await _manager.VerifyStatusAsync(TvIp, DebugPort, token);
            }
            else
            {
                status = await _manager.ConnectAsync(TvIp, PairingPort, PairingCode, DebugPort, token);
            }

            RenderStatus(status);
            AppendLog("[SUCCESS] 电视连接流程完成。");
        });

    private async void InstallButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在一键安装：自动 5555、音量守护、授权 Key、无障碍...", async token =>
        {
            AppendLog("[INFO] 一键安装开始：安装自动 5555 App、安装音量守护 App、写入授权 Key、启用无障碍。");
            var status = await _manager.OneClickAutoDeployAsync(TvIp, PairingPort, PairingCode, DebugPort, token);
            DebugPortTextBox.Text = "5555";
            RenderStatus(status);
            AppendLog("[SUCCESS] 一键安装完成：电视端会自动恢复 5555，音量 GUI 守护已启用。");
            AppendLog(BuildFinalResult(status));
        });
    private async void WriteKeyButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在写入授权 Key...", async token =>
        {
            await _manager.WriteAdbKeyAsync(TvIp, DebugPort, token);
            RenderStatus(await _manager.VerifyStatusAsync(TvIp, DebugPort, token));
            AppendLog("[SUCCESS] 授权 key 写入成功。");
        });

    private async void EnableAccessibilityButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在启用无障碍...", async token =>
        {
            await _manager.EnableAccessibilityAsync(TvIp, DebugPort, token);
            RenderStatus(await _manager.VerifyStatusAsync(TvIp, DebugPort, token));
            AppendLog("[SUCCESS] 无障碍服务配置完成，原有服务已保留。");
        });

    private async void VerifyButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在诊断...", async token =>
        {
            var status = await _manager.VerifyStatusAsync(TvIp, DebugPort, token);
            RenderStatus(status);
            AppendLog(BuildFinalResult(status));
        });

    private async void ViewLogsButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在读取日志...", async token =>
        {
            var logs = await _manager.GetLogsAsync(TvIp, DebugPort, 200, token);
            AppendLog("[INFO] SonyVolumeGuiKiller 最近日志：");
            AppendLog(string.IsNullOrWhiteSpace(logs) ? "[INFO] 没有匹配的日志。" : logs);
        });

    private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定清空电视的全部 logcat 缓冲区吗？",
                "确认清空日志",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync("正在清空电视日志...", async token =>
        {
            await _manager.ClearLogsAsync(TvIp, DebugPort, token);
            AppendLog("[SUCCESS] 电视 logcat 已清空。");
        });
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在备份电视 APK...", async token =>
        {
            var backup = await _manager.BackupApkAsync(TvIp, DebugPort, token);
            SetPackageBackupResult(backup.Path, backup.Size, backup.Sha256, "备份成功");
            AppendLog(
                $"[SUCCESS] APK 备份完成：{backup.Path}{Environment.NewLine}" +
                $"[INFO] 文件大小：{backup.Size:N0} 字节{Environment.NewLine}" +
                $"[INFO] SHA256：{backup.Sha256}");
        });

    private async void RefreshAppsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAppsAsync();

    private async void InstallLocalApkButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Android APK (*.apk)|*.apk",
            InitialDirectory = Path.Combine(AppContext.BaseDirectory, "local_apk"),
            Multiselect = false,
            Title = "选择要安装到电视的 APK"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunOperationAsync("正在安装本地 APK...", async token =>
        {
            var installResult = await _manager.InstallLocalApkAsync(TvIp, DebugPort, dialog.FileName, token);
            AppManagerActionText.Text =
                $"安装成功{Environment.NewLine}包名：{installResult.PackageName}{Environment.NewLine}文件：{dialog.FileName}";
            AppendLog($"[SUCCESS] 本地 APK 安装成功：{dialog.FileName}");

            var status = await _manager.VerifyStatusAsync(TvIp, DebugPort, token);
            RenderStatus(status);
            await RefreshAppsCoreAsync(token);
        });
    }

    private async void BackupSelectedAppsButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedApps = _apps.Where(app => app.IsSelected).ToList();
        if (selectedApps.Count == 0)
        {
            MessageBox.Show("请先选择至少一个应用。", "未选择应用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunOperationAsync("正在备份选中应用...", async token =>
        {
            foreach (var app in selectedApps)
            {
                var backup = await _manager.BackupAppAsync(TvIp, DebugPort, app, token);
                _lastTvAppBackupPath = backup.Path;
                AppManagerActionText.Text =
                    $"已备份：{app.AppLabel}{Environment.NewLine}包名：{app.PackageName}{Environment.NewLine}路径：{backup.Path}";
                AppendLog(
                    $"[SUCCESS] 应用备份完成：{app.AppLabel} / {app.PackageName}{Environment.NewLine}" +
                    $"[INFO] 保存路径：{backup.Path}{Environment.NewLine}" +
                    $"[INFO] SHA256：{backup.Sha256}");
            }
        });
    }

    private async void BackupSingleAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppInfo app })
        {
            return;
        }

        await RunOperationAsync($"正在备份 {app.AppLabel}...", async token =>
        {
            var backup = await _manager.BackupAppAsync(TvIp, DebugPort, app, token);
            _lastTvAppBackupPath = backup.Path;
            AppManagerActionText.Text =
                $"已备份：{app.AppLabel}{Environment.NewLine}包名：{app.PackageName}{Environment.NewLine}路径：{backup.Path}";
            AppendLog(
                $"[SUCCESS] 单个应用备份完成：{app.AppLabel} / {app.PackageName}{Environment.NewLine}" +
                $"[INFO] 保存路径：{backup.Path}{Environment.NewLine}" +
                $"[INFO] SHA256：{backup.Sha256}");
        });
    }

    private void SelectAllAppsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var app in _appsView.Cast<AppInfo>())
        {
            app.IsSelected = true;
        }

        UpdateAppCountText();
    }

    private void ClearAppSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var app in _apps)
        {
            app.IsSelected = false;
        }

        UpdateAppCountText();
    }

    private void AppSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _appsView.Refresh();
        UpdateAppCountText();
    }

    private async void DashboardNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowDashboardView();
        await Task.CompletedTask;
    }

    private async void AppManagerNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAppManagerView();
        if (_apps.Count == 0)
        {
            await RefreshAppsAsync();
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var helpWindow = new HelpWindow { Owner = this };
        helpWindow.ShowDialog();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        _operationCancellation?.Cancel();

    private void ClearUiLogButton_Click(object sender, RoutedEventArgs e) =>
        LogTextBox.Clear();

    private async Task RunOperationAsync(string busyMessage, Func<CancellationToken, Task> operation)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, busyMessage);
        AppendLog($"[INFO] {busyMessage}");

        try
        {
            await operation(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("[WARN] 操作已取消。");
        }
        catch (Exception exception)
        {
            AppendLog($"[ERROR] {exception.Message}");
            MessageBox.Show(exception.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false, "等待操作");
        }
    }

    private void SetBusy(bool busy, string message)
    {
        BusyText.Text = message;
        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (button != CancelButton)
            {
                button.IsEnabled = !busy;
            }
        }

        CancelButton.IsEnabled = busy;
    }

    private async Task LoadAdbVersionAsync()
    {
        if (!_adbRunner.Exists)
        {
            return;
        }

        try
        {
            var result = await _adbRunner.RunAsync(["version"], TimeSpan.FromSeconds(10));
            var versionLine = result.StdOut
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "ADB 版本：未知";
            AdbVersionText.Text = versionLine.Replace("Android Debug Bridge version", "ADB 版本");
        }
        catch (Exception exception)
        {
            AdbVersionText.Text = $"ADB 版本：读取失败 ({exception.Message})";
        }
    }

    private async Task RefreshAppsAsync(CancellationToken cancellationToken = default, bool switchToPage = true)
    {
        if (switchToPage)
        {
            ShowAppManagerView();
        }

        await RunOperationAsync(
            "正在读取电视应用列表...",
            token => RefreshAppsCoreAsync(cancellationToken == default ? token : cancellationToken));
    }

    private async Task RefreshAppsCoreAsync(CancellationToken cancellationToken)
    {
        var apps = await _manager.GetInstalledAppsAsync(TvIp, DebugPort, cancellationToken);

        _apps.Clear();
        foreach (var app in apps)
        {
            app.PropertyChanged += App_PropertyChanged;
            _apps.Add(app);
        }

        _appsView.Refresh();
        UpdateAppCountText();
        AppManagerActionText.Text = $"应用列表已刷新，共 {_apps.Count} 个应用。";
        AppendLog($"[SUCCESS] 已读取电视应用列表，共 {_apps.Count} 个应用。");
    }

    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppInfo.IsSelected))
        {
            UpdateAppCountText();
        }
    }

    private bool FilterApps(object item)
    {
        if (item is not AppInfo app)
        {
            return false;
        }

        var query = AppSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return app.AppLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               app.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateAppCountText()
    {
        var visible = _appsView.Cast<AppInfo>().Count();
        var selected = _apps.Count(app => app.IsSelected);
        AppCountText.Text = $"{visible} 个应用 / 已选 {selected}";
    }

    private void ShowDashboardView()
    {
        DashboardView.Visibility = Visibility.Visible;
        AppManagerView.Visibility = Visibility.Collapsed;
        DashboardNavButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EAF2FF"));
        DashboardNavButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
        AppManagerNavButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F7FB"));
        AppManagerNavButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C3A56"));
    }

    private void ShowAppManagerView()
    {
        DashboardView.Visibility = Visibility.Collapsed;
        AppManagerView.Visibility = Visibility.Visible;
        AppManagerNavButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EAF2FF"));
        AppManagerNavButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
        DashboardNavButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F7FB"));
        DashboardNavButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C3A56"));
    }

    private void RenderStatus(DeviceStatus status)
    {
        SidebarIpTextBox.Text = status.TvIp;

        DeviceStateText.Text = status.DeviceState;
        PackageStateText.Text = status.PackageInstalled ? "installed" : "not installed";
        VersionText.Text = status.PackageVersionName;
        VersionCodeText.Text = status.PackageVersionCode;
        PackagePathText.Text = status.PackagePath;
        RunAsText.Text = status.RunAsAvailable ? "可用" : "不可用";
        KeyText.Text = status.AdbKeyInstalled ? "已写入" : "未写入";
        AccessibilityText.Text = status.AccessibilityEnabled && status.AccessibilityServiceEnabled ? "enabled" : "disabled";
        AccessibilityEnabledText.Text = status.AccessibilityEnabled ? "已开启" : "未开启";
        AccessibilityServiceText.Text = status.AccessibilityServiceEnabled ? "已包含目标服务" : "未包含目标服务";
        TcpPortText.Text = status.TcpPort5555Enabled ? $"{status.DebugPort} open" : $"{status.DebugPort} closed";
        ControllerText.Text = status.VolumeControllerNull ? "null" : "not null";
        ControllerRawTextBox.Text = status.VolumeControllerRawLine;
        ServicesTextBox.Text = status.CurrentAccessibilityServices;

        BackupPathTextBox.Text = _lastBackupPath;
        BackupShaText.Text = _lastBackupSha;
        BackupSizeText.Text = _lastBackupSize;

        var adbStateText = status.AdbAuthorized
            ? "connected"
            : status.DeviceState switch
            {
                "未授权" => "unauthorized",
                "离线" => "offline",
                _ => "disconnected"
            };

        ApplyConnectionBadge(status.DeviceState, status.AdbAuthorized ? "#16A34A" :
            status.DeviceState == "未授权" ? "#D97706" :
            status.DeviceState == "离线" ? "#DC2626" : "#94A3B8");
        DeviceStateBadgeText.Text = adbStateText;
        SetBadge(DeviceStateBadge, status.AdbAuthorized ? "#16A34A" :
            status.DeviceState == "未授权" ? "#D97706" :
            status.DeviceState == "离线" ? "#DC2626" : "#94A3B8");

        PackageBadgeText.Text = status.PackageInstalled ? "installed" : "not installed";
        SetBadge(PackageBadge, status.PackageInstalled ? "#16A34A" : "#94A3B8");

        AccessibilityBadgeText.Text = status.AccessibilityEnabled && status.AccessibilityServiceEnabled ? "enabled" : "disabled";
        SetBadge(AccessibilityBadge, status.AccessibilityEnabled && status.AccessibilityServiceEnabled ? "#16A34A" : "#D97706");

        var guardReady = status.VolumeControllerNull && status.TcpPort5555Enabled;
        GuardBadgeText.Text = guardReady ? "守护正常" : "待处理";
        SetBadge(GuardBadge, guardReady ? "#16A34A" : "#D97706");

        HealthHeadlineText.Text = guardReady
            ? "设备状态良好"
            : status.DeviceState == "未授权"
                ? "等待设备授权"
                : "需要进一步处理";
        HealthSubText.Text = status.DeviceState == "未授权"
            ? "请在电视端勾选“始终允许”，确认 RSA 授权弹窗。"
            : status.LastError is { Length: > 0 }
                ? status.LastError
                : "索尼电视管理工具状态已更新。";

        DeviceStateHintText.Text = status.DeviceState == "未授权"
            ? "❌ 未弹出设备授权窗口时，请关闭并重新开启网络调试，再次 adb connect；如仍无效，重启电视。"
            : status.DeviceState;
        LastCheckText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var score = CalculateHealthScore(status);
        RenderHealthScore(score);

        if (!string.IsNullOrWhiteSpace(status.LastLogLines))
        {
            AppendLog($"[INFO] 最近 30 行应用日志：{Environment.NewLine}{status.LastLogLines}");
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            AppendLog($"[WARN] 诊断未完整完成：{status.LastError}");
        }
    }

    private void SetPackageBackupResult(string path, long size, string sha, string status)
    {
        _lastBackupPath = path;
        _lastBackupSha = sha;
        _lastBackupSize = $"{size / 1024d / 1024d:F2} MB";
        BackupStatusText.Text = status;
        BackupPathTextBox.Text = path;
        BackupShaText.Text = sha;
        BackupSizeText.Text = _lastBackupSize;
    }

    private static int CalculateHealthScore(DeviceStatus status)
    {
        var score = 0;
        if (status.AdbAuthorized) score += 20;
        if (status.PackageInstalled) score += 20;
        if (status.AccessibilityEnabled && status.AccessibilityServiceEnabled) score += 20;
        if (status.VolumeControllerNull) score += 20;
        if (status.TcpPort5555Enabled) score += 20;
        return score;
    }

    private void RenderHealthScore(int score)
    {
        HealthScoreText.Text = $"{score} / 100";
        HealthScoreBar.Value = score;
    }

    private void ApplyConnectionBadge(string text, string color)
    {
        SidebarConnectionBadgeText.Text = text;
        SetBadge(SidebarConnectionBadge, color);
    }

    private void ApplyAdbServiceStatus(bool adbAvailable)
    {
        AdbServiceBadgeText.Text = adbAvailable ? "ADB 服务可用" : "ADB 服务不可用";
        SetBadge(AdbServiceBadge, adbAvailable ? "#16A34A" : "#DC2626");
    }

    private static void SetBadge(Border badge, string color) =>
        badge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static string BuildFinalResult(DeviceStatus status) =>
        string.Join(Environment.NewLine,
        [
            "[INFO] 诊断结果：",
            $"- APK：{(status.PackageInstalled ? "安装成功" : "未安装")}",
            $"- 授权 key：{(status.AdbKeyInstalled ? "写入成功" : "未写入")}",
            $"- 无障碍：{(status.AccessibilityEnabled && status.AccessibilityServiceEnabled ? "已启用" : "未启用")}",
            $"- 电视端 {status.DebugPort}：{(status.TcpPort5555Enabled ? "已开启" : "未开启")}",
            $"- mVolumeController：{(status.VolumeControllerNull ? "已为 null" : "仍未断开")}",
            $"- 健康度：{CalculateHealthScore(status)} / 100"
        ]);

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogTextBox.AppendText($"{line}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();

            try
            {
                var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(
                    Path.Combine(logDirectory, $"manager_{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Keep UI logging available even if file logging fails.
            }
        });
    }

    private static int ParsePortOrDefault(string value, int defaultPort, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? defaultPort
            : ParsePort(value, fieldName);
    }
    private static int ParsePort(string value, string fieldName)
    {
        if (!int.TryParse(value.Trim(), out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"请输入有效的{fieldName}（1～65535）。");
        }

        return port;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
