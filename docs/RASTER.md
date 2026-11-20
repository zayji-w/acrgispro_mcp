# 栅格处理

用 `bridge_status` 确认原生桥接在线，再用 `pro_map_state` 获取当前的 `project_id` 和目标二维地图的 `map_id`。路径使用本机绝对路径。支持 GeoTIFF、IMG、JP2、带 HDR 的 ENVI DAT 和 File GDB 内的栅格数据集。

| 接口 | 参数及行为 |
|---|---|
| `pro_describe_raster` | 传入 `path`，返回坐标系、范围、行列数、像元大小、波段及其 NoData 元数据。 |
| `pro_add_raster_layer` | 加入目标地图。重复添加同一数据路径时返回现有图层。 |
| `pro_raster_clip` | 在 `mask_path` 与 `bbox` 中选择一种。前者沿同坐标系多边形轮廓裁剪，轮廓外写为 NoData。后者用源坐标系的 `[xmin,ymin,xmax,ymax]` 保留矩形范围。输出范围贴合源像元网格。 |
| `pro_raster_project` | 指定 `target_wkid` 和目标坐标系单位下的 `cell_size`。地理坐标系变化时传入 ArcGIS 的 `transform_method`。可用同坐标系、同分辨率的 `snap_path` 指定参考网格。 |
| `pro_raster_resample` | 用源坐标系单位设置方形像元的 `cell_size`，保留左下角原点。任选 `NEAREST`、`BILINEAR`、`CUBIC` 或 `MAJORITY`。默认最近邻，适用于分类影像。 |
| `pro_raster_index` | 输入两份对齐的影像，也可指定一份多波段影像中的不同波段。波段从 1 编号，见下表。 |
| `pro_raster_zonal_statistics` | 输入同坐标系的多边形分区和单波段、方形像元的值栅格。区域字段用整数或文本。默认 `MEAN`，还支持 `ALL`、`MINIMUM`、`MAXIMUM`、`SUM`、`STD`、`MEDIAN`、`RANGE`、`MIN_MAX`、`MIN_MAX_MEAN`。用 `pro_read_table` 读取生成的 File GDB 表。 |

指数与分区统计需要 Spatial Analyst。指数在相同的坐标系、范围、分辨率及行列网格上计算 `(正波段－负波段)/(正波段＋负波段)`。任一输入像元为 NoData 或分母为零时，输出为 NoData。按实际光谱含义选波段，并提前应用数据产品要求的缩放和偏移。

| 指数 | 正波段 | 负波段 |
|---|---|---|
| NDVI | 近红外 | 红光 |
| NDWI | 绿光 | 近红外 |
| MNDWI | 绿光 | 短波红外 1 |
| NDMI | 近红外 | 短波红外 1 |
| NDBI | 短波红外 1 | 近红外 |

分区统计使用值栅格的像元网格。默认 `ignore_nodata=true` 只统计有效像元。设为 `false` 时，包含 NoData 的分区没有有效统计值。未覆盖像元中心的细小分区也可能没有记录。

五个处理接口返回 `job_id`，用 `pro_job_status` 查询，可以用 `pro_cancel_job` 请求取消。调用中断后可用同样参数和 `idempotency_key` 查询已提交的任务。输出保存在配置的结果目录下，每项任务使用独立子目录。栅格结果采用 TIFF，默认加入指定地图。传 `add_to_map=false` 可只生成文件。桥接在添加之前回读并检查输出元数据，项目或地图切换后仍可从作业结果取得文件路径。ArcGIS Pro 工程的保存由用户决定。
