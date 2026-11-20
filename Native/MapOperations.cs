using ArcGIS.Desktop.Core;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Mapping;
using System.Text.Json;
namespace SafeBridge.Native;

internal static class MapOperations
{
    private static readonly object ProjectIdentity = new();
    private static Project? knownProject;
    private static string? knownUri;
    private static string identity="";
    public static void ResetProject()
    {
        lock (ProjectIdentity)
        {
            knownProject = null;
            knownUri = null;
            identity = "";
        }
    }
    public static string ProjectId()
    {
        lock (ProjectIdentity)
        {
            var project=Project.Current??throw new BridgeException("NO_PROJECT","Open a project first");
            if (!ReferenceEquals(project, knownProject) || !string.Equals(knownUri, project.URI, StringComparison.OrdinalIgnoreCase))
            {
                knownProject = project;
                knownUri = project.URI;
                identity = Guid.NewGuid().ToString("N");
            }
            return identity;
        }
    }
    public static Map Resolve(string projectId,string mapId)
    {
        if(ProjectId()!=projectId)throw new BridgeException("STALE_CONTEXT","The project changed; query map state again");
        var map=Project.Current.GetItems<MapProjectItem>().Select(i=>i.GetMap()).FirstOrDefault(m=>m.URI==mapId);
        if(map is null)throw new BridgeException("STALE_CONTEXT","The target map no longer exists");
        if(map.MapType!=MapType.Map)throw new BridgeException("INVALID_ARGUMENT","Only 2D maps are supported");
        return map;
    }
    public static string? Source(Layer layer)
    {
        if(layer is RasterLayer raster)
        {
            try { if(RasterData.LayerSource(raster) is { } source)return source; }
            catch { /* Fall back to the SDK path for an unsupported raster data connection. */ }
        }
        try
        {
            var uri=layer.GetPath();if(uri is null)return null;
            if(!uri.IsFile)return uri.AbsoluteUri;
            var path=uri.LocalPath;
            // Pro omits the .shp suffix from GetPath() for shapefile layers.
            if(!path.EndsWith(".shp",StringComparison.OrdinalIgnoreCase)&&File.Exists(path+".shp"))path+=".shp";
            return Path.GetFullPath(path);
        }
        catch{return null;}
    }
    private static object LayerState(Layer layer) => new
    {
        layer_id=layer.URI, uri=layer.URI, name=layer.Name, visible=layer.IsVisible,
        layer_type=layer.GetType().Name, source=Source(layer)
    };
    public static object State(JsonElement args)
    {
        var expected = Contracts.OptionalText(args, "project_id");
        if (Project.Current is null)
        {
            if (expected is not null) throw new BridgeException("STALE_CONTEXT", "The project is no longer open");
            return new { project_id = (string?)null, reason = "no_project", maps = Array.Empty<object>(), active_map_id = (string?)null };
        }
        var pid = ProjectId();
        if (expected is not null && expected != pid)
            throw new BridgeException("STALE_CONTEXT", "The project changed; query map state again");

        var requestedMap = Contracts.OptionalText(args, "map_id");
        var maps = Project.Current.GetItems<MapProjectItem>().Select(item => item.GetMap()).ToArray();
        var (offset, limit) = Contracts.Page(args);
        object MapWithLayers(Map map, IEnumerable<Layer> layers) => new
        {
            map_id=map.URI, uri=map.URI, name=map.Name, type=map.MapType.ToString(),
            layers=layers.Select(LayerState).ToArray()
        };
        object Result(object[] rows, object? pagination = null)
        {
            var result = new Dictionary<string, object?>
            {
                ["project_id"]=pid, ["project_path"]=Project.Current.URI,
                ["active_map_id"]=MapView.Active?.Map?.URI, ["active_map"]=MapView.Active?.Map?.Name,
                ["maps"]=rows, ["project_saved_by_bridge"]=false, ["backend"]="native-sdk"
            };
            if (pagination is not null) result["pagination"] = pagination;
            return result;
        }
        if (requestedMap is not null)
        {
            var map = maps.FirstOrDefault(candidate => candidate.URI == requestedMap)
                ?? throw new BridgeException("STALE_CONTEXT", "The target map no longer exists");
            var layers = map.GetLayersAsFlattenedList();
            return Result([MapWithLayers(map, layers.Skip(offset).Take(limit))], Contracts.PageInfo(offset, limit, layers.Count));
        }
        if (!Contracts.IsPaged(args))
            return Result(maps.Select(map => MapWithLayers(map, map.GetLayersAsFlattenedList())).ToArray());

