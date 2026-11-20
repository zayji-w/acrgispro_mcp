using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeBridge.Native;

// Pure catalog logic: no Esri dependencies, shared with offline CoreChecks.
internal enum GpValueKind { Text, Number, Integer, Boolean, Null, SpatialReferenceWkid, GeneratedOutput, GeneratedGdb, MapLayer }

internal sealed class GpValue
{
    public GpValueKind Kind { get; }
    public object? Value { get; }
    public GpValue(GpValueKind kind, object? value = null) { Kind = kind; Value = value; }
}

internal sealed class GpParam
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public bool Required { get; init; }
    public JsonElement? Default { get; init; }
    public string[]? Values { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string[]? Geometry { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }
    public string? Pattern { get; init; }
    public int? MaxLength { get; init; }
    public string? DatasetParam { get; init; }
    public Dictionary<string, string[]>? RequiredIf { get; init; }
}

internal sealed class GpArgument
{
    public string? Param { get; init; }
    public JsonElement? Literal { get; init; }
    public bool GeneratedOutput { get; init; }
    public bool GeneratedGdb { get; init; }
    public bool DistanceUnit { get; init; }
    public string? SpatialReferenceParam { get; init; }
}

internal sealed class GpTool
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Category { get; init; } = "";
    public string Risk { get; init; } = "";
    public string[] ParameterOrder { get; init; } = [];
    public Dictionary<string, GpParam> Params { get; init; } = new(StringComparer.Ordinal);
    public List<GpArgument> Arguments { get; init; } = [];
    public string OutputKind { get; init; } = "none";
    public string? OutputNameParam { get; init; }
    public string DefaultName { get; init; } = "";
    public string? OutputCrsFrom { get; init; }
    public bool RequiresSameGcs { get; init; }
    public bool HasDatasetInputs => Params.Values.Any(p => p.Type is "dataset" or "dataset_list");
}

internal sealed record NormalizedGp(Dictionary<string, JsonElement> Values, string? OutputName);

