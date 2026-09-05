using System.Diagnostics;
using SonyVolumeGuiKillerPcManager.Models;

namespace SonyVolumeGuiKillerPcManager.Services;

public sealed class AdbRunner
{
    private readonly string _adbPath = Path.Combine(AppContext.BaseDirectory, "tools", "adb.exe");

    public event Action<string>? Log;

    public string AdbPath => _adbPath;
    public bool Exists => File.Exists(_adbPath);

    public async Task<AdbResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!Exists)
        {
            throw new FileNotFoundException($"未找到内置 ADB：{_adbPath}", _adbPath);
        }

        var args = arguments.ToArray();
        Log?.Invoke($"[CMD] adb {FormatArguments(args)}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        stopwatch.Stop();

        var result = new AdbResult(
            timedOut ? -1 : process.ExitCode,
            stdout,
            stderr,
            stopwatch.ElapsedMilliseconds,
            timedOut);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Log?.Invoke($"[OUT] {stdout}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Log?.Invoke($"[ERR] {stderr}");
        }

        Log?.Invoke(result.Success
            ? $"[SUCCESS] exit={result.ExitCode}, elapsed={result.ElapsedMs} ms"
            : $"[FAIL] exit={result.ExitCode}, elapsed={result.ElapsedMs} ms");
        return result;
    }

    private static string FormatArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(argument =>
            argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument));
}
