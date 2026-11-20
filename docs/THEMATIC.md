# 专题图渲染

通过 `pro_map_state` 取得当前工程、二维地图和图层 ID。专题工具操作点、线和面要素图层。图层的定义查询继续参与渲染，属性数据保持原样，工程由用户保存。

| 接口 | 主要参数与结果 |
|---|---|
| `pro_set_unique_value_renderer` | `field` 选择一个文本或整数类型字段。默认最多 32 类，可调至 128 类。超限会报告错误。返回每类的源值、图例文字及颜色。 |
| `pro_set_graduated_colors_renderer` | `field` 选择数值字段。默认 `natural_breaks`，还支持 `equal_interval` 和 `quantile`。自动分级的 `class_count` 默认 5 类，可传 2–12。 |
| `pro_get_thematic_renderer` | 读回 ArcGIS Pro 实际使用的渲染器。返回分类字段、类别或上下界、图例文字和颜色。自动分级应以读回的类别和断点为准。 |

手动分级用 `breaks=[0, 30, 60, 100]`，左端第一个区间含下界，其余区间左开右闭。数组依次填写最小值和各级上界，省略 `method` 或传 `manual`。

`colors` 可指定每类的 `#RRGGBB` 色值。分级颜色按数值升序排列。唯一值颜色按接口返回的类别顺序排列。颜色数量须与实际类别数一致。`show_other` 控制其余值的可见性，`other_label` 用于修改其图例文字。

完成渲染后，调用 `pro_create_layout` 并开启 `legend`。图例使用地图中的渲染样式。用 `pro_export_layout` 导出 PNG 或 PDF，并用 `pro_job_status` 查询导出结果。默认按研究区轮廓宽高选择纸张方向。分类较多时，可通过布局编辑工具移动或放大图例。