internal sealed class GpCatalog
{
    private static readonly Regex OutputNamePattern = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex FieldNamePattern = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex StatisticsPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,63}\s+(SUM|MEAN|MIN|MAX|COUNT|STD|VAR|RANGE|FIRST|LAST|MEDIAN|MODE)(;[A-Za-z_][A-Za-z0-9_]{0,63}\s+(SUM|MEAN|MIN|MAX|COUNT|STD|VAR|RANGE|FIRST|LAST|MEDIAN|MODE)){0,15}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly JsonDocument document;
    private readonly Dictionary<string, GpTool> tools = new(StringComparer.Ordinal);
    private readonly int maxParameters;

    public IReadOnlyDictionary<string, GpTool> Tools => tools;
    public int CatalogVersion => 2;

    public static GpCatalog Load(string path)
    {
        if (!File.Exists(path)) throw new BridgeException("CAPABILITY_UNAVAILABLE", "GP catalog is missing: " + path);
        return new GpCatalog(File.ReadAllText(path));
    }

    public GpCatalog(string json)
    {
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException) { throw new BridgeException("CAPABILITY_UNAVAILABLE", "GP catalog is not valid JSON"); }
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("catalog_version", out var version) || version.GetInt32() != 2)
            throw new BridgeException("CAPABILITY_UNAVAILABLE", "Unsupported GP catalog version");
        maxParameters = 24;
        if (root.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object && runtime.TryGetProperty("max_parameters", out var mp) && mp.TryGetInt32(out var limit) && limit > 0)
            maxParameters = limit;
        if (!root.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Object || !toolsElement.EnumerateObject().Any())
            throw new BridgeException("CAPABILITY_UNAVAILABLE", "GP catalog has no tools");
        foreach (var property in toolsElement.EnumerateObject())
        {
            var tool = ParseTool(property.Name, property.Value);
            if (tools.ContainsKey(tool.Name)) throw new BridgeException("CAPABILITY_UNAVAILABLE", "Duplicate GP tool: " + tool.Name);
            tools.Add(tool.Name, tool);
        }
        ValidateCatalog();
    }

    private static GpTool ParseTool(string name, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("Tool entry must be an object: " + name);
        if (!Regex.IsMatch(name, @"^(analysis|management|conversion|dataManagement)\.[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant))
            throw Invalid("Unsupported GP tool name: " + name);
        var risk = Text(element, "risk");
        if (risk is not ("write_new_output" or "read_only")) throw Invalid("Invalid risk for " + name);
        var parameterOrder = StringArray(element, "parameter_order") ?? [];
        var parameterValues = new Dictionary<string, GpParam>(StringComparer.Ordinal);
        var paramsElement = element.GetProperty("params");
        foreach (var property in paramsElement.EnumerateObject())
        {
            var spec = property.Value;
            var type = Text(spec, "type");
            if (type is not ("dataset" or "dataset_list" or "number" or "integer" or "boolean" or "enum" or "string" or "statistics_fields" or "field" or "field_list" or "distance" or "unit" or "output_name" or "map_layer" or "wkid"))
                throw Invalid("Unsupported parameter type for " + name + "." + property.Name + ": " + type);
            var required = spec.TryGetProperty("required", out var req) && req.GetBoolean();
            JsonElement? defaultValue = spec.TryGetProperty("default", out var def) && def.ValueKind != JsonValueKind.Null ? def.Clone() : null;
            Dictionary<string, string[]>? requiredIf = null;
            if (spec.TryGetProperty("required_if", out var requiredIfElement) && requiredIfElement.ValueKind == JsonValueKind.Object)
            {
                requiredIf = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var condition in requiredIfElement.EnumerateObject()) requiredIf[condition.Name] = ArrayStrings(condition.Value) ?? [];
            }
            parameterValues[property.Name] = new GpParam
            {
                Name = property.Name,
                Type = type,
                Required = required,
                Default = defaultValue,
                Values = StringArray(spec, "values"),
                Min = Number(spec, "min"),
                Max = Number(spec, "max"),
                Geometry = StringArray(spec, "geometry"),
                MinItems = Int(spec, "min_items"),
                MaxItems = Int(spec, "max_items"),
                Pattern = spec.TryGetProperty("pattern", out var pattern) ? pattern.GetString() : null,
                MaxLength = Int(spec, "max_length"),
                DatasetParam = spec.TryGetProperty("dataset_param", out var datasetParam) ? datasetParam.GetString() : null,
                RequiredIf = requiredIf
            };
        }
        var arguments = new List<GpArgument>();
        if (!element.TryGetProperty("arguments", out var argumentsElement) || argumentsElement.ValueKind != JsonValueKind.Array) throw Invalid("Missing arguments for " + name);
        foreach (var argument in argumentsElement.EnumerateArray())
        {
            var sources = 0;
            var parsed = new GpArgument
            {
                Param = argument.TryGetProperty("param", out var param) ? param.GetString() : null,
                Literal = argument.TryGetProperty("literal", out var literal) ? literal.Clone() : null,
                GeneratedOutput = argument.TryGetProperty("generated_output", out var generated) && generated.GetBoolean(),
                GeneratedGdb = argument.TryGetProperty("generated_gdb", out var generatedGdb) && generatedGdb.GetBoolean(),
                DistanceUnit = argument.TryGetProperty("distance_unit", out var distanceUnit) && distanceUnit.GetBoolean(),
                SpatialReferenceParam = argument.TryGetProperty("spatial_reference", out var spatialReference) ? spatialReference.GetProperty("param").GetString() : null
            };
            if (parsed.Param is not null) sources++;
            if (parsed.Literal is not null) sources++;
            if (parsed.GeneratedOutput) sources++;
            if (parsed.GeneratedGdb) sources++;
            if (parsed.DistanceUnit) sources++;
            if (parsed.SpatialReferenceParam is not null) sources++;
            if (sources != 1) throw Invalid("Argument must have exactly one source in " + name);
            arguments.Add(parsed);
        }
        var output = element.GetProperty("output");
        var kind = Text(output, "kind");
        if (kind is not ("feature_class" or "table" or "feature_classes" or "selection" or "none")) throw Invalid("Unsupported output kind for " + name);
        return new GpTool
        {
            Name = name,
            Label = element.TryGetProperty("label", out var label) ? label.GetString() ?? name : name,
            Category = element.TryGetProperty("category", out var category) ? category.GetString() ?? "general" : "general",
            Risk = risk,
            ParameterOrder = parameterOrder,
            Params = parameterValues,
            Arguments = arguments,
            OutputKind = kind,
            OutputNameParam = output.TryGetProperty("name_param", out var nameParam) ? nameParam.GetString() : null,
            DefaultName = output.TryGetProperty("default_name", out var defaultName) ? defaultName.GetString() ?? "result" : "result",
            OutputCrsFrom = element.TryGetProperty("environment", out var environment) && environment.ValueKind == JsonValueKind.Object && environment.TryGetProperty("output_crs_from", out var crsFrom) && crsFrom.ValueKind == JsonValueKind.String ? crsFrom.GetString() : null,
            RequiresSameGcs = element.TryGetProperty("requires_same_gcs", out var sameGcs) && sameGcs.GetBoolean()
        };
    }

    private void ValidateCatalog()
    {
        foreach (var tool in tools.Values)
        {
            var generatedOutput = tool.Arguments.Count(a => a.GeneratedOutput);
            var generatedGdb = tool.Arguments.Count(a => a.GeneratedGdb);
            if (tool.OutputKind is "feature_class" or "table")
            {
                if (generatedOutput != 1 || generatedGdb != 0) throw Invalid("Tool must declare one generated output: " + tool.Name);
                if (string.IsNullOrEmpty(tool.OutputNameParam) || !tool.Params.ContainsKey(tool.OutputNameParam) || tool.Params[tool.OutputNameParam].Type != "output_name")
                    throw Invalid("Tool must declare an output_name parameter: " + tool.Name);
            }
            else if (tool.OutputKind == "feature_classes")
            {
                if (generatedGdb != 1 || generatedOutput != 0) throw Invalid("Tool must declare one generated GDB: " + tool.Name);
            }
            else if (generatedOutput != 0 || generatedGdb != 0) throw Invalid("Tool must not generate datasets: " + tool.Name);
            foreach (var argument in tool.Arguments)
            {
                if (argument.Param is not null && !tool.Params.ContainsKey(argument.Param)) throw Invalid("Argument references unknown parameter in " + tool.Name + ": " + argument.Param);
                if (argument.SpatialReferenceParam is not null && (!tool.Params.TryGetValue(argument.SpatialReferenceParam, out var sr) || sr.Type != "wkid"))
                    throw Invalid("Spatial reference argument must reference a wkid parameter in " + tool.Name);
                if (argument.DistanceUnit && (!tool.Params.TryGetValue("distance", out var distance) || distance.Type != "distance" || !tool.Params.ContainsKey("unit")))
                    throw Invalid("distance_unit argument requires distance and unit parameters in " + tool.Name);
            }
            foreach (var parameter in tool.Params.Values)
            {
                if (parameter.Type == "output_name" && !OutputNamePattern.IsMatch(parameter.Default?.GetString() ?? tool.DefaultName))
                    throw Invalid("Invalid default output name in " + tool.Name);
                if (parameter.Default is JsonElement defaultValue && !MatchesDefault(parameter, defaultValue))
                    throw Invalid("Invalid default for " + tool.Name + "." + parameter.Name);
                if (parameter.RequiredIf is not null)
                    foreach (var condition in parameter.RequiredIf.Keys)
                        if (!tool.Params.ContainsKey(condition)) throw Invalid("required_if references unknown parameter in " + tool.Name);
            }
        }
    }

    private static bool MatchesDefault(GpParam parameter, JsonElement value) => parameter.Type switch
    {
        "enum" => value.ValueKind == JsonValueKind.String && (parameter.Values?.Contains(value.GetString() ?? "") ?? false),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" or "distance" => value.ValueKind == JsonValueKind.Number && InRange(parameter, value.GetDouble()),
        "integer" or "wkid" => value.ValueKind == JsonValueKind.Number && InRange(parameter, value.GetDouble()),
        "string" or "statistics_fields" or "output_name" or "field" => value.ValueKind == JsonValueKind.String,
        _ => true
    };

    public GpTool Tool(string name)
    {
        if (!tools.TryGetValue(name, out var tool)) throw new BridgeException("CAPABILITY_UNAVAILABLE", "Geoprocessing tool is not registered: " + name);
        return tool;
    }

    public NormalizedGp Normalize(GpTool tool, JsonElement parameters)
    {
        if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            throw new BridgeException("INVALID_ARGUMENT", "parameters must be an object");
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parameters.ValueKind == JsonValueKind.Object)
            foreach (var property in parameters.EnumerateObject()) supplied[property.Name] = property.Value;
        if (supplied.Keys.Any(key => !tool.Params.ContainsKey(key)))
            throw new BridgeException("INVALID_ARGUMENT", "Unexpected parameter: " + supplied.Keys.First(key => !tool.Params.ContainsKey(key)));
        if (supplied.Count > maxParameters) throw new BridgeException("INVALID_ARGUMENT", "Too many parameters");
        var normalized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var parameter in tool.Params.Values)
        {
            if (supplied.TryGetValue(parameter.Name, out var value) && value.ValueKind != JsonValueKind.Null)
                normalized[parameter.Name] = NormalizeValue(tool, parameter, value);
            else if (parameter.Default is JsonElement defaultValue)
                normalized[parameter.Name] = defaultValue.Clone();
        }
        foreach (var parameter in tool.Params.Values)
        {
            if (normalized.ContainsKey(parameter.Name)) continue;
            var required = parameter.Required || RequiredByCondition(parameter, tool, normalized);
            if (required) throw new BridgeException("INVALID_ARGUMENT", "Missing required parameter: " + parameter.Name);
        }
        string? outputName = null;
        if (tool.OutputKind is "feature_class" or "table")
        {
            outputName = normalized.TryGetValue(tool.OutputNameParam!, out var nameElement) ? nameElement.GetString() : tool.DefaultName;
            outputName ??= tool.DefaultName;
            if (!OutputNamePattern.IsMatch(outputName)) throw new BridgeException("INVALID_ARGUMENT", "Invalid output_name");
            normalized[tool.OutputNameParam!] = JsonSerializer.SerializeToElement(outputName);
        }
        return new NormalizedGp(normalized, outputName);
    }

    private static bool RequiredByCondition(GpParam parameter, GpTool tool, Dictionary<string, JsonElement> normalized)
    {
        if (parameter.RequiredIf is null) return false;
        foreach (var condition in parameter.RequiredIf)
        {
            if (!normalized.TryGetValue(condition.Key, out var other)) continue;
            if (other.ValueKind != JsonValueKind.String) continue;
            if (condition.Value.Contains(other.GetString() ?? "")) return true;
        }
        return false;
    }

    private JsonElement NormalizeValue(GpTool tool, GpParam parameter, JsonElement value)
    {
        switch (parameter.Type)
        {
            case "dataset":
                return JsonSerializer.SerializeToElement(PathPolicy.Dataset(RequireString(parameter, value)).Path);
            case "dataset_list":
            {
                var paths = StringItems(parameter, value).Select(item => PathPolicy.Dataset(item).Path).ToArray();
                CheckItemCount(parameter, paths.Length);
                return JsonSerializer.SerializeToElement(paths);
            }
            case "field":
                return JsonSerializer.SerializeToElement(RequireField(parameter, value));
            case "field_list":
            {
                var fields = StringItems(parameter, value).Select(item => RequireField(parameter, JsonSerializer.SerializeToElement(item))).ToArray();
                CheckItemCount(parameter, fields.Length);
                return JsonSerializer.SerializeToElement(fields);
            }
            case "number":
            case "distance":
            {
                if (value.ValueKind != JsonValueKind.Number) throw InvalidArgument(parameter, "must be a number");
                var number = value.GetDouble();
                if (!InRange(parameter, number)) throw InvalidArgument(parameter, "is out of range");
                return JsonSerializer.SerializeToElement(number);
            }
            case "integer":
            case "wkid":
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var integer)) throw InvalidArgument(parameter, "must be an integer");
                if (!InRange(parameter, integer)) throw InvalidArgument(parameter, "is out of range");
                return JsonSerializer.SerializeToElement(integer);
            }
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidArgument(parameter, "must be a boolean");
                return value.Clone();
            case "unit":
            case "enum":
            {
                var text = RequireString(parameter, value);
                if (parameter.Values is null || !parameter.Values.Contains(text)) throw InvalidArgument(parameter, "is not an allowed value");
                return JsonSerializer.SerializeToElement(text);
            }
            case "string":
            {
                var text = RequireString(parameter, value);
                if (parameter.Pattern is not null && !Regex.IsMatch(text, parameter.Pattern, RegexOptions.CultureInvariant)) throw InvalidArgument(parameter, "has an invalid format");
                return JsonSerializer.SerializeToElement(text);
            }
            case "statistics_fields":
            {
                var text = RequireString(parameter, value);
                if (!StatisticsPattern.IsMatch(text)) throw InvalidArgument(parameter, "must look like 'field SUM' or 'field1 SUM;field2 MEAN'");
                return JsonSerializer.SerializeToElement(text);
            }
            case "output_name":
            {
                var text = RequireString(parameter, value);
                if (!OutputNamePattern.IsMatch(text)) throw InvalidArgument(parameter, "must match ^[A-Za-z][A-Za-z0-9_]{0,63}$");
                return JsonSerializer.SerializeToElement(text);
            }
            case "map_layer":
            {
                var text = RequireString(parameter, value);
                if (text.Length > 1024) throw InvalidArgument(parameter, "is too long");
                return JsonSerializer.SerializeToElement(text);
            }
            default:
                throw new BridgeException("CAPABILITY_UNAVAILABLE", "Unsupported parameter type: " + parameter.Type);
        }
    }

    private static string RequireString(GpParam parameter, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw InvalidArgument(parameter, "must be a string");
        var text = value.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text)) throw InvalidArgument(parameter, "must not be empty");
        var limit = parameter.MaxLength ?? 4096;
        if (text.Length > limit) throw InvalidArgument(parameter, "is too long");
        return text;
    }

    private static string RequireField(GpParam parameter, JsonElement value)
    {
        var text = RequireString(parameter, value);
        if (!FieldNamePattern.IsMatch(text)) throw InvalidArgument(parameter, "is not a valid field name");
        return text;
    }

    private static IEnumerable<string> StringItems(GpParam parameter, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : throw InvalidArgument(parameter, "must contain strings"));
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        throw InvalidArgument(parameter, "must be a string or an array of strings");
    }

    private void CheckItemCount(GpParam parameter, int count)
    {
        if (parameter.MinItems is int minimum && count < minimum) throw InvalidArgument(parameter, "needs at least " + minimum + " items");
        if (parameter.MaxItems is int maximum && count > maximum) throw InvalidArgument(parameter, "allows at most " + maximum + " items");
        if (count > 64) throw InvalidArgument(parameter, "has too many items");
    }

    private static bool InRange(GpParam parameter, double value) => (parameter.Min is not double min || value >= min) && (parameter.Max is not double max || value <= max);

    private static BridgeException InvalidArgument(GpParam parameter, string message) => new("INVALID_ARGUMENT", "Parameter " + parameter.Name + " " + message);

    public List<GpValue> BuildArguments(GpTool tool, NormalizedGp normalized, string? generatedOutputPath, string? generatedGdbPath)
    {
        var values = new List<GpValue>();
        foreach (var argument in tool.Arguments)
        {
            if (argument.GeneratedOutput)
            {
                if (generatedOutputPath is null) throw new BridgeException("INTERNAL_ERROR", "Output path is not prepared");
                values.Add(new GpValue(GpValueKind.GeneratedOutput, generatedOutputPath));
                continue;
            }
            if (argument.GeneratedGdb)
            {
                if (generatedGdbPath is null) throw new BridgeException("INTERNAL_ERROR", "Output GDB is not prepared");
                values.Add(new GpValue(GpValueKind.GeneratedGdb, generatedGdbPath));
                continue;
            }
            if (argument.DistanceUnit)
            {
                var distance = normalized.Values["distance"].GetDouble();
                var unit = normalized.Values.TryGetValue("unit", out var unitElement) ? unitElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(unit)) throw new BridgeException("INVALID_ARGUMENT", "Distance unit is required");
                values.Add(new GpValue(GpValueKind.Text, distance.ToString("0.##########", CultureInfo.InvariantCulture) + " " + unit));
                continue;
            }
            if (argument.SpatialReferenceParam is not null)
            {
                var wkid = normalized.Values[argument.SpatialReferenceParam].GetInt32();
                values.Add(new GpValue(GpValueKind.SpatialReferenceWkid, wkid));
                continue;
            }
            if (argument.Literal is JsonElement literal)
            {
                values.Add(LiteralValue(literal));
                continue;
            }
            var parameter = tool.Params[argument.Param!];
            if (!normalized.Values.TryGetValue(parameter.Name, out var element))
            {
                values.Add(new GpValue(GpValueKind.Null));
                continue;
            }
            values.Add(ParameterValue(parameter, element));
        }
        while (values.Count > 0 && values[^1].Kind == GpValueKind.Null) values.RemoveAt(values.Count - 1);
        return values;
    }

    private static GpValue LiteralValue(JsonElement literal) => literal.ValueKind switch
    {
        JsonValueKind.String => new GpValue(GpValueKind.Text, literal.GetString()),
        JsonValueKind.Number => literal.TryGetInt32(out var integer) ? new GpValue(GpValueKind.Integer, integer) : new GpValue(GpValueKind.Number, literal.GetDouble()),
        JsonValueKind.True or JsonValueKind.False => new GpValue(GpValueKind.Boolean, literal.GetBoolean()),
        _ => new GpValue(GpValueKind.Null)
    };

    private static GpValue ParameterValue(GpParam parameter, JsonElement value) => parameter.Type switch
    {
        "dataset" or "string" or "enum" or "statistics_fields" or "output_name" or "unit" or "field" => new GpValue(GpValueKind.Text, value.GetString()),
        "dataset_list" or "field_list" => new GpValue(GpValueKind.Text, string.Join(";", value.EnumerateArray().Select(item => item.GetString()))),
        "number" or "distance" => new GpValue(GpValueKind.Number, value.GetDouble()),
        "integer" => new GpValue(GpValueKind.Integer, value.GetInt32()),
        "wkid" => new GpValue(GpValueKind.Integer, value.GetInt32()),
        "boolean" => new GpValue(GpValueKind.Boolean, value.GetBoolean()),
        "map_layer" => new GpValue(GpValueKind.MapLayer, value.GetString()),
        _ => throw new BridgeException("CAPABILITY_UNAVAILABLE", "Unsupported parameter type: " + parameter.Type)
    };

    public object Summary()
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var tool in tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            var parameters = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var parameter in tool.Params.Values.OrderBy(parameter => parameter.Name, StringComparer.Ordinal))
            {
                var spec = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = parameter.Type };
                if (parameter.Required) spec["required"] = true;
                if (parameter.Default is JsonElement defaultValue) spec["default"] = defaultValue.Clone();
                if (parameter.Values is { Length: > 0 }) spec["values"] = parameter.Values;
                if (parameter.Min is not null || parameter.Max is not null) spec["range"] = new[] { parameter.Min, parameter.Max };
                if (parameter.Geometry is { Length: > 0 }) spec["geometry"] = parameter.Geometry;
                if (parameter.MinItems is not null) spec["min_items"] = parameter.MinItems;
                if (parameter.MaxItems is not null) spec["max_items"] = parameter.MaxItems;
                if (parameter.DatasetParam is not null) spec["dataset_param"] = parameter.DatasetParam;
                if (parameter.RequiredIf is not null) spec["required_if"] = parameter.RequiredIf;
                parameters[parameter.Name] = spec;
            }
            result[tool.Name] = new
            {
                label = tool.Label,
                category = tool.Category,
                risk = tool.Risk,
                parameter_order = tool.ParameterOrder,
                @params = parameters,
                output = new { kind = tool.OutputKind, name_param = tool.OutputNameParam, default_name = tool.DefaultName },
                environment = new { output_crs_from = tool.OutputCrsFrom },
                requires_same_gcs = tool.RequiresSameGcs
            };
        }
        return new { catalog_version = CatalogVersion, max_parameters = maxParameters, tools = result };
    }

    private static BridgeException Invalid(string message) => new("CAPABILITY_UNAVAILABLE", message);
    private static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid("Missing string field: " + key);
    private static string[]? StringArray(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? ArrayStrings(value) : null;
    private static string[]? ArrayStrings(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : null;
    private static double? Number(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static int? Int(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}


