# 在 Windows 安装 acrgispro_mcp

已测试的组合为 ArcGIS Pro 3.7 x64 和 Python 3.12 x64。安装脚本需在 PowerShell 7 中运行。ArcGIS Pro、MCP 客户端和本机插件需在同一个已登录的 Windows 桌面会话中运行。

## 从发布包安装

下载 GitHub Releases 中的 `acrgispro_mcp-0.7.5-windows.zip`。可将其中 `acrgispro_mcp.esriAddinX` 的哈希与包内的 `SHA256SUMS.txt` 对照。

```powershell
Get-FileHash -Algorithm SHA256 .\acrgispro_mcp.esriAddinX
Get-Content .\SHA256SUMS.txt
```

保存当前工程并关闭 ArcGIS Pro。在解压目录运行安装命令。

```powershell
pwsh -File scripts/install.ps1
```

安装脚本使用 `requirements.lock` 准备独立的 Python 3.12 环境。运行目录和用户配置的绝对路径分别见安装结果的 `runtime` 和 `config`。使用 `-DryRun` 可以提前查看这两个路径。新安装的状态及输出目录在运行目录下。已有配置时沿用其中的运行目录。显式指定 `-ArcGISProExe` 会更新配置中的 Pro 可执行文件路径。

| 参数 | 用途 |
|---|---|
| `-PythonExe <path>` | 指定 Python 3.12 的可执行文件。 |
| `-RuntimeRoot <path>` | 为新安装设置运行目录。已有配置时，使用其中记录的运行目录。 |
| `-ArcGISProExe <path>` | Pro 使用自定义安装位置且无法自动识别时，指定 `ArcGISPro.exe`。 |
| `-PackagePath <path>` | 安装独立下载的 `.esriAddinX`，或指定本地构建的插件包。 |
| `-RegisterCodex` | 将 MCP 服务登记为 Codex 中的 `arcgispro`。 |
| `-DryRun` | 显示安装计划。 |

## 配置 MCP 客户端

使用 stdio 传输。下面的 `<RUNTIME_ROOT>` 替换为实际运行目录的绝对路径。JSON 中可使用正斜杠书写 Windows 路径。

```json
{
  "mcpServers": {
    "arcgispro": {
      "command": "<RUNTIME_ROOT>/.venv/Scripts/python.exe",
      "args": ["<RUNTIME_ROOT>/server.py"]
    }
  }
}
```

Codex 可在安装时加 `-RegisterCodex`。使用 DeepSeek Harness 时，按[对应客户端配置](DSH-INSTALL.md)填写 profile。重新连接 MCP 服务后，在 Pro 中打开目标工程并调用 `bridge_status`。连接正常时会返回 `connected: true`、`backend: native-sdk` 和 `protocol_version: 2`。随后调用 `pro_map_state`，核对返回的工程和地图。

桥接初始化遇到问题时，在配置所在目录检查 `bridge-lifecycle.jsonl`。如果修改了配置，重新打开 Pro，或使用 Pro 的 Add-In 选项卡启动桥接。

## 从源码构建或更新

源码构建需安装.NET 10 SDK。构建脚本通过 Windows 注册表定位 Pro 安装目录，也可用 `-ProInstallDir <path>` 指定。

```powershell
pwsh -File scripts/build.ps1
pwsh -File scripts/install.ps1
```

已部署的用户在构建后可运行 `pwsh -File scripts/deploy.ps1 -Apply -RuntimeRoot "<现有运行目录>"`。加 `-PythonOnly` 可只更新 Python 层。部署命令省略 `-Apply` 时执行预检。更新原生插件前保存并关闭 Pro。更新 Python 层后重新连接客户端。
