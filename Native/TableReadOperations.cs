using System.Text.Json;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Exceptions;

namespace SafeBridge.Native;

internal static class TableReadOperations
{
    private const int MaxFields = 32;

    private static bool Supported(Field field) => field.FieldType is
        FieldType.OID or FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger or
        FieldType.Single or FieldType.Double or FieldType.String or FieldType.Date or
        FieldType.DateOnly or FieldType.TimeOnly or FieldType.TimestampOffset or FieldType.GUID or FieldType.GlobalID;

    private static object? JsonValue(object? value) => value switch
    {
        null or DBNull => null,
        float number when !float.IsFinite(number) => null,
        double number when !double.IsFinite(number) => null,
        string or byte or short or int or long or float or double or decimal or
            DateTime or DateOnly or TimeOnly or DateTimeOffset or Guid => value,
        _ => throw new BridgeException("UNSUPPORTED_FIELD_TYPE", "Field value cannot be represented in JSON")
    };

    public static object Read(JsonElement args)
    {
        var dataset = PathPolicy.Dataset(Contracts.Text(args, "path"));
        return DatasetOperations.WithTable(dataset, table => ReadPage(table, dataset, args));
    }

    private static object ReadPage(Table table, (string Path, string Store, string Name, bool Shape) dataset, JsonElement args)
    {
        using var definition = table.GetDefinition();
        if (!definition.HasObjectID())
            throw new BridgeException("UNSUPPORTED_DATASET", "Table needs a unique ObjectID for pagination");
        var all = definition.GetFields();
        var byName = all.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        Field Resolve(string name)
        {
            if (!byName.TryGetValue(name, out var field))
                throw new BridgeException("FIELD_NOT_FOUND", $"Field does not exist: {name}");
            if (!Supported(field))
                throw new BridgeException("UNSUPPORTED_FIELD_TYPE", $"Field is not a scalar attribute: {field.Name}");
            return field;
        }
        var oid = Resolve(definition.GetObjectIDField());
        var requested = args.TryGetProperty("fields", out var names);
        var available = all.Where(Supported).ToArray();
        var fields = requested ? names.EnumerateArray().Select(name => Resolve(name.GetString()!)).ToArray()
            : available.Take(MaxFields).ToArray();
        var order = Resolve(Contracts.OptionalText(args, "order_by") ?? oid.Name);
        var direction = Contracts.OptionalText(args, "direction") ?? "asc";
        var descending = direction == "desc";
        if (dataset.Shape && (order.Name != oid.Name || descending))
            throw new BridgeException("UNSUPPORTED_SORT", "Shapefile reads support FID ascending. Copy to File GDB for other sorts.");

        var (offset, limit) = Contracts.Page(args);
        var subfields = fields.Select(field => field.Name).Append(oid.Name).Append(order.Name).Distinct(StringComparer.Ordinal);
        var filter = new QueryFilter { SubFields = string.Join(",", subfields) };
        if (!dataset.Shape)
        {
            static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
            var orderBy = Quote(order.Name) + (descending ? " DESC" : " ASC");
            if (order.Name != oid.Name) orderBy += ", " + Quote(oid.Name) + " ASC";
            filter.PostfixClause = "ORDER BY " + orderBy;
            filter.Offset = offset;
            filter.RowCount = limit + 1;
        }

        var rows = new List<Dictionary<string, object?>>(limit);
        var hasMore = false;
        long? count = null;
        try
        {
            using var cursor = table.Search(filter, true);
            long previousId = -1;
            var remainingToSkip = dataset.Shape ? offset : 0;
            while (cursor.MoveNext())
            {
                using var row = cursor.Current;
                if (dataset.Shape)
                {
                    var id = row.GetObjectID();
                    if (id <= previousId)
                        throw new BridgeException("UNSTABLE_ORDER", "Shapefile cursor did not return ascending FIDs");
                    previousId = id;
                    if (remainingToSkip > 0)
                    {
                        remainingToSkip--;
                        continue;
                    }
                }
                if (rows.Count == limit)
                {
                    hasMore = true;
                    break;
                }
                rows.Add(fields.ToDictionary(field => field.Name, field => JsonValue(row[field.Name])));
            }
            if (Contracts.Bool(args, "include_count")) count = table.GetCount();
        }
        catch (GeodatabaseException ex)
        {
            throw new BridgeException("TABLE_QUERY_FAILED", ex.Message);
        }
        var next = (long)offset + rows.Count;
        if (hasMore && next > int.MaxValue)
            throw new BridgeException("PAGINATION_LIMIT", "Next offset exceeds the supported range");
        return new
        {
            path = dataset.Path,
            kind = dataset.Shape ? "shapefile" : table is FeatureClass ? "feature_class" : "table",
            object_id_field = oid.Name,
            fields = fields.Select(field => new { name = field.Name, type = field.FieldType.ToString() }).ToArray(),
            omitted_field_count = available.Length - fields.Length,
            sort = new { field = order.Name, direction, tie_breaker = order.Name == oid.Name ? null : oid.Name },
            rows,
            pagination = new { offset, limit, total = count, next_offset = hasMore ? (long?)next : null }
        };
    }
}
