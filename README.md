# acrgispro_mcp

acrgispro_mcp 通过 MCP 操作当前 Windows 桌面会话中的 ArcGIS Pro。Python 服务负责 MCP 接入、参数校验和本机通信。SafeBridge 原生插件通过 ArcGIS Pro SDK 执行 GIS 操作。

当前版本为 0.7.5，原生插件版本为 SafeBridge 0.6.7。测试环境为 Windows 上的 ArcGIS Pro 3.7 x64 和 Python 3.12。项目提供 53 个 MCP 工具，矢量地理处理目录包含 27 个工具。

## 安装

从 GitHub Releases 下载并解压发布 ZIP。保存并关闭 ArcGIS Pro，在解压目录运行以下命令。安装后按文档配置客户端，再打开 Pro 和目标工程。

```powershell
pwsh -File scripts/install.ps1
```

[安装、更新与客户端配置](docs/INSTALL.md) · [DeepSeek Harness 接入](docs/DSH-INSTALL.md)

## 使用

1. 调用 `bridge_status` 确认原生桥接已连接。需要启动 Pro 时调用 `pro_launch`，也可在冷启动时传入本地 `.aprx` 路径。已运行时返回实际进程和工程的检查结果。
2. 调用 `pro_map_state` 获取当前工程的 `project_id` 和目标地图的 `map_id`。可用 `limit` 和 `offset` 分页读取，后续页传入首屏的 `project_id`。
3. 运行矢量地理处理前，用 `pro_list_gp_tools` 和 `pro_gp_tool_info` 确认工具及参数。提交后用 `pro_job_status` 检查 `job_id` 对应的结果和地图绑定。提交超时且未取得 `job_id` 时，用相同参数和 `idempotency_key` 重试同一请求。
4. 使用 `pro_layer_presentation` 读取已有符号和标注状态。更新标注时可通过 `class_name` 指定已有标注类，其他类的定义保留。省略该参数时编辑首个标注类，`visible` 控制整个图层的标注显示。

写盘的处理结果放在任务专用的输出目录。将结果加到地图时，桥接会核对提交时的目标工程和地图。数据路径须指向本机可访问的文件，地图和布局编辑由用户在 Pro 内保存。请求上限为 64 KiB，响应上限为 4 MiB。

| 功能 | 文档 |
|---|---|
| 矢量地理处理和选择 | [GP 目录](docs/GP-CATALOG.md) |
| 读取字段、记录和统计输出 | [属性表](docs/TABLE-READ.md) |
| 栅格裁剪、重投影、重采样、指数和分区统计 | [栅格工具](docs/RASTER.md) |
| 唯一值和分级设色 | [专题渲染](docs/THEMATIC.md) |
| 布局创建、图层样式和 PNG/PDF 导出 | [制图与导出](docs/CARTOGRAPHY.md) |
| 现有图例和文字元素编辑 | [布局编辑](docs/LAYOUT-EDITING.md) |

## 开发

Python 入口为 `server.py`，MCP 工具位于 `gis_agent/`，ArcGIS Pro 插件源码位于 `Native/`。默认配置和运行文件清单位于仓库根目录。有关源码构建、离线检查及发布流程，参见[架构与维护](docs/ARCHITECTURE.md)。

本项目源码采用 [MIT 许可证](LICENSE)。运行需要用户自行安装并许可 ArcGIS Pro，Esri 软件及 SDK 按其各自条款使用。