        var summaries = maps.Skip(offset).Take(limit).Select(map => (object)new
        {
            map_id=map.URI, uri=map.URI, name=map.Name, type=map.MapType.ToString(),
            layer_count=map.GetLayersAsFlattenedList().Count
        }).ToArray();
        return Result(summaries, Contracts.PageInfo(offset, limit, maps.Length));
    }
    public static object Add(string projectId,string mapId,string path,string? name=null)
    {
        var map=Resolve(projectId,mapId);var canonical=PathPolicy.Dataset(path).Path;
        // Validate the source before changing the map.
        DatasetOperations.GeometryInfo(canonical);
        var existing=map.GetLayersAsFlattenedList().OfType<FeatureLayer>().FirstOrDefault(l=>string.Equals(Source(l),canonical,StringComparison.OrdinalIgnoreCase));
        var layer=existing??LayerFactory.Instance.CreateLayer<FeatureLayer>(new FeatureLayerCreationParams(new Uri(canonical)),map);
        if(existing is null && name is not null)layer.SetName(name);
        return new{layer_id=layer.URI,map_id=map.URI,canonical_source=canonical,already_present=existing is not null,name=layer.Name};
    }
    public static Layer Layer(string projectId,string mapId,string layerId)
    {
        var layer=Resolve(projectId,mapId).GetLayersAsFlattenedList().FirstOrDefault(l=>l.URI==layerId);
        return layer??throw new BridgeException("STALE_CONTEXT","Target layer no longer exists");
    }
    public static object Visibility(JsonElement args)
    {
        var layer=Layer(Contracts.Text(args,"project_id"),Contracts.Text(args,"map_id"),Contracts.Text(args,"layer_id"));
        layer.SetVisibility(Contracts.Bool(args,"visible"));return new{layer_id=layer.URI,visible=layer.IsVisible};
    }
    public static object Zoom(JsonElement args)
    {
        var layer=Layer(Contracts.Text(args,"project_id"),Contracts.Text(args,"map_id"),Contracts.Text(args,"layer_id"));
        var view=MapView.Active;
        if(view is null||view.Map.URI!=Contracts.Text(args,"map_id"))throw new BridgeException("NO_ACTIVE_VIEW","Activate a view of the specified map first");
        var applied=view.ZoomTo(layer,false,TimeSpan.Zero);
        return new{applied,layer_id=layer.URI,extent=DatasetOperations.Extent(view.Extent)};
    }
    public static FeatureLayer FeatureLayer(string projectId,string mapId,string layerId)
    {
        var layer=Resolve(projectId,mapId).GetLayersAsFlattenedList().FirstOrDefault(l=>l.URI==layerId);
        return layer as FeatureLayer??throw new BridgeException("STALE_CONTEXT","Target feature layer no longer exists");
    }
    public static (string Status,string[] LayerIds,string? Error) BindOutput(string projectId,string mapId,string path,string? name)
    {
        var map=Resolve(projectId,mapId);
        // GP is submitted without automatic layer insertion. Only the requested map is edited.
        var matches=map.GetLayersAsFlattenedList().Where(l=>string.Equals(Source(l),path,StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach(var duplicate in matches.Skip(1))map.RemoveLayer(duplicate);
        if(matches.Length>0)return("attached",new[]{matches[0].URI},null);
        var added=JsonSerializer.SerializeToElement(Add(projectId,mapId,path,name));
        return("attached",new[]{added.GetProperty("layer_id").GetString()!},null);
    }
}
