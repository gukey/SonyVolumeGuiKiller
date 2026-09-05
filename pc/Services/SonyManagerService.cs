using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Net.Http;
using SonyVolumeGuiKillerPcManager.Models;

namespace SonyVolumeGuiKillerPcManager.Services;

public sealed partial class SonyManagerService
{
    public const string PackageName = "com.codex.sonyvolumegui";
    public const string AccessibilityService =
        "com.codex.sonyvolumegui/.VolumeGuardAccessibilityService";
    private const string AutoEnablePackageName = "com.tpn.adbautoenable.fix";
    private const string LegacyAutoEnablePackageName = "com.tpn.adbautoenable";

    private readonly AdbRunner _adb;

    public SonyManagerService(AdbRunner adb)
    {
        _adb = adb;
    }

    public async Task<DeviceStatus> ConnectAsync(
        string tvIp,
        int pairingPort,
        string pairingCode,
        int debugPort,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(pairingPort, nameof(pairingPort));
        ValidatePort(debugPort, nameof(debugPort));
        if (string.IsNullOrWhiteSpace(pairingCode))
        {
            throw new ArgumentException("Please enter pairing code.");
        }

        var pair = await _adb.RunAsync(
            ["pair", Endpoint(tvIp, pairingPort), pairingCode.Trim()],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!pair.Success ||
            pair.CombinedOutput.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(AdbErrorTranslator.Explain(pair, "ADB wireless pairing failed."));
        }

        await EnsureConnectedDeviceAsync(tvIp, debugPort, cancellationToken);

        return await VerifyStatusAsync(tvIp, debugPort, cancellationToken);
    }

    public async Task<DeviceStatus> InstallOrUpdateAsync(
        string tvIp,
        int debugPort,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        EnsureAssets();
        var activeDebugPort = await ResolveActiveDebugPortAsync(tvIp, debugPort, cancellationToken);

        await RequireSuccessAsync(["kill-server"], "停止 ADB 服务失败。", cancellationToken);
        await RequireSuccessAsync(["start-server"], "启动 ADB 服务失败。", cancellationToken);
        await EnsureConnectedDeviceAsync(tvIp, activeDebugPort, cancellationToken);

        var apkPath = Path.Combine(AppContext.BaseDirectory, "apk", "SonyVolumeGuiKiller-release.apk");
        await RequireSuccessAsync(
            ["-s", Endpoint(tvIp, activeDebugPort), "install", "-r", apkPath],
            "安装或更新 APK 失败。",
            cancellationToken,
            TimeSpan.FromMinutes(3));

        await WriteAdbKeyAsync(tvIp, activeDebugPort, cancellationToken);
        await EnableAccessibilityAsync(tvIp, activeDebugPort, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return await VerifyStatusAsync(tvIp, activeDebugPort, cancellationToken);
    }

    public async Task<DeviceStatus> OneClickAutoDeployAsync(
        string tvIp,
        int pairingPort,
        string pairingCode,
        int debugPort,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        EnsureAssets();
        EnsureAutoEnableAsset();

        await RequireSuccessAsync(["kill-server"], "停止 ADB 服务失败。", cancellationToken);
        await RequireSuccessAsync(["start-server"], "启动 ADB 服务失败。", cancellationToken);

        var activeDebugPort = await ResolveActiveDebugPortAsync(tvIp, debugPort, cancellationToken);
        if (!await CanConnectAsync(tvIp, activeDebugPort, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(pairingCode))
            {
                throw new ArgumentException("请输入配对码，或把调试口改成当前可连接端口。已优先尝试当前调试口和 5555。");
            }

            ValidatePort(pairingPort, nameof(pairingPort));
            var pair = await _adb.RunAsync(
                ["pair", Endpoint(tvIp, pairingPort), pairingCode.Trim()],
                TimeSpan.FromSeconds(18),
                cancellationToken);
            activeDebugPort = await ResolveActiveDebugPortAsync(tvIp, debugPort, cancellationToken);
            if ((!pair.Success || pair.CombinedOutput.Contains("failed", StringComparison.OrdinalIgnoreCase)) &&
                !await CanConnectAsync(tvIp, activeDebugPort, cancellationToken))
            {
                throw new InvalidOperationException(AdbErrorTranslator.Explain(pair, "ADB 无线配对失败。请确认配对码和配对端口仍然有效。"));
            }
        }

        await EnsureConnectedDeviceAsync(tvIp, activeDebugPort, cancellationToken);
        await InstallAutoEnableAsync(tvIp, activeDebugPort, cancellationToken);
        await WriteAutoEnableAdbIdentityAsync(tvIp, activeDebugPort, cancellationToken);
        await StartAutoEnableAsync(tvIp, activeDebugPort, cancellationToken);
        await TriggerAutoEnableSwitchAsync(tvIp, cancellationToken);
        await WaitForPort5555Async(tvIp, cancellationToken);

        await InstallOrUpdateAsync(tvIp, 5555, cancellationToken);
        await TriggerAutoEnableSwitchAsync(tvIp, cancellationToken);
        return await VerifyStatusAsync(tvIp, 5555, cancellationToken);
    }
    public async Task WriteAdbKeyAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        var androidDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".android");
        var privateKey = Path.Combine(androidDirectory, "adbkey");
        var publicKey = Path.Combine(androidDirectory, "adbkey.pub");

