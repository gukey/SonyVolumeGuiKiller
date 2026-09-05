# Windows 管理端构建

.NET 8 SDK，Windows x64。主界面和帮助窗口采用 WPF，项目图以资源形式嵌入 EXE。

准备官方 Android platform-tools 的 adb.exe、AdbWinApi.dll、AdbWinUsbApi.dll，以及本项目的守护 APK 1.0.4 和自动 5555 辅助 APK 0.3.5-sony.1（构建见 ../adb-auto-enable/README.md）。PC 1.0.5 内嵌新版辅助 APK；不将这些二进制或授权密钥提交到 Git。

```powershell
dotnet publish SonyVolumeGuiKillerPcManager.csproj -c Release -o publish `
  -p:AdbToolsDir="C:/build-inputs/platform-tools" `
  -p:GuardApk="C:/build-inputs/SonyVolumeGuiKiller-1.0.4-debug.apk" `
  -p:AutoEnableApk="C:/build-inputs/adb-auto-enable-fix.apk"
```

输出 EXE 和 apk 子目录需一起分发。发布包另附图文使用说明。默认输入路径兼容原工作目录；其他环境请传入上述三个参数。

本次修改：中文窗口标题、连接字段名称、端口提示、离线图解帮助、项目功能图；调整连接区避免按钮与字段重叠。构建仅验证 UI 与打包，不会连接或修改电视。
