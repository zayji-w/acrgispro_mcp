# 制图与导出（0.5.0）

新增8个MCP接口，保留0.4.0的28个接口和27个GP目录工具。图形、标注、布局通过ArcGIS Pro SDK在实时工程内执行；不向Python窗口输入脚本。

| 接口 | 输入和结果 |
|---|---|
| pro_set_simple_renderer | project_id/map_id/layer_id，#RRGGBB色；点大小/线宽/面轮廓宽度以磅为单位 |
| pro_set_labels | 指定已有字段、字号/字体/颜色；仅构造字段引用，不接受任意表达式；替换目标层标注类 |
| pro_layer_presentation | 读取实际CIM渲染器与标注配置，供修改前后复核 |
| pro_list_layouts | 枚举layout_id、页面、元素名称/类型 |
| pro_create_layout | 指定extent_layer_id研究区图层、name/title；A4/A3，默认orientation=auto |
| pro_open_layout | 按project_id/layout_id在Pro中打开布局视图 |
| pro_export_layout | PNG/PDF、72–600dpi；返回job_id，使用pro_job_status查询 |
| pro_export_map | 当前活动2D地图直接导出PNG，明确width/height像素；返回job_id |

## 自动横竖版

输入extent_layer_id应是研究区边界图层。读取该图层完整范围，投影到目标地图坐标系，再比较宽高：宽>高用横向，否则竖向。正方形固定用竖向；显式landscape/portrait优先。结果返回aspect_ratio、study_extent、orientation_basis与width_cm/height_cm，便于复核。页面选择依据研究区范围，不依据电脑窗口比例；不自动旋转研究区或改变地图坐标系。范围受地图投影影响，跨日期变更线/异常跨度建议先采用适合研究区的地图投影。

模板包含标题、地图框、图例、比例尺、指北针；三种附加元素均可关闭。地图框覆盖研究区，周围留6%范围边距。创建前拒绝同名布局，失败时仅清理本次未完成的新布局。没有自动覆盖或编辑已有布局，也不自动保存工程。需要保留布局时由用户保存工程。

本版是基础制图：单一符号与单字段标注。分级设色/唯一值专题图、格网、插图和布局元素精细编辑尚未提供。

## 导出可靠性

共用已有JobManager：单活动任务、持久日志、幂等键、状态与取消接口；GP与导出不会同时占用后台任务槽。输出始终新建到output_root/job_id/name.png或.pdf，不能指定任意目标路径。先写.partial文件，校验签名/大小后原子改名，不覆盖文件。返回SHA256、大小、格式、DPI，PNG附像素尺寸。

地图PNG限制40MP，布局导出限制80MP；降低dpi可避免超限。PDF为完整页面，PNG为页面/地图；不把页面截图当导出。原生Export在渲染中途不可打断：开始前取消有效，渲染结束后到达的取消请求会保留真实成功状态和CANCELLATION_ARRIVED_AFTER_NATIVE_EXPORT警告。渲染会短暂占用Pro工作线程，心跳与job_status仍可响应；不能承诺渲染期间所有地图查询都即时完成。

导出和布局都绑定提交时的project_id与layout_id/map_id，执行前再次检查目标，拒绝已切换的上下文。新建布局需要研究区图层和已知坐标系；地图直接导出需要活动视图属于目标地图。

## 部署与验收

插件需在Pro正常关闭后替换。部署脚本备份旧插件与运行文件，保留现有bridge.config.json。客户端注册脚本从@mcp.tool声明生成白名单，避免再次过滤新工具。既有聊天仍可能需要刷新才能加载新接口。

验收分离：离线编译/参数/方向测试通过不代表已在Pro出图。现场测试在独立临时工程执行，至少验证宽/高两种研究区自动选版、标签与符号读回、布局元素、PNG/PDF文件可打开和尺寸正确、同键去重、坏路径/非法DPI/失效layout_id。原始研究工程不得用于覆盖式测试。

原生实现依据[Esri布局SDK示例](https://github.com/Esri/arcgis-pro-sdk/wiki/ProSnippets-Layouts)和[Esri标注SDK示例](https://github.com/Esri/arcgis-pro-sdk/wiki/ProSnippets-Labeling)，并使用本机Pro3.7程序集编译验证。
