# 现有布局编辑（0.6.2）

本版新增5个MCP接口，共41个。直接编辑指定现有元素，不要求重建布局，不自动保存工程。

| 接口 | 用途 |
|---|---|
| pro_list_layout_elements | 读取名称、类型、位置、宽高、角度、锁定/可见状态和文字/字号；返回重复名称 |
| pro_update_layout_element | 按唯一名称移动、改宽高、等比缩放、旋转；文字可改内容/字号 |
| pro_update_layout_elements | 同一布局1–100元素批量编辑，适合说明底框和文字一起调整 |
| pro_add_layout_text | 新建点文字或段落说明框；段落需同时给width_cm/height_cm |
| pro_place_city_name | 市名每个Unicode字符生成独立文字，逐字指定位置/角度/字号 |

## 坐标与缩放

统一使用厘米：原点在纸面左下角，x_cm/y_cm为元素未旋转包围框的左下角。原布局为毫米、英寸等单位时自动换算。字号单位是磅，rotation为角度（正数逆时针）。每次操作返回实际位置和尺寸，不能把请求值当成已生效的值。

移动会保留原元素锚点；旋转后将包围框放到目标位置。width_cm/height_cm读取及修改SDK元素框尺寸，content_width_cm/content_height_cm另行返回GetBounds得到的内容范围。对于图例，两者可以不同。scale与明确宽高互斥；文字和说明框等比缩放框及字号。

图例的明确宽高使用原生AdjustSize适配（必要时缩小文字），放大外框并不保证内容随之放大。使用scale时会缩放图例文字、符号块、间距、缩进和换行宽度，并用AdjustFrame让框适配内容，实际框尺寸由Pro排版决定；这保持图例与地图图层的动态关联。结果返回实际legend_fit和legend_style以便核对。参见[Esri图例适配策略](https://doc.esri.com/en/arcgis-pro/latest/sdk/api-reference/topic158.html)。边框与文字是不同元素时，用batch分别列出两个名字，避免只移动底框。

编辑只按list返回的完整唯一名称定位。同名对象返回AMBIGUOUS_ELEMENT，不猜测选择；可先在Pro将重复名字改为唯一名。expected_type可防止误编辑同名但类型已变的元素。群组需显式列出要编辑的成员，本版不整体改变群组变换。

锁定元素默认返回ELEMENT_LOCKED；allow_locked=true只在本次编辑期间临时解锁，结束后恢复锁定状态。批次先解析所有目标、校验类型再执行；SDK操作失败时恢复改动前的CIM定义，恢复失败明确返回PARTIAL_LAYOUT_UPDATE，不假称已回滚。没有新增删除布局/研究图层接口。

## 逐字市名

例如text="合肥市"，positions按字符顺序传3条记录：

```json
[{"x_cm":6,"y_cm":10},{"x_cm":6.6,"y_cm":10.1,"rotation":8},{"x_cm":7.2,"y_cm":10.2}]
```

name="City_Hefei"时，新元素名为City_Hefei_01/02/03。返回每个字的实际位置/字号，之后可通过update单独微调。最多64个字符，含补充平面汉字，位置数量必须一致。重复元素名拒绝创建，避免请求重试时重复叠字；创建结果未知时先list核对。SDK创建失败会移除本次已创建的字，不动其它文字。

这是布局纸面文字，会随布局导出，不是地图地理坐标注记；地图范围改变时不会自动追随行政中心。输入按普通文字转义，不能提交任意表达式或动态标签代码。

## 实现与验证边界

位置、缩放、文字由Pro3.7 SDK在MCT调用；参见[Esri布局示例](https://github.com/Esri/arcgis-pro-sdk/wiki/ProSnippets-Layouts)与[图例适配属性](https://doc.esri.com/en/arcgis-pro/latest/sdk/api-reference/topic2691.html)。

离线编译/参数测试与现场验收分别记录。现场使用临时工程、合成研究区和预先存在的图例/说明底框/文字；检查厘米到毫米换算、移动/缩放读回、未选择元素保持、锁定和错误批次无修改、每字位置独立、PNG/PDF导出文字可见，原工程指纹不变。

0.6.2现场记录：`artifacts/acceptance/layout-edit-20261102-2200/report.md`。2026-11-03完成42项独立真实MCP断言，另有当前聊天5个新增接口的直接调用记录及最终PNG。25项Python测试、104条核心断言与7条启动检查通过。市名摆放由调用者提供每字坐标，本版不自动避让河流、道路和其它文字，也不整体变换群组。
