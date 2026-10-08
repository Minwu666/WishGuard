# 核验人指南

使用者应选择一位愿意协助核验的可信朋友。仓库不提供在线服务，也不会自动判断角色强度或读取账号资源。

## 生成独立密钥

在核验人自己的电脑上安装 Python 和 `tools/requirements.txt` 中的依赖，然后运行：

```powershell
python tools/authority.py init --private-dir .private/reviewer --public-output authority-public.txt
```

工具生成 ECDSA P-256 私钥与 Base64 SPKI 公钥。私钥保存在核验人电脑，只把公钥交给使用者。
已有文件不会被覆盖。私钥以未加密 PEM 保存，应放在仅核验人可以访问并有可靠备份的位置；不要共享、提交 Git 或放进客户端包。
不同使用者建议分别生成密钥目录。

## 核验与签发

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

若核验人把私钥交给使用者、使用者能修改本地程序或状态，约束就可能失效。这是行为上的协助，不是抗管理员的安全系统。
