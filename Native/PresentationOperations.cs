using System.Security;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
namespace SafeBridge.Native;

internal static class PresentationOperations
{
    private static FeatureLayer Layer(JsonElement a)=>MapOperations.FeatureLayer(Contracts.Text(a,"project_id"),Contracts.Text(a,"map_id"),Contracts.Text(a,"layer_id"));
    private static CIMColor Color(string hex)=>ColorFactory.Instance.CreateRGBColor(Convert.ToInt32(hex[1..3],16),Convert.ToInt32(hex[3..5],16),Convert.ToInt32(hex[5..7],16));
    private static CIMTextSymbol Text(double size,string color="#222222",string font="Microsoft YaHei")=>SymbolFactory.Instance.ConstructTextSymbol(Color(color),size,font,"Regular");
    public static object State(JsonElement a)
    {
        var layer=Layer(a);var def=(CIMFeatureLayer)layer.GetDefinition();
        return new{layer_id=layer.URI,renderer=layer.GetRenderer()?.ToJson(),labels_visible=layer.IsLabelVisible,label_classes=def.LabelClasses?.Select(c=>new{name=c.Name,expression=c.Expression,visible=c.Visibility,where_clause=c.WhereClause,text_symbol=c.TextSymbol?.ToJson()}).ToArray()};
    }
    public static object Style(JsonElement a)
    {
        var layer=Layer(a);var hex=PresentationSpec.Color(a,"color","#4C78A8");var color=Color(hex);
        var outline=SymbolFactory.Instance.ConstructStroke(Color(PresentationSpec.Color(a,"outline_color","#333333")),PresentationSpec.Number(a,"outline_width",0.5,0,10),SimpleLineStyle.Solid);
        CIMSymbol symbol=layer.ShapeType switch
        {
            esriGeometryType.esriGeometryPolygon=>SymbolFactory.Instance.ConstructPolygonSymbol(color,SimpleFillStyle.Solid,outline),
            esriGeometryType.esriGeometryPolyline=>SymbolFactory.Instance.ConstructLineSymbol(color,PresentationSpec.Number(a,"width",1.5,0.1,20),SimpleLineStyle.Solid),
            esriGeometryType.esriGeometryPoint or esriGeometryType.esriGeometryMultipoint=>SymbolFactory.Instance.ConstructPointSymbol(color,PresentationSpec.Number(a,"size",8,1,72),SimpleMarkerStyle.Circle),
            _=>throw new BridgeException("INVALID_GEOMETRY","Simple symbols support point, line and polygon feature layers")
        };
        layer.SetRenderer(new CIMSimpleRenderer{Symbol=symbol.MakeSymbolReference()});
        return new{layer_id=layer.URI,renderer="simple",color=hex,project_saved=false};
    }
    public static object Labels(JsonElement a)
    {
        var layer=Layer(a);var field=Contracts.Text(a,"field");
        using(var table=layer.GetTable())using(var definition=table.GetDefinition())
            if(!definition.GetFields().Any(f=>f.Name==field))throw new BridgeException("FIELD_NOT_FOUND","Label field not found: "+field);
        var def=layer.GetDefinition() as CIMFeatureLayer??throw new BridgeException("INVALID_ARGUMENT","Feature layer CIM is unavailable");
        var classes = def.LabelClasses ?? [];
        var className = Contracts.OptionalText(a, "class_name");
        var visible = Contracts.Bool(a, "visible", true);
        CIMLabelClass label;
        if (className is null)
        {
            label = classes.FirstOrDefault() ?? new CIMLabelClass { Name = "GIS-Agent labels" };
            if (classes.Length == 0) classes = [label];
        }
        else
        {
            var matches = classes.Where(candidate => candidate.Name == className).ToArray();
            if (matches.Length == 0) throw new BridgeException("LABEL_CLASS_NOT_FOUND", "No label class with that exact name");
            if (matches.Length > 1) throw new BridgeException("AMBIGUOUS_LABEL_CLASS", "More than one label class has that name");
            label = matches[0];
        }
        // Only a literal field reference; callers cannot submit an expression or script.
        label.ExpressionEngine=LabelExpressionEngine.Arcade;label.Expression="$feature["+JsonSerializer.Serialize(field)+"]";
        label.TextSymbol=Text(PresentationSpec.Number(a,"font_size",10,4,72),PresentationSpec.Color(a,"color","#222222"),Contracts.OptionalText(a,"font_family")??"Microsoft YaHei").MakeSymbolReference();
        label.Visibility = className is null || visible;
        def.LabelClasses = classes;
        if (className is null || visible) def.LabelVisibility = visible;
        layer.SetDefinition(def);
        return new{layer_id=layer.URI,field,visible,expression=label.Expression,project_saved=false,class_name=label.Name,labels_visible=layer.IsLabelVisible,label_class_count=classes.Length};
    }
    public static Layout Resolve(string projectId,string layoutId)
    {
        if(MapOperations.ProjectId()!=projectId)throw new BridgeException("STALE_CONTEXT","The project changed; query map state again");
        return Project.Current.GetItems<LayoutProjectItem>().Select(i=>i.GetLayout()).FirstOrDefault(l=>l.URI==layoutId)
            ??throw new BridgeException("LAYOUT_NOT_FOUND","Layout no longer exists; call pro_list_layouts");
    }
    public static object List(JsonElement args)
    {
        var projectId = MapOperations.ProjectId();
        var expected = Contracts.OptionalText(args, "project_id");
        if (expected is not null && expected != projectId)
            throw new BridgeException("STALE_CONTEXT", "The project changed; query map state again");
        var items = Project.Current.GetItems<LayoutProjectItem>().ToArray();
        if (!Contracts.IsPaged(args))
            return new { project_id=projectId, layouts=items.Select(i=>i.GetLayout()).Select(l=>new{layout_id=l.URI,name=l.Name,page=l.GetPage(),elements=l.GetElementsAsFlattenedList().Select(e=>new{name=e.Name,type=e.GetType().Name}).ToArray()}).ToArray(), project_saved=false };