        if (!File.Exists(privateKey) || !File.Exists(publicKey))
        {
            throw new FileNotFoundException(
                $"未找到本机 ADB 授权 key。请先完成一次 ADB RSA 授权。期望文件：{privateKey} 和 {publicKey}");
        }

        await DeviceSuccessAsync(tvIp, debugPort, ["push", privateKey, "/data/local/tmp/sonyvol_adbkey"],
            "上传 adbkey 失败。", cancellationToken);
        await DeviceSuccessAsync(tvIp, debugPort, ["push", publicKey, "/data/local/tmp/sonyvol_adbkey.pub"],
            "上传 adbkey.pub 失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["chmod", "644", "/data/local/tmp/sonyvol_adbkey", "/data/local/tmp/sonyvol_adbkey.pub"],
            "设置临时 key 权限失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort, ["run-as", PackageName, "mkdir", "-p", "files"],
            "无法创建 APK 私有 files 目录。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["run-as", PackageName, "cp", "/data/local/tmp/sonyvol_adbkey", "files/adbkey"],
            "复制 adbkey 到 APK 私有目录失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["run-as", PackageName, "cp", "/data/local/tmp/sonyvol_adbkey.pub", "files/adbkey.pub"],
            "复制 adbkey.pub 到 APK 私有目录失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["run-as", PackageName, "chmod", "600", "files/adbkey", "files/adbkey.pub"],
            "设置 APK 私有 key 权限失败。", cancellationToken);
    }

    private async Task InstallAutoEnableAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        var apkPath = AutoEnableApkPath();
        await DeviceAsync(
            tvIp,
            debugPort,
            ["shell", "pm", "uninstall", LegacyAutoEnablePackageName],
            cancellationToken,
            TimeSpan.FromSeconds(20));
        await DeviceSuccessAsync(
            tvIp,
            debugPort,
            ["install", "-r", apkPath],
            "安装自动 5555 APK 失败。",
            cancellationToken,
            TimeSpan.FromMinutes(3));
        await ShellSuccessAsync(
            tvIp,
            debugPort,
            ["pm", "grant", AutoEnablePackageName, "android.permission.WRITE_SECURE_SETTINGS"],
            "授予自动 5555 APK 权限失败。",
            cancellationToken);
    }

    private async Task WriteAutoEnableAdbIdentityAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "SonyVolumeGuiKillerPcManager", "adb-auto-enable");
        Directory.CreateDirectory(tempDirectory);

        var privateKeyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".android",
            "adbkey");
        if (!File.Exists(privateKeyPath))
        {
            throw new FileNotFoundException($"未找到本机 ADB 授权 key：{privateKeyPath}");
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath, cancellationToken));
        var localPrivateKey = Path.Combine(tempDirectory, "adb_key");
        var localPublicKey = Path.Combine(tempDirectory, "adb_key.pub");
        var localCertificate = Path.Combine(tempDirectory, "adb_cert");

        await File.WriteAllBytesAsync(localPrivateKey, rsa.ExportPkcs8PrivateKey(), cancellationToken);
        await File.WriteAllBytesAsync(localPublicKey, rsa.ExportSubjectPublicKeyInfo(), cancellationToken);

        var request = new CertificateRequest(
            "CN=ADBKeyFromPC",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));
        await File.WriteAllBytesAsync(localCertificate, certificate.Export(X509ContentType.Cert), cancellationToken);

        await DeviceSuccessAsync(tvIp, debugPort, ["push", localPrivateKey, "/data/local/tmp/adb_key"],
            "上传自动 5555 私钥失败。", cancellationToken);
        await DeviceSuccessAsync(tvIp, debugPort, ["push", localPublicKey, "/data/local/tmp/adb_key.pub"],
            "上传自动 5555 公钥失败。", cancellationToken);
        await DeviceSuccessAsync(tvIp, debugPort, ["push", localCertificate, "/data/local/tmp/adb_cert"],
            "上传自动 5555 证书失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort, ["run-as", AutoEnablePackageName, "mkdir", "-p", "files"],
            "创建自动 5555 私有目录失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["run-as", AutoEnablePackageName, "cp", "/data/local/tmp/adb_key", "files/adb_key"],
            "写入自动 5555 私钥失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["run-as", AutoEnablePackageName, "cp", "/data/local/tmp/adb_key.pub", "files/adb_key.pub"],
            "写入自动 5555 公钥失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["run-as", AutoEnablePackageName, "cp", "/data/local/tmp/adb_cert", "files/adb_cert"],
            "写入自动 5555 证书失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort,
            ["run-as", AutoEnablePackageName, "chmod", "600", "files/adb_key", "files/adb_key.pub", "files/adb_cert"],
            "设置自动 5555 授权文件权限失败。", cancellationToken);
    }

    private async Task StartAutoEnableAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        await ShellSuccessAsync(tvIp, debugPort, ["am", "force-stop", AutoEnablePackageName],
            "重启自动 5555 服务失败。", cancellationToken);
        await ShellSuccessAsync(tvIp, debugPort, ["monkey", "-p", AutoEnablePackageName, "1"],
            "启动自动 5555 APK 失败。", cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
    }

    private static async Task TriggerAutoEnableSwitchAsync(string tvIp, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        using var response = await client.PostAsync(
            $"http://{tvIp.Trim()}:9093/api/switch",
            new StringContent(string.Empty),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task WaitForPort5555Async(string tvIp, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var connect = await _adb.RunAsync(["connect", Endpoint(tvIp, 5555)], cancellationToken: cancellationToken);
            if (connect.Success &&
                !connect.CombinedOutput.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                var state = await GetDeviceStateAsync(tvIp, 5555, cancellationToken);
                if (state == "device")
                {
                    return;
                }
            }
        }

        throw new TimeoutException("等待电视自动切换到 5555 超时。");
    }

    public async Task EnableAccessibilityAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        var currentResult = await DeviceAsync(
            tvIp,
            debugPort,
            ["shell", "settings", "get", "secure", "enabled_accessibility_services"],
            cancellationToken);
        if (!currentResult.Success)
        {
            throw new InvalidOperationException(
                AdbErrorTranslator.Explain(currentResult, "读取现有无障碍服务失败。"));
        }

        var services = currentResult.StdOut.Trim();
        var existing = services is "" or "null"
            ? []
            : services.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (!existing.Contains(AccessibilityService, StringComparer.OrdinalIgnoreCase))
        {
            existing.Add(AccessibilityService);
        }

        await ShellSuccessAsync(
            tvIp,
            debugPort,
            ["settings", "put", "secure", "enabled_accessibility_services", string.Join(':', existing)],
            "写入无障碍服务列表失败。",
            cancellationToken);
        await ShellSuccessAsync(
            tvIp,
            debugPort,
            ["settings", "put", "secure", "accessibility_enabled", "1"],
            "启用系统无障碍开关失败。",
            cancellationToken);
    }

    public async Task<DeviceStatus> VerifyStatusAsync(
        string tvIp,
        int debugPort,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        var status = new DeviceStatus { TvIp = tvIp, DebugPort = debugPort };

        try
        {
            var state = await GetDeviceStateAsync(tvIp, debugPort, cancellationToken);
            status.DeviceState = MapDeviceState(state);
            status.AdbConnected = state is "device" or "unauthorized" or "offline";
            status.AdbAuthorized = state == "device";
            if (!status.AdbAuthorized)
            {
                return status;
            }

            var packageList = await ShellAsync(tvIp, debugPort, ["pm", "list", "packages", PackageName], cancellationToken);
            status.PackageInstalled = packageList.StdOut.Contains($"package:{PackageName}", StringComparison.OrdinalIgnoreCase);

            if (status.PackageInstalled)
            {
                var packageDump = await ShellAsync(tvIp, debugPort, ["dumpsys", "package", PackageName], cancellationToken);
                status.PackageVersionName = MatchValue(packageDump.StdOut, @"versionName=([^\s]+)");
                status.PackageVersionCode = MatchValue(packageDump.StdOut, @"versionCode=(\d+)");

                var path = await ShellAsync(tvIp, debugPort, ["pm", "path", PackageName], cancellationToken);
                status.PackagePath = ParsePackagePath(path.StdOut) ?? "-";

                var runAs = await ShellAsync(tvIp, debugPort, ["run-as", PackageName, "pwd"], cancellationToken);
                status.RunAsAvailable = runAs.Success;
                var key = await ShellAsync(
                    tvIp,
                    debugPort,
                    ["run-as", PackageName, "sh", "-c", "test -f files/adbkey && test -f files/adbkey.pub && echo installed"],
                    cancellationToken);
                status.AdbKeyInstalled = key.Success &&
                    key.StdOut.Contains("installed", StringComparison.OrdinalIgnoreCase);
            }

            var services = await ShellAsync(
                tvIp,
                debugPort,
                ["settings", "get", "secure", "enabled_accessibility_services"],
                cancellationToken);
            status.CurrentAccessibilityServices = services.StdOut.Trim();
            status.AccessibilityServiceEnabled = status.CurrentAccessibilityServices.Contains(
                AccessibilityService,
                StringComparison.OrdinalIgnoreCase);

            var enabled = await ShellAsync(
                tvIp,
                debugPort,
                ["settings", "get", "secure", "accessibility_enabled"],
                cancellationToken);
            status.AccessibilityEnabled = enabled.StdOut.Trim() == "1";

            var tcpPort = await ShellAsync(
                tvIp,
                debugPort,
                ["getprop", "service.adb.tcp.port"],
                cancellationToken);
            status.TcpPort5555Enabled = tcpPort.StdOut.Trim() == debugPort.ToString();

            var audio = await ShellAsync(tvIp, debugPort, ["dumpsys", "audio"], cancellationToken);
            status.VolumeControllerRawLine = audio.StdOut
                .Split(Environment.NewLine)
                .FirstOrDefault(line => line.Contains("mVolumeController", StringComparison.OrdinalIgnoreCase))
                ?.Trim() ?? "-";
            status.VolumeControllerNull = VolumeControllerIsNull(status.VolumeControllerRawLine);

            status.LastLogLines = await GetLogsAsync(tvIp, debugPort, 30, cancellationToken);
        }
        catch (Exception exception)
        {
            status.LastError = exception.Message;
        }

        return status;
    }

    public async Task<string> GetLogsAsync(
        string tvIp,
        int debugPort,
        int lineCount,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        var result = await DeviceAsync(
            tvIp,
            debugPort,
            ["logcat", "-d", "-s", "SonyVolumeGuiKiller"],
            cancellationToken,
            TimeSpan.FromSeconds(20));
        if (!result.Success)
        {
            throw new InvalidOperationException(AdbErrorTranslator.Explain(result, "读取日志失败。"));
        }

        return string.Join(
            Environment.NewLine,
            result.StdOut.Split(Environment.NewLine).TakeLast(lineCount));
    }

    public async Task ClearLogsAsync(string tvIp, int debugPort, CancellationToken cancellationToken) =>
        await DeviceSuccessAsync(tvIp, debugPort, ["logcat", "-c"], "清空日志失败。", cancellationToken);

    public async Task<(string Path, long Size, string Sha256)> BackupApkAsync(
        string tvIp,
        int debugPort,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        await EnsureConnectedDeviceAsync(tvIp, debugPort, cancellationToken);

        var pathResult = await ShellAsync(tvIp, debugPort, ["pm", "path", PackageName], cancellationToken);
        var remotePath = ParsePackagePath(pathResult.StdOut);
        if (!pathResult.Success || string.IsNullOrWhiteSpace(remotePath))
        {
            throw new InvalidOperationException("电视上未安装 Sony Volume GUI Killer，无法拉取 APK。");
        }

        var backupDirectory = Path.Combine(AppContext.BaseDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var fileName = $"SonyVolumeGuiKiller_from_tv_{DateTime.Now:yyyyMMdd_HHmmss}.apk";
        var localPath = Path.Combine(backupDirectory, fileName);

        await DeviceSuccessAsync(
            tvIp,
            debugPort,
            ["pull", remotePath, localPath],
            "从电视拉取 APK 失败。",
            cancellationToken,
            TimeSpan.FromMinutes(3));

        var fileInfo = new FileInfo(localPath);
        await using var apkStream = File.OpenRead(localPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(apkStream, cancellationToken));
        await File.WriteAllTextAsync($"{localPath}.sha256", $"{hash}  {fileName}{Environment.NewLine}",
            cancellationToken);
        return (localPath, fileInfo.Length, hash);
    }

    public async Task<IReadOnlyList<AppInfo>> GetInstalledAppsAsync(
        string tvIp,
        int debugPort,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        await EnsureConnectedDeviceAsync(tvIp, debugPort, cancellationToken);

        var result = await ShellAsync(tvIp, debugPort, ["pm", "list", "packages", "-f", "-3"], cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                AdbErrorTranslator.Explain(result, "无法获取应用列表，请检查 ADB 连接状态。"));
        }

        var apps = new List<AppInfo>();
        foreach (var line in result.StdOut.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var app = ParsePackageListLine(line);
            if (app is null)
            {
                continue;
            }

            var dump = await ShellAsync(tvIp, debugPort, ["dumpsys", "package", app.PackageName], cancellationToken);
            if (dump.Success)
            {
                app.VersionName = MatchValue(dump.StdOut, @"versionName=([^\s]+)");
                app.VersionCode = MatchValue(dump.StdOut, @"versionCode=(\d+)");
                app.InstallTime = MatchValue(dump.StdOut, @"firstInstallTime=([^\r\n]+)");

                var label = MatchValue(dump.StdOut, @"application-label(?:-zh-CN|-zh)?[:=]'?([^'\r\n]+)");
                app.AppLabel = label != "-" ? label : app.PackageName;

                var path = ParsePackagePath(dump.StdOut);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    app.ApkPath = path;
                }
            }
            else
            {
                app.AppLabel = app.PackageName;
            }

            if (string.IsNullOrWhiteSpace(app.AppLabel) || app.AppLabel == "-")
            {
                app.AppLabel = app.PackageName;
            }

            apps.Add(app);
        }

        return apps
            .OrderBy(app => app.AppLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<(string Path, long Size, string Sha256)> BackupAppAsync(
        string tvIp,
        int debugPort,
        AppInfo app,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        await EnsureConnectedDeviceAsync(tvIp, debugPort, cancellationToken);

        var pathResult = await ShellAsync(tvIp, debugPort, ["pm", "path", app.PackageName], cancellationToken);
        var remotePath = ParsePackagePath(pathResult.StdOut);
        if (!pathResult.Success || string.IsNullOrWhiteSpace(remotePath))
        {
            throw new InvalidOperationException($"无法获取 {app.PackageName} 的 APK 路径。");
        }

        var backupDirectory = Path.Combine(AppContext.BaseDirectory, "backups", "tv_apps");
        Directory.CreateDirectory(backupDirectory);

        var safeName = MakeSafeFileName(app.AppLabel);
        var fileName = $"{safeName}_{app.PackageName}_{DateTime.Now:yyyyMMdd_HHmmss}.apk";
        var localPath = Path.Combine(backupDirectory, fileName);

        await DeviceSuccessAsync(
            tvIp,
            debugPort,
            ["pull", remotePath, localPath],
            $"从电视备份 {app.PackageName} 失败。",
            cancellationToken,
            TimeSpan.FromMinutes(3));

        var fileInfo = new FileInfo(localPath);
        await using var apkStream = File.OpenRead(localPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(apkStream, cancellationToken));
        await File.WriteAllTextAsync(
            $"{localPath}.sha256",
            $"{hash}  {fileName}{Environment.NewLine}",
            cancellationToken);

        return (localPath, fileInfo.Length, hash);
    }

    public async Task<(string PackageName, string Message)> InstallLocalApkAsync(
        string tvIp,
        int debugPort,
        string apkPath,
        CancellationToken cancellationToken)
    {
        ValidateIp(tvIp);
        ValidatePort(debugPort, nameof(debugPort));
        await EnsureConnectedDeviceAsync(tvIp, debugPort, cancellationToken);

        if (!File.Exists(apkPath))
        {
            throw new FileNotFoundException($"未找到本地 APK：{apkPath}");
        }

        var result = await _adb.RunAsync(
            ["-s", Endpoint(tvIp, debugPort), "install", "-r", apkPath],
            TimeSpan.FromMinutes(3),
            cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                AdbErrorTranslator.ExplainInstallFailure(result, "本地 APK 安装失败。"));
        }

        return (TryInferPackageNameFromApkPath(apkPath), "Success");
    }

    private void EnsureAssets()
    {
        if (!_adb.Exists)
        {
            throw new FileNotFoundException($"未找到内置 ADB：{_adb.AdbPath}");
        }

        var apkPath = Path.Combine(AppContext.BaseDirectory, "apk", "SonyVolumeGuiKiller-release.apk");
        if (!File.Exists(apkPath))
        {
            throw new FileNotFoundException($"未找到安装包：{apkPath}");
        }
    }

    private static void EnsureAutoEnableAsset()
    {
        var apkPath = AutoEnableApkPath();
        if (File.Exists(apkPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(apkPath)!);
        using var stream = typeof(SonyManagerService).Assembly.GetManifestResourceStream(
            "EmbeddedTools.adb-auto-enable-fix.apk");
        if (stream is null)
        {
            throw new FileNotFoundException("未找到内嵌自动 5555 安装包资源。");
        }

        using var output = File.Create(apkPath);
        stream.CopyTo(output);
    }

    private static string AutoEnableApkPath() =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "adb-auto-enable-fix.apk");

    private async Task<int> ResolveActiveDebugPortAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        if (await CanConnectAsync(tvIp, debugPort, cancellationToken))
        {
            return debugPort;
        }

        if (debugPort != 5555 && await CanConnectAsync(tvIp, 5555, cancellationToken))
        {
            return 5555;
        }

        return debugPort;
    }

    private async Task<bool> CanConnectAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        var connect = await _adb.RunAsync(
            ["connect", Endpoint(tvIp, debugPort)],
            TimeSpan.FromSeconds(8),
            cancellationToken);
        if (!connect.Success ||
            connect.CombinedOutput.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await GetDeviceStateAsync(tvIp, debugPort, cancellationToken) == "device";
    }
    private async Task EnsureConnectedDeviceAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint(tvIp, debugPort);
        var connect = await _adb.RunAsync(["connect", endpoint], cancellationToken: cancellationToken);
        if (!connect.Success ||
            connect.CombinedOutput.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(AdbErrorTranslator.Explain(connect, "连接电视失败。"));
        }

        var state = await GetDeviceStateAsync(tvIp, debugPort, cancellationToken);
        if (state != "device")
        {
            var synthetic = new AdbResult(1, state, string.Empty, 0);
            throw new InvalidOperationException(AdbErrorTranslator.Explain(synthetic, $"设备状态异常：{state}"));
        }
    }

    private async Task<string> GetDeviceStateAsync(string tvIp, int debugPort, CancellationToken cancellationToken)
    {
        var result = await _adb.RunAsync(
            ["-s", Endpoint(tvIp, debugPort), "get-state"],
            cancellationToken: cancellationToken);
        return result.Success ? result.StdOut.Trim() : result.CombinedOutput.Trim();
    }

    private Task<AdbResult> ShellAsync(
        string tvIp,
        int debugPort,
        IEnumerable<string> shellArguments,
        CancellationToken cancellationToken) =>
        DeviceAsync(tvIp, debugPort, new[] { "shell" }.Concat(shellArguments), cancellationToken);

    private Task<AdbResult> DeviceAsync(
        string tvIp,
        int debugPort,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        _adb.RunAsync(
            new[] { "-s", Endpoint(tvIp, debugPort) }.Concat(arguments),
            timeout,
            cancellationToken);

    private async Task RequireSuccessAsync(
        IEnumerable<string> arguments,
        string fallback,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await _adb.RunAsync(arguments, timeout, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(AdbErrorTranslator.Explain(result, fallback));
        }
    }

    private async Task DeviceSuccessAsync(
        string tvIp,
        int debugPort,
        IEnumerable<string> arguments,
        string fallback,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await DeviceAsync(tvIp, debugPort, arguments, cancellationToken, timeout);
        if (!result.Success)
        {
            throw new InvalidOperationException(AdbErrorTranslator.Explain(result, fallback));
        }
    }

    private Task ShellSuccessAsync(
        string tvIp,
        int debugPort,
        IEnumerable<string> arguments,
        string fallback,
        CancellationToken cancellationToken) =>
        DeviceSuccessAsync(tvIp, debugPort, new[] { "shell" }.Concat(arguments), fallback, cancellationToken);

    private static string Endpoint(string tvIp, int port) => $"{tvIp.Trim()}:{port}";

    private static void ValidateIp(string tvIp)
    {
        if (!System.Net.IPAddress.TryParse(tvIp.Trim(), out _))
        {
            throw new ArgumentException("请输入有效的电视 IP 地址，例如 192.168.1.104。");
        }
    }

    private static void ValidatePort(int port, string paramName)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(paramName, "Port must be between 1 and 65535.");
        }
    }

    private static string MapDeviceState(string state)
    {
        if (state.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "未授权";
        }

        if (state.Contains("offline", StringComparison.OrdinalIgnoreCase))
        {
            return "离线";
        }

        return state == "device" ? "已连接" : "连接失败";
    }

    private static string MatchValue(string input, string pattern) =>
        Regex.Match(input, pattern, RegexOptions.IgnoreCase).Groups[1].Value is { Length: > 0 } value
            ? value
            : "-";

    private static string? ParsePackagePath(string output) =>
        output.Split(Environment.NewLine)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            ?["package:".Length..];

    private static AppInfo? ParsePackageListLine(string line)
    {
        if (!line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var payload = line["package:".Length..];
        var separatorIndex = payload.LastIndexOf('=');
        if (separatorIndex <= 0 || separatorIndex >= payload.Length - 1)
        {
            return null;
        }

        return new AppInfo
        {
            ApkPath = payload[..separatorIndex],
            PackageName = payload[(separatorIndex + 1)..],
            AppLabel = payload[(separatorIndex + 1)..]
        };
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "app" : sanitized;
    }

    private static string TryInferPackageNameFromApkPath(string apkPath) =>
        Path.GetFileNameWithoutExtension(apkPath);

    private static bool VolumeControllerIsNull(string line) =>
        line.Contains("VolumeController(null", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(line, @"mVolumeController\s*=\s*null", RegexOptions.IgnoreCase);
}
