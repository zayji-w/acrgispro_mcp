# GP 目录 v2 参考与扩展指南

`gp_catalog.json` 是地理处理能力的唯一事实源。原生端 `GpOperations` 不含任何工具名分支：新增一个可执行工具 = 在 `tools` 下增加一个条目；Python 端可选再加一个类型化封装（不必要，`pro_run_gp` 已能执行目录内任意工具）。

## 1. 顶层结构

```json
{
  "catalog_version": 2,
  "runtime": { "max_parameters": 24, "max_inputs": 8, "default_attach": true },
  "tools": { "<tool name>": { ... } }
}
```

- `catalog_version` 必须为 `2`；原生端启动时校验失败会以 `CAPABILITY_UNAVAILABLE` 安全停用 GP。
- 工具名必须匹配 `^(analysis|management|conversion|dataManagement)\.[A-Za-z][A-Za-z0-9_]{0,63}$`，并且必须等于 ArcGIS Pro `Geoprocessing.ExecuteToolAsync` 可识别的工具名（如 `analysis.Buffer`）。

提交任务时传入 `project_id` 和 `map_id`。目录输出绑定前，会复核提交时的工程和地图。如果工程已切换或目标地图已失效，已写出的结果仍可在任务输出目录读取。用 `pro_job_status` 查看 `map_binding` 和警告。

## 2. 工具条目

```json
"analysis.Buffer": {
  "label": "缓冲 Buffer",
  "category": "proximity",
  "risk": "write_new_output",
  "parameter_order": ["in_features","out_feature_class","buffer_distance_or_field","line_side","line_end_type","dissolve_option","dissolve_field","method"],
  "params": { ... },
  "arguments": [ ... ],
  "output": { "kind": "feature_class", "name_param": "output_name", "default_name": "buffer" },
  "environment": { "output_crs_from": "input_path" },
  "requires_same_gcs": false
}
```

| 字段 | 必填 | 含义 |
|---|---|---|
| `label` / `category` | 否 | 展示与分组 |
| `risk` | 是 | `write_new_output`（只新建输出）或 `read_only`（选择类）。会原地改写/删除数据的工具不允许登记 |
| `parameter_order` | 否 | **文档用途**：记录 ArcGIS 工具的位置参数顺序，便于审计与对照 ArcPy 签名；执行时以 `arguments` 为准 |
| `params` | 是 | 命名参数 schema，见 §3 |
| `arguments` | 是 | 位置参数装配计划，见 §4 |
| `output` | 是 | 输出类型与命名 |
| `environment` | 否 | `output_crs_from`：把哪个输入数据集的坐标系作为 GP 环境输出坐标系（`null` 表示不设置） |
| `requires_same_gcs` | 否 | 多输入工具是否要求相同地理坐标系；不同则提交即报 `DATUM_TRANSFORM_REQUIRED` |

### output.kind

| 值 | 含义 | 校验与绑定 |
|---|---|---|
| `feature_class` | 单要素类输出 | 描述 + 计数 + 加到目标地图 |
| `table` | 单表输出 | 描述 + 计数，不绑图层 |
| `feature_classes` | 输出是一个新建 GDB，内含多个要素类 | 枚举 GDB 内全部要素类，逐个描述并绑定 |
| `selection` | 选择类工具，无数据输出 | 返回 `{layer_id, selected_count, name}` |

`name_param` 必须指向一个 `output_name` 类型参数；`default_name` 是缺省输出名（同目录内不同工具可以重复，因为输出 GDB 按任务隔离）。

## 3. params（参数 schema）

| 类型 | JSON 值 | 校验 | 生成的 GP 值 |
|---|---|---|---|
| `dataset` | 字符串路径 | 本机绝对路径、shapefile 四件套、GDB 要素类存在；`geometry` 白名单 | 规范化路径字符串 |
| `dataset_list` | 数组或 `;` 分隔字符串 | 每个元素同上；`min_items`/`max_items` | `;` 拼接字符串 |
| `number` / `distance` | 数字 | `min` / `max` 闭区间 | double |
| `integer` | 整数 | 范围 | int |
| `wkid` | 整数 | 范围 | `SpatialReferenceBuilder.CreateSpatialReference(wkid)` |
| `boolean` | true/false | — | bool |
| `enum` | 字符串 | `values` 封闭域 | 字符串 token |
| `unit` | 字符串 | `values` 封闭域 | 与 `distance` 组合为 `"2 Meters"` |
| `string` | 字符串 | `max_length`、`pattern`（正则） | 字符串 |
| `statistics_fields` | 字符串 | `字段 SUM` 或 `字段1 SUM;字段2 MEAN`（统计量白名单） | 原样 |
| `field` | 字符串 | 字段名正则；提交时校验字段在数据集中存在 | 字段名 |
| `field_list` | 数组或 `;` 字符串 | 同上逐个校验 | `;` 拼接 |
| `output_name` | 字符串 | `^[A-Za-z][A-Za-z0-9_]{0,63}$` | 输出名 |
| `map_layer` | 字符串 `layer_id` | 在目标地图中解析为 FeatureLayer | Layer 对象（MCT 创建） |

公共字段：

- `required`: 缺失报 `INVALID_ARGUMENT`。
- `default`: 缺省值（会参与幂等指纹）。
- `required_if`: `{"其他参数": ["取值", ...]}`，条件满足时该参数必填（如 Buffer 的 `dissolve_field` 在 `dissolve_option=LIST` 时必填）。
- `dataset_param`: `field` / `field_list` 校验存在性所依赖的数据集参数。
- `geometry`: 允许的几何类型（`Point`/`Multipoint`/`Polyline`/`Polygon`）。
- `min_items` / `max_items` / `min` / `max` / `max_length` / `pattern` / `values`。