        var (offset, limit) = Contracts.Page(args);
        var layouts = items.Skip(offset).Take(limit).Select(i => i.GetLayout()).Select(l => new
        {
            layout_id=l.URI, name=l.Name, page=l.GetPage(), element_count=l.GetElementsAsFlattenedList().Count
        }).ToArray();
        return new { project_id=projectId, layouts, pagination=Contracts.PageInfo(offset,limit,items.Length), project_saved=false };
    }
    public static object Create(JsonElement a)
    {
        var map=MapOperations.Resolve(Contracts.Text(a,"project_id"),Contracts.Text(a,"map_id"));
        var layer=MapOperations.FeatureLayer(Contracts.Text(a,"project_id"),map.URI,Contracts.Text(a,"extent_layer_id"));
        var original=layer.QueryExtent();
        if(original is null||original.IsEmpty||original.SpatialReference is null||original.SpatialReference.IsUnknown||map.SpatialReference.IsUnknown)
            throw new BridgeException("UNKNOWN_CRS","Study area and map need known coordinate systems");
        var extent=(Envelope)GeometryEngine.Instance.Project(original,map.SpatialReference);
        var page=PresentationSpec.Page(extent.Width,extent.Height,Contracts.OptionalText(a,"page_size")??"A4",Contracts.OptionalText(a,"orientation")??"auto");
        var name=Contracts.Text(a,"name");
        if(Project.Current.GetItems<LayoutProjectItem>().Any(i=>string.Equals(i.Name,name,StringComparison.OrdinalIgnoreCase)))throw new BridgeException("OUTPUT_CONFLICT","A layout with this name already exists");
        var layout=LayoutFactory.Instance.CreateLayout(page.Width,page.Height,LinearUnit.Centimeters);layout.SetName(name);
        try
        {
            var w=page.Width;var h=page.Height;
            var hasLegend=Contracts.Bool(a,"legend",true);var mapRight=hasLegend?w-5.3:w-1;
            Envelope Box(double x,double y,double x2,double y2)=>EnvelopeBuilderEx.CreateEnvelope(x,y,x2,y2);
            var frame=ElementFactory.Instance.CreateMapFrameElement(layout,Box(1,2.5,mapRight,h-2.3),map,"GIS-Agent Map",false);
            // Padding prevents the boundary from coinciding with the map frame.
            var padded=EnvelopeBuilderEx.CreateEnvelope(extent.XMin-extent.Width*.06,extent.YMin-extent.Height*.06,extent.XMax+extent.Width*.06,extent.YMax+extent.Height*.06,extent.SpatialReference);
            frame.SetCamera(padded);
            var title=SecurityElement.Escape(Contracts.Text(a,"title"))!;
            ElementFactory.Instance.CreateTextGraphicElement(layout,TextType.RectangleParagraph,Box(1,h-1.9,w-1,h-.6),Text(22),title,"GIS-Agent Title",false);
            if(hasLegend)
                ElementFactory.Instance.CreateMapSurroundElement(layout,Box(w-4.8,4,w-1,h-5),new LegendInfo{MapFrameName=frame.Name},"GIS-Agent Legend",false);
            if(Contracts.Bool(a,"scale_bar",true))
                ElementFactory.Instance.CreateMapSurroundElement(layout,Box(1,1.1,7,1.8),new ScaleBarInfo{MapFrameName=frame.Name},"GIS-Agent Scale Bar",false);
            if(Contracts.Bool(a,"north_arrow",true))
                ElementFactory.Instance.CreateMapSurroundElement(layout,Box(w-3,h-4.6,w-1.7,h-2.8),new NorthArrowInfo{MapFrameName=frame.Name},"GIS-Agent North Arrow",false);
            return new{layout_id=layout.URI,name,orientation=page.Orientation,page_size=Contracts.OptionalText(a,"page_size")??"A4",width_cm=w,height_cm=h,extent_layer_id=layer.URI,orientation_basis="study-area extent projected to map CRS",aspect_ratio=extent.Width/extent.Height,study_extent=DatasetOperations.Extent(extent),map_id=map.URI,project_saved=false};
        }
        catch
        {
            // Remove only the newly created incomplete layout; preserve all existing layouts.
            var item=Project.Current.GetItems<LayoutProjectItem>().FirstOrDefault(i=>i.GetLayout().URI==layout.URI);
            if(item is not null)Project.Current.RemoveItem(item);
            throw;
        }
    }
    public static async Task<object> Open(JsonElement a)
    {
        var layout=await QueuedTask.Run(()=>Resolve(Contracts.Text(a,"project_id"),Contracts.Text(a,"layout_id")));
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(async()=>await ProApp.Panes.CreateLayoutPaneAsync(layout)).Task.Unwrap();
        return new{layout_id=layout.URI,opened=true,project_saved=false};
    }
}
