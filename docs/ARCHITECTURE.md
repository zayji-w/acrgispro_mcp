# 架构与维护

acrgispro_mcp 由 stdio MCP 服务和 ArcGIS Pro 原生插件组成。两端通过本机目录的原子 JSON 文件交换请求、响应和心跳，目前使用协议 v2。Python 在 Windows 桌面会话内启动 Pro，按进程 ID 匹配原生桥接，并核查实际工程。Pro 可以在客户端断开后继续运行。

```text
MCP 客户端 → server.py → gis_agent/app.py
                           ↓
                    registry.StrictMCPServer
                           ↓
                  gis_agent/tools/*.py
                           ↓
                       BridgeClient
                           ↓
                    本机 JSON 文件队列
                           ↓
                  Native/BridgeService.cs
                           ↓
                 ArcGIS Pro SDK 和任务管理器
```

`gis_agent/registry.py` 注册有类型标注的工具并拒绝重复名称。Python `contracts.py` 和原生 `Native/Contracts.cs` 各自校验进程边界上的参数。矢量 GP 由 `gp_catalog.json` 定义工具、参数和输出。新增业务模块需由 `gis_agent/app.py` 显式注册。新增运行文件需列入 `runtime-manifest.json`。

## 检查

按[安装说明](INSTALL.md)准备 ArcGIS Pro 和 Python 3.12。开发检出可以在根目录创建独立 Python 环境再运行检查。原生检查另外需要 .NET SDK。

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.lock
pwsh -File scripts/verify.ps1
```

离线检查覆盖 Python 参数与转发、C# 纯逻辑、启动检查、部署校验与回退。ArcGIS Pro 内的地图读回、导出和 GP 结果需要在独立测试工程中验证。

## 发布与回退

用 `scripts/build.ps1` 构建原生插件，然后用以下命令导出公开源代码并可选生成安装 ZIP。指定新的空导出目录和 ZIP 路径。

```powershell
pwsh -File scripts/package-release.ps1 -SourceDirectory "<导出目录>" `
    -AddinPath "<构建后的 acrgispro_mcp.esriAddinX>" -ReleaseZip "acrgispro_mcp-0.7.5-windows.zip"
```

只导出源代码时省略 `-AddinPath` 和 `-ReleaseZip`。公开源代码包含运行文件、构建/安装/验证脚本、文档、离线测试和合成协议样例。安装 ZIP 还提供原生插件和 SHA-256 文件清单。

`scripts/deploy.ps1` 校验来源文件并备份本次部署要写入的目标，文件复制失败会恢复。指定 `-Rollback -Apply -BackupDirectory <备份目录>` 可从指定记录回退。如果部署时指定过 `-RuntimeRoot` 或 `-ConfigPath`，回退时使用同样的参数。完整的原生部署与回退均需要先关闭 Pro。
