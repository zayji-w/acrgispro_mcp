using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeBridge.Native;

internal readonly record struct RasterBounds(double XMin, double YMin, double XMax, double YMax)
{
    public object Summary() => new { xmin = XMin, ymin = YMin, xmax = XMax, ymax = YMax };
    public bool HasArea => XMin < XMax && YMin < YMax
        && double.IsFinite(XMax - XMin) && double.IsFinite(YMax - YMin);
    public bool Overlaps(RasterBounds other) => Math.Max(XMin, other.XMin) < Math.Min(XMax, other.XMax)
        && Math.Max(YMin, other.YMin) < Math.Min(YMax, other.YMax);
    public double[] Coordinates => [XMin, YMin, XMax, YMax];
    public string GpExtent => string.Join(' ', Coordinates.Select(RasterSpec.GpNumber));
}

internal static class RasterSpec
{
    public static bool IsJob(string op) => op is "raster_clip" or "raster_project" or "raster_resample"
        or "raster_index" or "raster_zonal_statistics";

    public static RasterBounds Bounds(JsonElement value)
    {
        var items = value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        return new(items[0], items[1], items[2], items[3]);
    }

    public static bool ValidBounds(JsonElement value) => value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() == 4
        && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number
            && item.TryGetDouble(out var number) && double.IsFinite(number))
        && Bounds(value).HasArea;

    public static string GpNumber(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    public static void Validate(string op, JsonElement args)
    {
        if (!IsJob(op)) return;
        if (Contracts.Text(args, "idempotency_key").Length > 128)
            throw new BridgeException("INVALID_ARGUMENT", "Idempotency key exceeds 128 characters");
        PathPolicy.OutputName(Contracts.OptionalText(args, "output_name") ?? op);
        if (op == "raster_clip" && args.TryGetProperty("mask_path", out _) == args.TryGetProperty("bbox", out _))
            throw new BridgeException("INVALID_ARGUMENT", "Provide exactly one of mask_path and bbox");
        if (Contracts.OptionalText(args, "resampling") is { } resampling
            && resampling.ToUpperInvariant() is not ("NEAREST" or "BILINEAR" or "CUBIC" or "MAJORITY"))
            throw new BridgeException("INVALID_ARGUMENT", "Use NEAREST, BILINEAR, CUBIC or MAJORITY");
        if (Contracts.OptionalText(args, "index") is { } index
            && index.ToUpperInvariant() is not ("NDVI" or "NDWI" or "MNDWI" or "NDMI" or "NDBI"))
            throw new BridgeException("INVALID_ARGUMENT", "Use NDVI, NDWI, MNDWI, NDMI or NDBI");
        if (Contracts.OptionalText(args, "statistics") is { } stats
            && stats.ToUpperInvariant() is not ("ALL" or "MEAN" or "MINIMUM" or "MAXIMUM" or "SUM"
                or "STD" or "MEDIAN" or "RANGE" or "MIN_MAX" or "MIN_MAX_MEAN"))
            throw new BridgeException("INVALID_ARGUMENT", "Unsupported zonal statistic");
        if (Contracts.OptionalText(args, "transform_method") is { } transform
            && !Regex.IsMatch(transform, @"\A~?[A-Za-z][A-Za-z0-9_]{0,127}\z"))
            throw new BridgeException("INVALID_ARGUMENT", "Provide a single ArcGIS geographic transformation name");
        if (Contracts.OptionalText(args, "zone_field") is { Length: > 128 })
            throw new BridgeException("INVALID_ARGUMENT", "Provide one zone field name");
    }

    public static bool SameSize(double left, double right) => Math.Abs(left - right) <= Math.Max(left, right) * 1e-7;
    public static bool Aligned(double coordinate, double origin, double size)
    {
        var cells = (coordinate - origin) / size;
        return Math.Abs(cells - Math.Round(cells)) <= 1e-6;
    }
}
