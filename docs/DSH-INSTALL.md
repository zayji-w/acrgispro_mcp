# 在 DeepSeek Harness 中接入 acrgispro_mcp

先[安装 acrgispro_mcp 和 SafeBridge](INSTALL.md)，然后在实际使用的 profile 中配置 `@deepseek-ai/dsh-mcp-client`。桌面 profile 的 patch 文件通常位于 `%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml`。多 profile 安装可通过 `DSH_PROFILE_DIR` 确认实际位置。

在顶层数组添加以下 `insert` 条目。将 `<RUNTIME_ROOT>` 替换为安装结果中 `runtime` 的绝对路径。示例用正斜杠表示 Windows 路径。

```yaml
- insert:
    - id: mcp-arcgispro
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: arcgispro
        transport: stdio
        command: "<RUNTIME_ROOT>/.venv/Scripts/python.exe"
        args: ["<RUNTIME_ROOT>/server.py"]
        cwd: "<RUNTIME_ROOT>"
        toolCallTimeoutMs: 120000
        failOnStartupError: false
        reconnect:
          enabled: true
```

打开 ArcGIS Pro 和目标工程。保存 profile，等待 DSH 重新加载或重新启动客户端。在会话中调用 `bridge_status`，检查 `connected: true`、`backend: native-sdk`、`protocol_version: 2`。随后调用 `pro_map_state` 确认当前工程与地图。

| 现象 | 处理 |
|---|---|
| 工具未出现 | 核对正在使用的 profile、插件名称、Python 和服务路径，再查看 DSH 日志。 |
| `NOT_CONNECTED` | 确认 Pro 正在运行，并在配置所在目录检查 `bridge-lifecycle.jsonl`。 |
| `REQUEST_OUTCOME_UNKNOWN` | 重新查询；若重试同一个地理处理提交，继续使用原 `idempotency_key` 和相同参数。 |
| `BUSY` | 等当前任务进入终态，再提交后续任务。 |

移除这条 `insert` 配置即可断开此客户端的接入。
