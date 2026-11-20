using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
namespace SafeBridge.Native;
internal static class DemoOperations
{
    private static readonly List<(Map Map,GraphicsLayer Layer,string Project)> owned=[];
    public static object Add()
    {
        var view=MapView.Active??throw new BridgeException("NO_ACTIVE_VIEW","Activate a 2D map");
        if(view.Map.MapType!=MapType.Map)throw new BridgeException("INVALID_ARGUMENT","Only 2D maps supported");
        var prior=owned.FirstOrDefault(x=>ReferenceEquals(x.Map,view.Map)&&view.Map.GetLayersAsFlattenedList().Contains(x.Layer));
        if(prior.Layer is not null)return new{already_added=true,name=prior.Layer.Name};
        var e=view.Extent;if(e is null||e.Width<=0||e.Height<=0)throw new BridgeException("NO_ACTIVE_VIEW","Map extent is unavailable");
        var layer=LayerFactory.Instance.CreateLayer<GraphicsLayer>(new GraphicsLayerCreationParams{Name="Codex_Live_Demo_"+Guid.NewGuid().ToString("N")[..8]},view.Map);
        try
        {
            var symbol=SymbolFactory.Instance.ConstructPointSymbol(ColorFactory.Instance.CreateRGBColor(0,190,235),22,SimpleMarkerStyle.Circle);
            foreach(var xy in new[]{(.3,.35),(.5,.65),(.7,.35)})layer.AddElement(new CIMPointGraphic{Location=MapPointBuilderEx.CreateMapPoint(e.XMin+e.Width*xy.Item1,e.YMin+e.Height*xy.Item2,e.SpatialReference),Symbol=symbol.MakeSymbolReference()});
            owned.Add((view.Map,layer,Project.Current.URI));return new{name=layer.Name,uri=layer.URI,graphics=3,project_saved=false};
        }
        catch{view.Map.RemoveLayer(layer);throw;}
    }
    public static object Remove()
    {
        if(Project.Current is null)throw new BridgeException("NO_PROJECT","Open a project");var names=new List<string>();
        foreach(var item in owned.Where(x=>x.Project==Project.Current.URI).ToArray())
        {if(item.Map.GetLayersAsFlattenedList().Contains(item.Layer)){names.Add(item.Layer.Name);item.Map.RemoveLayer(item.Layer);}owned.Remove(item);}
        return new{removed=names,project_saved=false};
    }
}
