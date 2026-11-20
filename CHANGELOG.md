# MCP 0.7.5 / SafeBridge 0.6.7 — 2026-11-20

- 安装程序检测 ArcGIS Pro，为新运行目录生成匹配的配置，并可从空白 Codex 配置注册。
- 原生桥逐条恢复作业历史，保留可读记录和原有任务 ID，隔离损坏文件。
- 可从干净源码目录生成 Windows 发布包。原生二进制使用可移植的调试符号路径。

# MCP 0.7.4 / SafeBridge 0.6.6 — 2026-11-20

- 新增唯一值、分级设色和专题渲染读回。分级支持自然断点、等间距、分位数和手动断点。
- 工程关闭时更新工程身份。矢量 GP 在绑定前复核工程与地图，绑定范围限于提交时指定的目标。
- Windows 短暂锁定响应文件时，读取流程继续等待同一份响应。

# MCP 0.7.3 / SafeBridge 0.6.5 — 2026-11-20

- 新增 7 个栅格接口，支持元数据读取、地图加载、裁剪、重投影、重采样、光谱指数和分区统计。
- 栅格处理复用作业查询、取消和幂等机制。处理完毕后回读栅格网格及波段，无异常才按请求添加到目标地图。

# MCP 0.7.2 / SafeBridge 0.6.4 — 2026-11-20

新增只读属性查询，可按本地 Shapefile、File GDB 要素类或独立表路径读取字段及记录。支持分页，File GDB 支持按字段排序。汇总统计任务成功后可直接读取输出表。

# MCP 0.7.1 / SafeBridge 0.6.3 — 2026-11-15

响应采用独立的 4 MiB 上限。地图、图层、布局和布局元素支持分页。设置标注可指定既有标注类，并保留同一图层的其他类和筛选条件。

# MCP 0.7.0 — 2026-11-08
新增 `pro_launch`，可直接启动 Pro 和指定 `.aprx`。已运行时复用现有进程，分别核对原生桥接和实际工程路径。原生插件仍为 0.6.2，协议仍为 v2。

# 0.6.2 结构修订 1 — 2026-11-03
MCP入口缩减为启动文件，41工具按6个业务组拆分；统一注册表与依赖注入，严格校验不再依赖globals。客户端白名单从实际MCP注册表读取，运行文件改用统一清单，增加PythonOnly部署及复制失败恢复。整理当前README与架构维护说明。原生插件、协议、工具名和参数保持兼容；增加布尔值不能作为整数传入的显式校验。

# 0.6.2 — 2026-11-03
修复图例外框尺寸与内容范围混用，分别回读；等比缩放同步更新图例文字、符号块、间距和缩进，保留动态图例关联。修复验收等待条件：心跳连接后仍等待临时工程完成加载。42项真实MCP现场断言与PNG/PDF检查通过，5个新增接口当前聊天直接调用通过；研究工程未保存、指纹不变。详见artifacts/acceptance/layout-edit-20261102-2200。

# 0.6.1 — 2026-11-02
现场修正段落与点文字统一识别为CIMTextGraphicBase，支持现有段落说明框的文字和字号编辑。

# 0.6.0 — 2026-11-02
新增现有布局元素查询/单个与批量编辑、说明框文字创建、市名逐字独立摆放；按页面单位换算厘米坐标；批次预检与CIM恢复；重复名称/锁定/非法坐标显式拒绝。现场状态见独立验收记录。

# 0.5.0 — 2026-11-02
新增基础制图8接口；按指定研究区范围自动选版、原生布局和PNG/PDF导出；导出复用后台任务及新输出策略。修复客户端白名单生成逻辑，部署时保留既有配置。现场出图验收状态单独记录，不能用编译通过替代。

# CHANGELOG

## 0.4.0 — 2026-10-17

### 新增

- 目录驱动通用 GP 执行引擎：`GpCatalog.cs`（纯逻辑 schema/参数/位置参数装配）+ `GpOperations.cs`（通用执行、输出校验、地图绑定）。C# 不再为单个工具写分支。
- `gp_catalog.json` v2：27 个矢量 GP 工具（叠加、邻近、融合、转换、属性统计、属性/位置选择）。
- Python MCP：新增 `pro_gp_tool_info`、`pro_buffer`、`pro_dissolve`、`pro_project`、`pro_intersect`、`pro_union`、`pro_erase`、`pro_spatial_join`、`pro_select_by_attribute`、`pro_select_by_location`、`pro_feature_to_point`、`pro_merge`、`pro_statistics`；`pro_run_gp` 语义扩展为任意目录工具。
- 多输出 GDB 结果（`conversion.FeatureClassToGeodatabase`）枚举、描述与逐图层绑定；表输出（`analysis.Statistics`）描述与计数；选择类结果返回 `selected_count`。
- DSH（DeepSeek Harness）接入：`@deepseek-ai/dsh-mcp-client` stdio 配置，工具以 `mcp__arcgispro__*` 暴露，配置变更自动重连。
- 文档：`docs/DSH-INSTALL.md`、`docs/GP-CATALOG.md`、`README.md` 重写、`tests/smoke_gp.py` 全目录冒烟。

### 变更

- GP 提交在做完 MCT 预检后立即返回；几何/坐标系/字段/图层错误恢复为**提交即失败**，不再先受理任务再失败。
- 输出路径统一为 `<output_root>/<job_id>/result.gdb/<output_name>`；输入只读、`overwriteoutput=false`。
- 图层与空间参考 GP 值在 MCT 创建（修复选择类工具线程亲和性错误）。
- 原生版本 0.3.1 → 0.4.0（add-in、heartbeat、version 响应、诊断日志同步）。
- `pro_list_gp_tools` 返回精简 schema（省略空字段），并新增单工具查询。

### 修复

- 0.3.1 遗留：DSH/Codex 配置丢失后 Pro 插件安全停用，恢复 `bridge.config.json` 即自动连接。
- `FeatureClassToGeodatabase` 输出 GDB 校验误用要素类路径解析的问题。
- MCP 适配层类型校验：`list[str]`、`str|None`、数字参数接受整数（`distance: 2`）。

### 验证

- 离线：Python 14 项单元测试、Native CoreChecks 86 条断言（含目录 schema/参数计划）、StartupChecks 7 条。
- 独立真实 stdio 验收：108 项断言 / 120 次调用，覆盖 27 个目录工具与错误路径（`artifacts/acceptance/dsh-20261017-1805/`）。
- 独立 ArcPy 几何回读 17 项：Buffer 面积 181.563（理论 181.699，圆弧离散化）、Union 175.000、Clip 长度 16、Erase 长度 20、SpatialJoin 长度 26、Merge 长度 46、Project WKID 4326、统计表 `SUM_test_id=6` 等。
- DSH 当前会话直接调用：`bridge_status`、`pro_map_state`、`pro_gp_tool_info`、`pro_buffer`、`pro_project`、`pro_select_by_attribute`、`pro_job_status` 全部成功并可见于地图。
- 使用合成验收工程完成验证，核对了源工程 SHA-256。

## 0.3.1 — 2026-10-17

- 启动修复：配置缺失/损坏时不向 Pro 生命周期抛异常，桥接安全停用并记录 `bridge-lifecycle.jsonl`。
- 保持 15 工具面与 v2 协议；见 `artifacts/acceptance/20261017-163929/`。
