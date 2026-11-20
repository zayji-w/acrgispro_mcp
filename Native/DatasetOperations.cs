using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Exceptions;
using ArcGIS.Core.Geometry;
namespace SafeBridge.Native;

internal static class DatasetOperations
{
    // Caller must be on the MCT. All data handles are disposed before returning.
    public static T WithFeatureClass<T>(string path,Func<FeatureClass,T> action)
        => WithDataset(PathPolicy.Dataset(path), action);

    public static T WithTable<T>((string Path, string Store, string Name, bool Shape) dataset, Func<Table,T> action)
        => WithDataset(dataset, action);

    private static T WithDataset<TDataset,T>((string Path,string Store,string Name,bool Shape) d, Func<TDataset,T> action)
        where TDataset : Table
    {
        try
        {
            if(d.Shape)
            {
                using var store=new FileSystemDatastore(new FileSystemConnectionPath(new Uri(d.Store),FileSystemDatastoreType.Shapefile));
                using var table=store.OpenDataset<TDataset>(d.Name);return action(table);
            }
            using var gdb=new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(d.Store)));
            var parts = d.Name.Split(Path.DirectorySeparatorChar);
            if (parts.Length == 2)
            {
                using var group = gdb.OpenDataset<FeatureDataset>(parts[0]);
                using var feature = group.OpenDataset<FeatureClass>(parts[1]);
                return action((TDataset)(Table)feature);
            }
            if (parts.Length != 1)
                throw new BridgeException("DATASET_NOT_FOUND", "Use a table, feature class or feature dataset member path");
            using var dataset=gdb.OpenDataset<TDataset>(d.Name);return action(dataset);
        }
        catch(GeodatabaseException ex){throw new BridgeException("DATASET_NOT_FOUND",ex.Message);}
    }
    public static object? Extent(Envelope? e)=>e is null||e.IsEmpty?null:new{xmin=e.XMin,ymin=e.YMin,xmax=e.XMax,ymax=e.YMax};
    public static object Describe(string path,bool count)
    {
        var d=PathPolicy.Dataset(path);
        return WithFeatureClass(d.Path,fc=>
        {
            using var def=fc.GetDefinition();var sr=def.GetSpatialReference();
            return (object)new{exists=true,path=d.Path,kind=d.Shape?"shapefile":"feature_class",geometry_type=def.GetShapeType().ToString(),
                crs=new{wkid=sr.Wkid,wkt=sr.Wkt,unknown=sr.IsUnknown,gcs_wkid=sr.GcsWkid},extent=Extent(fc.GetExtent()),
                fields=def.GetFields().Select(f=>new{name=f.Name,type=f.FieldType.ToString()}).ToArray(),count=count?(long?)fc.GetCount():null};
        });
    }
    public static object DescribeTable(string path,bool count)
    {
        var d=PathPolicy.Dataset(path);
        return WithTable(d, table =>
        {
            using var def=table.GetDefinition();
            return (object)new{exists=true,path=d.Path,kind="table",fields=def.GetFields().Select(f=>new{name=f.Name,type=f.FieldType.ToString()}).ToArray(),count=count?(long?)table.GetCount():null};
        });
    }
    public static string[] ListFeatureClasses(string gdbPath)
    {
        var full=PathPolicy.Local(gdbPath);
        if(!full.EndsWith(".gdb",StringComparison.OrdinalIgnoreCase)||!Directory.Exists(full))throw new BridgeException("DATASET_NOT_FOUND","File GDB is missing: "+full);
        try
        {
            using var gdb=new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(full)));
            var names=new List<string>();
            foreach(var definition in gdb.GetDefinitions<FeatureClassDefinition>())
            {
                using(definition)names.Add(definition.GetName());
            }
            return names.Select(name=>Path.Combine(full,name)).OrderBy(name=>name,StringComparer.Ordinal).ToArray();
        }
        catch(GeodatabaseException ex){throw new BridgeException("DATASET_NOT_FOUND",ex.Message);}
    }
    public static Dictionary<string,string> Fields(string path)=>WithFeatureClass(path,fc=>
    {
        using var def=fc.GetDefinition();
        return def.GetFields().ToDictionary(f=>f.Name,f=>f.FieldType.ToString(),StringComparer.OrdinalIgnoreCase);
    });
    public static (GeometryType Type,SpatialReference Sr) GeometryInfo(string path)=>WithFeatureClass(path,fc=>
    {using var def=fc.GetDefinition();return(def.GetShapeType(),def.GetSpatialReference());});
}
