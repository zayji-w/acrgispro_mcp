# 属性表和统计结果读取

调用 pro_read_table 并传入本地 Shapefile、File GDB 要素类或独立表的绝对路径。读取范围为路径对应的数据集全表。

```json
{
  "path": "C:/example/data.gdb/county",
  "fields": ["OBJECTID", "NAME", "POPULATION"],
  "order_by": "POPULATION",
  "direction": "desc",
  "limit": 20,
  "include_count": true
}
```

- fields 指定 1–32 个属性字段，名称匹配忽略大小写。省略时按表结构返回前 32 个可读属性字段。omitted_field_count 是未返回的可读属性字段数。空值返回 null，日期以 ISO 8601 形式表示，支持数值、文本、日期时间和 GUID 等标量。
- limit 默认为 50，范围 1–100。读取下一页时保持原路径、字段和排序，使用 pagination.next_offset 作为 offset。next_offset 为 null 代表最后一页。include_count=true 时返回总行数。若数据在两页之间发生变化，从第一页重新读取。
- File GDB 默认按 ObjectID 升序。指定 order_by 后按字段排序，同值按 ObjectID 升序排列。direction 支持 asc 或 desc。Shapefile 按 FID 升序，需要按其它属性排序时可先将要素复制到 File GDB。 [Esri QueryFilter 分页示例](https://doc.esri.com/en/arcgis-pro/latest/sdk/api-reference/conceptdocs/docs/ProSnippets-Geodatabase.html)
- pro_statistics 提交后查询 pro_job_status，任务成功后将 outputs[0].path 传给 pro_read_table。可按 SUM_字段名降序取得分组排名，按需选择分组字段和统计列。
