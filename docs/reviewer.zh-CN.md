# 核验人指南

使用者应选择一位愿意协助核验的可信朋友。项目不提供在线审批服务；签发工具不读取游戏账号，也不会自动判断角色强度或决定是否批准放行。

## 生成独立密钥

在核验人自己的电脑上安装 Python 和 `tools/requirements.txt` 中的依赖，然后运行：

```powershell
python tools/authority.py init --private-dir .private/reviewer --public-output authority-public.txt
```

工具生成 ECDSA P-256 私钥与 Base64 SPKI 公钥。私钥保存在核验人电脑，只把公钥交给使用者。
已有文件不会被覆盖。私钥以未加密 PEM 保存，应放在仅核验人可以访问并有可靠备份的位置；不要共享、提交 Git 或放进客户端包。
不同使用者建议分别生成密钥目录。

## 600 抽永久解除

收到使用者主动提交的申请 JSON 和当前资源截图后：

1. 确认截图清楚显示原石及纠缠之缘，不是相遇之缘。
2. 计算 `纠缠之缘 + floor(原石 / 160)`，确认至少 600 抽。
3. 确认截图数值与申请相同；模糊、过时或不一致时先重新核验。
4. 在自己的电脑上签发，示例数字仅作演示：

```powershell
python tools/authority.py issue --key .private/reviewer/issuer-private.pem --request evidence/request.json --primogems 96000 --intertwined 0 --evidence evidence/resources.png --reviewed --output evidence/release.wishpass
```

`--reviewed` 表示操作者已经人工查看证据；程序不会验证图片真伪，也不会自动理解截图。
申请与资源观察时间须在最近 24 小时内。签发的凭证须在七天内导入；导入后永久生效。
核验人与安装的对应关系由公钥决定；凭证还绑定安装 ID、计划 ID 和申请随机数。

## 一次性 30 分钟临时放行

临时放行由核验人单独审批，适合双方预先约定的限时例外。核验人应了解本次申请的原因，明确决定是否批准，并写下审批理由。是否因为角色推出或复刻而批准，应由核验人判断，程序不会自动评级或代为批准。

临时申请仍绑定原来的 600 抽永久解除计划。它不要求达到 600 抽，也不要求提交资源截图；使用者应提交新导出的申请 JSON，申请时间须在最近 24 小时内。旧资源观察时间不影响这次临时审批。

核验人在自己的电脑上使用独立的临时签发命令。下面的理由仅作示例，应换成核验人实际批准的理由：

```powershell
python tools/authority.py issue-temporary --key .private/reviewer/issuer-private.pem --request evidence/request.json --reviewed --reason "已核对本次限时申请，同意按双方约定体验三十分钟。" --output evidence/temporary.wishpass
```

这里的 `--reviewed` 表示核验人已经审阅并批准本次例外；`--reason` 必须单独填写，去掉前后空白后为 1～500 个字符。理由会进入签名，不能用使用者申请中的备注代替核验人审批。临时命令的时长固定为 30 分钟。

临时凭证须在签发后的 24 小时内导入。首次成功导入后立即连续计时 30 分钟，每张凭证只能消费一次；关闭游戏或程序不会暂停，重新导入同一张凭证不会重新计时。请在准备开始使用时再导入。

计时结束后守护恢复，600 抽计划仍然保留。临时凭证不会永久解除计划，也不会移除随游戏启动的守护。永久解除继续使用上面的 `issue` 命令和 600 抽核验流程。

若核验人把私钥交给使用者、使用者能修改本地程序或状态，约束就可能失效。这是行为上的协助，不是抗管理员的安全系统。
