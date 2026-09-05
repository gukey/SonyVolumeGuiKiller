# SonyVolumeGuiKiller

索尼 Android TV 音量 GUI 守护应用。此仓库从本地 `02_电视App源码` 导入电视端源码，版本 1.0.4（高于真机已有的 1.0.3）；不包含 PC 客户端、自动开启 ADB 的独立应用或授权密钥。

## 本次稳定性修复

- ADB 操作最多持续 12 秒，超时强制关闭 socket，覆盖持续收包、认证循环、shell 不结束和写入阻塞；服务退出时也会主动关闭连接。
- 限制 ADB 包长和 shell 输出，避免异常数据导致大量内存分配。
- 工作线程串行执行，合并音量/SystemUI 事件；执行中的事件保留一次补查，避免排队失控或直接丢弃。
- 成功后每 5 秒复查，失败后按 1/2/4/8 秒退避重试；电视亮屏、解锁时立即检查。
- 开机广播立即返回，由持久化 JobScheduler 任务处理开机、升级及约 15 分钟一次的后台兜底检查，移除原来约 190 秒的 goAsync 挂起。
- 不再覆盖 enabled_accessibility_services，不再自动启用其他应用的无障碍服务，尊重用户主动关闭。
- 页面显示连接状态、最近成功时间和错误，提供“立即检查 / 重试”。

## 适用条件与限制

需要先启用本应用的无障碍服务，并将电视已授权的 `adbkey` 和 `adbkey.pub` 放入应用私有 files 目录。沿用本地回环 ADB 端口 5555、45647、33821、65000。端口关闭或授权撤销时会重试，但普通应用不能自行开启 adbd 或批准自己的授权。

`service call audio 97 null` 是沿用旧版本的索尼固件相关调用，并非稳定 Android API。仅在 dumpsys 验证控制器为空时报告成功；系统升级后若事务编号改变，需要重新核对固件，不能通过任意试探其他编号处理。

系统完全解除无障碍绑定时，后台任务可临时清除 GUI，但不能代替每 5 秒的守护；请在无障碍设置中关闭再开启本服务，无需重启电视。系统任务可能被待机策略延后；强行停止应用后需用户再次打开。此版本不承诺绕过系统的强行停止或权限撤销。

## 构建与验证

JDK 17、Android SDK 35：

```powershell
./gradlew.bat testDebugUnitTest assembleDebug lintDebug
```

输出 `app/build/outputs/apk/debug/app-debug.apk`。debug 构建支持原有 PC 客户端通过 run-as 写入 key。签名必须与电视上原 APK 一致才能直接覆盖；不要为绕过签名错误直接卸载而丢失授权文件。

测试覆盖正常 shell、超时后重新连接、持续收包不结束、负数/超大包长、输出上限、取消阻塞读取、控制器结果识别。真机还需检查长时间运行、连续音量键、待机唤醒、ADB 中断后恢复和服务重新连接。

## 实现依据

Android 官方要求广播快速完成，延后的后台工作交给系统任务：
[BroadcastReceiver](https://developer.android.com/reference/android/content/BroadcastReceiver)、
[JobService](https://developer.android.com/reference/android/app/job/JobService)。
无障碍的绑定与反馈中断按 [AccessibilityService](https://developer.android.com/reference/android/accessibilityservice/AccessibilityService) 生命周期处理。
