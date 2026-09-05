using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SonyVolumeGuiKillerPcManager.Models;

public sealed class AppInfo : INotifyPropertyChanged
{
    private bool _isSelected;

    public string AppLabel { get; set; } = "-";
    public string PackageName { get; set; } = string.Empty;
    public string ApkPath { get; set; } = "-";
    public string VersionName { get; set; } = "-";
    public string VersionCode { get; set; } = "-";
    public string InstallTime { get; set; } = "-";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string ShortApkPath =>
        ApkPath.Length <= 54 ? ApkPath : $"...{ApkPath[^54..]}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
