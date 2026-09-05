namespace SonyVolumeGuiKillerPcManager.Models;

public sealed record AdbResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    long ElapsedMs,
    bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StdOut, StdErr }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
