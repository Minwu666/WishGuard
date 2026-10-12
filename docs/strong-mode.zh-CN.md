# 强制模式（0.3.0）

这是实验功能，输入兼容性检查仍有未通过项，目前不能作为可靠的强制拦截保证。请结合[验证范围](testing.md)评估是否适合自己。

适合明确希望增加自我约束的使用者。**保护中断或失效时，会直接结束已登记的原神进程。** 请先退出游戏再安装；它也可能因识别、系统故障而中断游戏会话。安装必须得到电脑使用者同意。

**安装前，先在原神内切换为窗口模式并退出游戏。** 独占全屏尚未支持；此前测试出现过反复最小化。无边框模式已经在一台电脑上验证正常游玩、祈愿遮挡及返回，但仍需在自己的电脑上另行验证，首次使用建议窗口模式。若出现反复最小化，请恢复窗口模式后再启动游戏。

确认进入游戏后，切换到其他应用时程序会尝试收起原神；加载阶段不自动收起。隔离测试中收起与恢复正常，真实游戏存在兼容差异：本机实测祈愿遮挡期间切出和最小化受限，使用者选择保留当前行为。如需切换应用，可先按 Esc 或单击右上角 × 返回普通游戏画面。

切回游戏时可能短暂显示“正在确认游戏画面”。确认普通画面后自动恢复；发现祈愿时继续遮挡。遮挡期间可按 Esc 或单击游戏右上角 × 返回。正常游戏退出后不运行 OCR。600 抽达标核验后的永久解除，以及可撤销的 24 小时冷静期，仍通过主界面办理。

仍是本地视觉工具，无法承诺零漏拦、无法绕过或零封号风险。它不隐藏系统进程、不禁用任务管理器、不安装驱动、不修改游戏文件；退出处理仅针对登记路径和登记用户的原神，不终止其他软件或进程树。

## 安装

先按主文档配置核验公钥，正常启用 600 抽计划。使用 .NET 9 SDK 构建**普通 Release**；不能把集成测试构建用于正式安装。

```powershell
dotnet build src/WishGuard.csproj -c Release -p:WishGuardTestHooks=false -t:Rebuild -o artifacts/strong-package/Client
dotnet build enforcer/WishGuard.Enforcer.csproj -c Release -o artifacts/strong-package/Enforcer
```

退出原神。在管理员 PowerShell 中调用安装器，替换路径和使用者 SID。当前窗口兼容组件需要实际游玩账户为可提权的管理员账户；以另一个管理员账户代装到标准账户的场景尚未支持。

```powershell
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
./tools/Install-StrongMode.ps1 `
  -BundleDirectory "$PWD/artifacts/strong-package" `
  -InstallDirectory 'D:/Apps/WishGuard' `
  -DataDirectory "$env:LOCALAPPDATA/WishGuard" `
  -GamePath 'D:/Games/Genshin Impact Game/YuanShen.exe' `
  -OwnerSid $sid -AcceptGameExit `
  -ReportPath "$PWD/artifacts/install-result.json"
```

安装器保留原计划和公钥，为旧安装建立备份，并配置保护组件。原目录的程序仍可直接打开。安装失败会尝试恢复原任务和原程序，结果写入报告；不要在失败状态下开始游戏。
普通主界面和游戏等待程序仍使用普通权限；窗口兼容组件仅服务于已登记的游戏及通过验证的攒愿窗口，不执行任意命令。计划完成后，它也会退出并移除自动启动。

如果原神暂时卸载了，并且使用者明确确认以后安装到同一位置，可增加 `-AllowGameNotInstalled` 提前安装；真实游戏测试需要重装后另行进行。

完成计划后由程序停止保护并移除自身启动任务。不要向项目仓库上传自己的计划文件、密钥、核验材料或截图。

## 验证范围

自动检查覆盖：普通画面恢复、祈愿遮挡、切换和最小化、保护中断、模拟游戏退出、无关进程不受影响、冷静期申请及撤销、无效凭证拒绝、保存失败回滚、永久解除后停止保护。

集成测试使用独立临时计划和模拟游戏，不使用真实原神、真实核验私钥或实际安装任务。窗口及 OCR 测试需要可交互的 Windows 桌面和简体中文 OCR，不能由无桌面的 CI 结果替代。
