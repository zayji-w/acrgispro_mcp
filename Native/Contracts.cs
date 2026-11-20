using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeBridge.Native;

internal sealed class BridgeException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

internal static class Contracts
{
    public const string NativeVersion = "0.6.7";
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public static readonly string[] Legacy = ["version", "map_state", "add_demo", "remove_demo", "stop"];
    public static readonly string[] Operations = [..Legacy, "describe_dataset", "read_table", "add_layer", "set_layer_visibility", "zoom_to_layer", "list_gp_tools", "run_gp", "job_status", "cancel_job", "set_simple_renderer", "set_labels", "layer_presentation", "list_layouts", "create_layout", "open_layout", "export_layout", "export_map", "list_layout_elements", "update_layout_elements", "add_layout_text", "place_city_name", "describe_raster", "add_raster_layer", "raster_clip", "raster_project", "raster_resample", "raster_index", "raster_zonal_statistics", "set_unique_value_renderer", "set_graduated_colors_renderer", "get_thematic_renderer"];
    private const string RasterJob = "project_id map_id idempotency_key ";
    private const string RasterOutput = "add_to_map output_name ";
    private static readonly Dictionary<string, (string Required, string Optional)> Schemas = new()
    {
        ["describe_dataset"] = ("path", "include_count"),
        ["read_table"] = ("path", "fields order_by direction offset limit include_count"),
        ["describe_raster"] = ("path", ""),
        ["add_raster_layer"] = ("project_id map_id path", "display_name"),
        ["raster_clip"] = (RasterJob + "input_path", RasterOutput + "mask_path bbox"),
        ["raster_project"] = (RasterJob + "input_path target_wkid cell_size", RasterOutput + "resampling transform_method snap_path"),
        ["raster_resample"] = (RasterJob + "input_path cell_size", RasterOutput + "resampling"),
        ["raster_index"] = (RasterJob + "index positive_path negative_path", RasterOutput + "positive_band negative_band"),
        ["raster_zonal_statistics"] = (RasterJob + "zone_path zone_field value_path", "statistics ignore_nodata output_name"),
        ["map_state"] = ("", "project_id map_id offset limit"),
        ["add_layer"] = ("project_id map_id path", "display_name"),
        ["set_layer_visibility"] = ("project_id map_id layer_id visible", ""),
        ["zoom_to_layer"] = ("project_id map_id layer_id", ""),
        ["list_gp_tools"] = ("", ""),
        ["run_gp"] = ("project_id map_id tool parameters idempotency_key", ""),
        ["job_status"] = ("job_id", ""), ["cancel_job"] = ("job_id", ""),
        ["set_simple_renderer"]=("project_id map_id layer_id color","outline_color width size outline_width"),
        ["set_unique_value_renderer"] = ("project_id map_id layer_id field", "colors category_limit show_other other_label"),
        ["set_graduated_colors_renderer"] = ("project_id map_id layer_id field", "method class_count breaks colors show_other other_label"),
        ["get_thematic_renderer"] = ("project_id map_id layer_id", ""),
        ["set_labels"]=("project_id map_id layer_id field","visible font_size color font_family class_name"),
        ["list_layouts"]=("","project_id offset limit"),
        ["layer_presentation"]=("project_id map_id layer_id",""),
        ["create_layout"]=("project_id map_id extent_layer_id name title","orientation page_size legend scale_bar north_arrow"),
        ["open_layout"]=("project_id layout_id",""),
        ["export_layout"]=("project_id layout_id idempotency_key","format dpi output_name"),
        ["export_map"]=("project_id map_id idempotency_key","dpi width height output_name")
        ,["list_layout_elements"]=("project_id layout_id","offset limit")
        ,["update_layout_elements"]=("project_id layout_id edits","allow_locked")
        ,["add_layout_text"]=("project_id layout_id name text x_cm y_cm","width_cm height_cm font_size font_family color rotation")
        ,["place_city_name"]=("project_id layout_id name text positions","font_size font_family color")
    };
    public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public static string Text(JsonElement e, string key) => e.GetProperty(key).GetString()!;
    public static string? OptionalText(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetString() : null;
    public static bool Bool(JsonElement e, string key, bool fallback = false) => e.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
    public static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    public static bool IsPaged(JsonElement args) =>
        args.TryGetProperty("offset", out _) || args.TryGetProperty("limit", out _);
    public static (int Offset, int Limit) Page(JsonElement args) =>
        (args.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0,
         args.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 50);
    public static object PageInfo(int offset, int limit, int total) =>
        new { offset, limit, total, next_offset = (long)offset + limit < total ? offset + limit : (int?)null };
    public static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>();
            foreach (var p in e.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new BridgeException("INVALID_ARGUMENT", "Duplicate JSON key");
                Unique(p.Value);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var v in e.EnumerateArray()) Unique(v);
    }
    public static JsonDocument Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 65536) throw new BridgeException("INVALID_ARGUMENT", "Request exceeds 64 KiB");
        try
        {
            var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            try { Unique(doc.RootElement); return doc; } catch { doc.Dispose(); throw; }
        }
        catch (JsonException) { throw new BridgeException("INVALID_ARGUMENT", "Invalid JSON"); }
    }
    public static void ValidateArgs(string op, JsonElement args)
    {
        if (!Operations.Contains(op)) throw new BridgeException("UNKNOWN_OPERATION", "Operation is not registered");
        var schema = Schemas.GetValueOrDefault(op, ("", ""));
        var required = Words(schema.Item1); var allowed = required.Concat(Words(schema.Item2)).ToHashSet();
        if (args.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_ARGUMENT", "Arguments must be object");
        if (required.Any(k => !args.TryGetProperty(k, out _))) throw new BridgeException("INVALID_ARGUMENT", "Missing argument");
        foreach (var p in args.EnumerateObject())
        {
            if (!allowed.Contains(p.Name)) throw new BridgeException("INVALID_ARGUMENT", "Unexpected argument: " + p.Name);
            bool valid = p.Name switch
            {
                "parameters" => p.Value.ValueKind == JsonValueKind.Object,
                "bbox" => RasterSpec.ValidBounds(p.Value),
                "breaks" => ThematicSpec.ValidBreaks(p.Value),
                "colors" => ThematicSpec.ValidColors(p.Value),
                "category_limit" => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var categories) && categories is >= 1 and <= 128,
                "class_count" => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var classes) && classes is >= 2 and <= 12,
                "show_other" => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "cell_size" => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var cell) && double.IsFinite(cell) && cell > 0,
                "target_wkid" or "positive_band" or "negative_band" => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var band) && band > 0,
                "add_to_map" or "ignore_nodata" => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "fields" => ValidReadFields(p.Value),
                "order_by" => p.Value.ValueKind == JsonValueKind.String
                    && p.Value.GetString() is { Length: <= 128 } order && !string.IsNullOrWhiteSpace(order),
                "direction" => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "asc" or "desc",
                "offset" => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var offset) && offset >= 0,
                "limit" => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var limit) && limit is >= 1 and <= 100,
                "edits" or "positions"=>p.Value.ValueKind==JsonValueKind.Array,
                "allow_locked"=>p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "x_cm" or "y_cm" or "width_cm" or "height_cm" or "rotation"=>p.Value.ValueKind==JsonValueKind.Number&&p.Value.TryGetDouble(out var coordinate)&&double.IsFinite(coordinate),
                "visible" or "include_count" or "legend" or "scale_bar" or "north_arrow" => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "width" or "height" or "size" or "outline_width" or "font_size" or "dpi" => p.Value.ValueKind==JsonValueKind.Number && p.Value.TryGetDouble(out var numeric) && double.IsFinite(numeric),
                _ => p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()) && p.Value.GetString()!.Length <= 4096
            };
            if (!valid) throw new BridgeException("INVALID_ARGUMENT", "Invalid argument: " + p.Name);
        }
        if (op is "job_status" or "cancel_job" && !Regex.IsMatch(Text(args, "job_id"), "^[0-9a-f]{32}$")) throw new BridgeException("INVALID_ARGUMENT", "Invalid job ID");
        if (op == "run_gp" && Text(args, "idempotency_key").Length > 128) throw new BridgeException("INVALID_ARGUMENT", "Idempotency key exceeds 128 characters");
        if (op == "map_state" && args.TryGetProperty("map_id", out _) && !args.TryGetProperty("project_id", out _))
            throw new BridgeException("INVALID_ARGUMENT", "map_id requires project_id");
        PresentationSpec.Validate(op,args);
        LayoutEditSpec.Validate(op,args);
        RasterSpec.Validate(op,args);
        ThematicSpec.Validate(op,args);
    }
    private static bool ValidReadFields(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() is < 1 or > 32) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return fields.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: <= 128 } name && !string.IsNullOrWhiteSpace(name) && names.Add(name));
    }
    public static (string Op, JsonElement Args, bool V2) Validate(JsonElement req, string id, string token, double? now = null)
    {
        if (req.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_ARGUMENT", "Request must be object");
        bool v2 = req.TryGetProperty("protocol_version", out var version);
        string[] expected = v2 ? ["args", "expires", "id", "op", "protocol_version", "token"] : ["expires", "id", "op", "token"];
        if (!req.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(expected)) throw new BridgeException("INVALID_ARGUMENT", "Unexpected request fields");
        if (req.GetProperty("id").ValueKind != JsonValueKind.String || Text(req,"id") != id || !Regex.IsMatch(id,"^[0-9a-f]{32}$")) throw new BridgeException("INVALID_ARGUMENT", "Request ID mismatch");
        if (req.GetProperty("token").ValueKind != JsonValueKind.String || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Text(req,"token")), Encoding.UTF8.GetBytes(token))) throw new BridgeException("UNAUTHORIZED", "Invalid session token");
        double current = now ?? Now;
        if (req.GetProperty("expires").ValueKind!=JsonValueKind.Number || !req.GetProperty("expires").TryGetDouble(out double expiry) || !double.IsFinite(expiry) || expiry < current || expiry > current + 60) throw new BridgeException("REQUEST_EXPIRED", "Invalid request lifetime");
        if (req.GetProperty("op").ValueKind != JsonValueKind.String) throw new BridgeException("INVALID_ARGUMENT", "Invalid operation");
        string op = Text(req,"op");
        var args = v2 ? req.GetProperty("args") : JsonSerializer.SerializeToElement(new { });
        if (v2)
        {
            if (version.ValueKind!=JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 2) throw new BridgeException("PROTOCOL_MISMATCH", "Protocol 2 required");
            ValidateArgs(op, args);
        }
        else if (!Legacy.Contains(op)) throw new BridgeException("UNKNOWN_OPERATION", "Legacy operation is not registered");
        return (op,args.Clone(),v2);
    }
    public static object Error(Exception ex) => ex is BridgeException b
        ? new { code=b.Code, message=b.Message, retryable=false, details=b.Details }
        : new { code="INTERNAL_ERROR", message=ex.Message, retryable=false, details=(object?)null };
    public static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{"+string.Join(",",element.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>JsonSerializer.Serialize(p.Name)+":"+Canonical(p.Value)))+"}",
        JsonValueKind.Array => "["+string.Join(",",element.EnumerateArray().Select(Canonical))+"]",
        _ => element.GetRawText()
    };
}
