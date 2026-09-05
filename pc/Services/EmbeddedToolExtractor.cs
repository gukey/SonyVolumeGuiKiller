using System.Reflection;
using System.Security.Cryptography;

namespace SonyVolumeGuiKillerPcManager.Services;

public static class EmbeddedToolExtractor
{
    private static readonly (string ResourceName, string FileName)[] Tools =
    [
        ("EmbeddedTools.adb.exe", "adb.exe"),
        ("EmbeddedTools.AdbWinApi.dll", "AdbWinApi.dll"),
        ("EmbeddedTools.AdbWinUsbApi.dll", "AdbWinUsbApi.dll")
    ];

    public static void EnsureAdbTools()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(toolsDirectory);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var tool in Tools)
        {
            using var resource = assembly.GetManifestResourceStream(tool.ResourceName)
                ?? throw new InvalidOperationException($"缺少内嵌资源：{tool.ResourceName}");
            var destination = Path.Combine(toolsDirectory, tool.FileName);

            if (File.Exists(destination) && StreamsMatch(resource, destination))
            {
                continue;
            }

            resource.Position = 0;
            var temporaryPath = $"{destination}.{Environment.ProcessId}.tmp";
            try
            {
                using (var output = new FileStream(
                           temporaryPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None))
                {
                    resource.CopyTo(output);
                    output.Flush(true);
                }

                File.Move(temporaryPath, destination, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static bool StreamsMatch(Stream embeddedResource, string destination)
    {
        using var existingFile = File.OpenRead(destination);
        var embeddedHash = SHA256.HashData(embeddedResource);
        var existingHash = SHA256.HashData(existingFile);
        return embeddedHash.AsSpan().SequenceEqual(existingHash);
    }
}