## 4. arguments（位置参数装配）

数组顺序就是 `ExecuteToolAsync` 的位置参数顺序。每项**恰好**一个来源：

| 来源 | 写法 | 含义 |
|---|---|---|
| 命名参数 | `{"param":"input_path"}` | 取 `params` 中的值；缺失时填 `null` |
| 生成的输出路径 | `{"generated_output":true}` | `<job>/result.gdb/<output_name>` |
| 生成的输出 GDB | `{"generated_gdb":true}` | `<job>/result.gdb`（用于 `conversion.FeatureClassToGeodatabase`） |
| 常量 | `{"literal":"ALL"}` / `{"literal":null}` | 固定 GP token / 空位 |
| 距离+单位 | `{"distance_unit":true}` | 由 `distance` + `unit` 组合为 `"2 Meters"` |
| 空间参考 | `{"spatial_reference":{"param":"target_wkid"}}` | 用 wkid 构造 `SpatialReference` |

执行器会删除**末尾**的 `null` 参数（ArcGIS 允许省略尾部可选参数），但保留中间 `null`（例如 SpatialJoin 的 `field_mapping`）。中间的 `null` 已实测被 `ExecuteToolAsync` 接受。

## 5. 新增一个工具（示例：`analysis.SymDiff` 之外的 `analysis.Frequency`）

1. 用 ArcPy 确认工具名与位置参数顺序（权威来源）：

```powershell
& '<ProInstallDir>\bin\Python\envs\arcgispro-py3\python.exe' -c "import arcpy,inspect;print(inspect.signature(arcpy.analysis.Frequency))"
```

2. 在 `gp_catalog.json` 的 `tools` 增加条目，把参数按上表拆成 `params` 与 `arguments`。
3. 运行离线校验：

```powershell
dotnet run --project tests\Native.CoreChecks\Native.CoreChecks.csproj -c Release
.venv\Scripts\python.exe -m unittest discover -s tests -p "test_*.py"
```

4. 构建并部署（Pro 关闭）：`scripts\build.ps1`、`scripts\deploy.ps1 -Apply`。
5. 用独立客户端或 `pro_run_gp` 冒烟，`pro_job_status` 检查 `outputs` / `validation` / `map_binding`。

> 不允许登记会原地修改/删除输入或执行任意代码的工具；如果确实需要，先在 `management.CopyFeatures` 生成的副本上操作，或走后续版本的显式确认策略。

## 6. 当前目录清单（27）

| 工具 | 输出 | 关键参数 |
|---|---|---|
| `analysis.Clip` | 要素类 | `input_path`, `clip_path`(面) |
| `analysis.PairwiseClip` | 要素类 | 同上 |
| `analysis.Buffer` | 要素类 | `distance`,`unit`,`line_side`,`line_end_type`,`dissolve_option`(,`dissolve_field`),`method` |
| `analysis.PairwiseBuffer` | 要素类 | `distance`,`unit`,`dissolve_option`,`dissolve_field`,`method` |
| `analysis.MultipleRingBuffer` | 要素类 | `distances`（如 `"2;4"`）,`unit`,`dissolve_option`,`outside_only`,`method` |
| `management.Dissolve` | 要素类 | `dissolve_field`,`statistics_fields`,`multi_part`,`unsplit_lines` |
| `analysis.PairwiseDissolve` | 要素类 | `dissolve_field`,`statistics_fields`,`multi_part` |
| `analysis.Intersect` | 要素类 | `input_paths`(≥2),`join_attributes`,`output_type` |
| `analysis.PairwiseIntersect` | 要素类 | 同上 |
| `analysis.Union` | 要素类 | `input_paths`(≥2,面),`join_attributes` |
| `analysis.Erase` / `analysis.PairwiseErase` | 要素类 | `input_path`,`erase_path`(面) |
| `analysis.Identity` | 要素类 | `input_path`,`identity_path`,`join_attributes` |
| `analysis.SymDiff` | 要素类 | `input_path`,`update_path`,`join_attributes` |
| `analysis.SpatialJoin` | 要素类 | `target_path`,`join_path`,`join_operation`,`join_type`,`match_option`,`search_distance` |
| `management.MinimumBoundingGeometry` | 要素类 | `geometry_type`,`group_option`,`group_field` |
| `management.FeatureToPoint` | 要素类 | `point_location` |
| `management.PolygonToLine` / `management.FeatureToLine` | 要素类 | 面输入 |
| `management.MultipartToSinglepart` | 要素类 | — |
| `management.Project` | 要素类 | `target_wkid`,`transform_method`,`preserve_shape` |
| `management.Merge` | 要素类 | `input_paths` |
| `management.CopyFeatures` | 要素类 | — |
| `conversion.FeatureClassToGeodatabase` | 多要素类 GDB | `input_paths` |
| `analysis.Statistics` | 表 | `statistics_fields`,`case_field` |
| `management.SelectLayerByAttribute` | 选择 | `layer_id`,`where_clause`,`selection_type`,`invert_where_clause` |
| `management.SelectLayerByLocation` | 选择 | `layer_id`,`select_features_path`,`overlap_type`,`search_distance`,`selection_type` |
