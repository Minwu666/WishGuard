# 攒愿 · WishGuard

**把期待，留给值得的相遇。**

一个 Windows 本地防冲动祈愿工具：识别《原神》祈愿页面后，用独立窗口遮挡抽卡区域；自动统计资源，让攒抽计划更容易坚持。

[![Windows build](https://github.com/Minwu666/WishGuard/actions/workflows/build.yml/badge.svg)](https://github.com/Minwu666/WishGuard/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> 当前是 **0.2.1 源码原型**，还没有面向所有电脑验证过的安装包。项目与米哈游无关联，未获官方授权，不能保证零封号风险、零漏拦或无法绕过。

## 能做什么

- **不依赖祈愿快捷键**：在可见画面中识别祈愿按钮，再显示遮挡。
- **本地 OCR 统计**：读取原石和纠缠之缘，连续两次读数一致才更新；不计相遇之缘。
- **600 抽计划**：`纠缠之缘 + floor(原石 / 160)` 达到 600 后，由你选择的可信核验人核验，签发永久解除凭证。
- **跟随游戏启停**：游戏启动时开启识别，游戏退出后关闭主程序与 OCR；轻量启动器等待下一次启动。
- **永久解除后停止联动**：移除自己的登录启动项，启动器也退出。
- **可以反悔的冷静期**：申请提前结束后等待 24 小时；可随时撤销，重新申请会重新计时。

本项目不注入游戏、不读写游戏内存、不修改游戏文件、不发送游戏按键。截图和 OCR 在本机内存中处理，不联网、不上传。

## 先体验模拟界面

需要 Windows 10 2004 或更新版本，以及 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。
实际识别还需要 Windows 简体中文 OCR 组件。

```powershell
git clone https://github.com/Minwu666/WishGuard.git
cd WishGuard
dotnet build src/WishGuard.csproj -c Release -o artifacts/app
.\artifacts\app\WishGuard.exe --demo
```

模拟模式不需要公钥，不会监视真实游戏、不会消费资源，也不会设置登录启动。

## 正式使用前：配置可信核验人

**公开源码不提供中央核验服务。作者、GitHub Issues 和任何特定聊天都不负责给所有用户解锁。**
你可以请可信的朋友保管签发私钥，自己只安装对应的验签公钥。每位用户应使用独立部署，避免共享同一套核验密钥。

核验人在自己的电脑上操作：

```powershell
python -m pip install -r tools/requirements.txt
python tools/authority.py init --private-dir .private/reviewer --public-output authority-public.txt
```

核验人保存 `.private/reviewer/issuer-private.pem`，仅把 `authority-public.txt` 交给使用者。
使用者将公钥放到 `src/authority-public.txt`，重新构建，再运行 `artifacts/app/WishGuard.exe`。
这些文件默认被 Git 忽略，仓库不包含任何实际部署密钥。

启用前，请阅读 [使用说明](docs/usage.zh-CN.md) 和 [核验人指南](docs/reviewer.zh-CN.md)。
现有安装不要随意更换公钥，更换会使旧凭证无法验证。

## 使用与限制

1. 用简体中文、键鼠、窗口化或无边框模式启动游戏。
2. 在攒愿中选择是否跟随游戏，点击“开启 600 抽守护”。
3. 打开限定祈愿页后停手，确认遮挡出现；点击游戏原有的右上角 `×` 返回。
4. 达标后导出申请，把申请和当前资源截图私下交给你的核验人；核验通过后导入凭证。

需要理解的边界：

- 识别有延迟，连续快速操作、新弹窗、游戏更新、HDR/DPI/分辨率变化可能导致漏拦。
- 手柄、云游戏、独占全屏、其他语言未支持；遮挡没取得焦点时，键盘可能仍进入游戏。
- 不会禁止任务管理器、锁住电脑或让管理员无法卸载。它的目标是增加冲动操作的成本。
- 游戏关闭时仍有轻量进程检查进程名称，才可发现下次启动；这时不截图、不运行 OCR。
- 当前阈值固定为 600 抽；角色复刻条件、动态强度判断、多用户服务尚未实现。

## 开发与验证

```powershell
# 不依赖 OCR 语言包的规则与持久化检查（报告明确标记跳过 OCR）
.\artifacts\app\WishGuard.exe --self-test --skip-ocr --data test-results

# 已安装简体中文 OCR 的本机完整检查
.\artifacts\app\WishGuard.exe --self-test --data test-results-ocr
```

测试程序是 Windows GUI 可执行文件。自动化脚本应使用 `Start-Process -Wait -PassThru` 等待退出并检查退出码及 `results.json`。
不要把跳过 OCR 的 CI 绿灯理解成实机拦截通过。验证范围见 [测试说明](docs/testing.md)。

```text
src/              Windows Forms 客户端、OCR 与规则
tools/            核验人密钥生成与凭证签发工具
docs/             使用、核验、测试说明
.github/workflows Windows 构建与规则检查
```

欢迎提交可复现的问题和改进。请先阅读 [贡献说明](CONTRIBUTING.md)，不要在公开 Issue 上传 UID、个人状态文件、凭证或密钥。

## 许可证

[MIT](LICENSE)。项目不包含游戏美术、角色立绘、实际账号截图或游戏客户端文件。
《原神》及相关商标属于其权利人。
