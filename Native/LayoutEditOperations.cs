using System.Security;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
namespace SafeBridge.Native;

internal static class LayoutEditOperations
{
    private static double Factor(Layout layout)=>LayoutEditSpec.CmPerUnit(layout.GetPage().Units?.ConversionFactor??0);
    private static Layout Layout(JsonElement a)=>PresentationOperations.Resolve(Contracts.Text(a,"project_id"),Contracts.Text(a,"layout_id"));
    private static Element Find(Layout layout,string name)
    {
        var matches=layout.GetElementsAsFlattenedList().Where(e=>e.Name==name).ToArray();
        if(matches.Length==0)throw new BridgeException("ELEMENT_NOT_FOUND","Element no longer exists: "+name);
        if(matches.Length!=1)throw new BridgeException("AMBIGUOUS_ELEMENT","Element name is not unique; rename duplicates in Pro first: "+name);
        return matches[0];
    }
    // Both point and paragraph text inherit CIMTextGraphicBase, not each other.
    private static CIMTextGraphicBase? TextGraphic(Element e)=>e is GraphicElement graphic?graphic.GetGraphic() as CIMTextGraphicBase:null;
    private static object State(Element e,double f)
    {
        var bounds=e.GetBounds();var text=TextGraphic(e);var symbol=text?.Symbol?.Symbol as CIMTextSymbol;
        var legend=e.GetDefinition() as CIMLegend;
        return new{name=e.Name,type=e.GetType().Name,x_cm=bounds.XMin*f,y_cm=bounds.YMin*f,width_cm=e.GetWidth()*f,height_cm=e.GetHeight()*f,content_width_cm=bounds.Width*f,content_height_cm=bounds.Height*f,rotation=e.GetRotation(),locked=e.IsLocked,visible=e.GetDefinition().Visible,text=text?.Text,font_size=symbol?.Height,legend_fit=legend?.FittingStrategy.ToString(),legend_style=legend is null?null:new{patch_width=legend.DefaultPatchWidth,patch_height=legend.DefaultPatchHeight,title_font=(legend.TitleSymbol?.Symbol as CIMTextSymbol)?.Height,items=legend.Items?.Select(i=>new{name=i.Name,label_font=(i.LabelSymbol?.Symbol as CIMTextSymbol)?.Height,patch_width=i.PatchWidth,patch_height=i.PatchHeight}).ToArray()}};
    }
    public static object List(JsonElement a)
    {
        var layout=Layout(a);var f=Factor(layout);var elements=layout.GetElementsAsFlattenedList();
        var paged = Contracts.IsPaged(a);
        var (offset, limit) = Contracts.Page(a);
        var page = paged ? elements.Skip(offset).Take(limit).ToArray() : elements.ToArray();
        var names = page.Select(e => e.Name).ToHashSet();
        var result = new Dictionary<string, object>
        {
            ["layout_id"]=layout.URI, ["page_width_cm"]=layout.GetPage().Width*f, ["page_height_cm"]=layout.GetPage().Height*f,
            ["coordinate_units"]="centimeters", ["coordinate_origin"]="page bottom-left; element x/y are unrotated bounds bottom-left",
            ["duplicate_names"]=elements.GroupBy(e=>e.Name).Where(g=>names.Contains(g.Key)&&g.Count()>1).Select(g=>g.Key).ToArray(),
            ["elements"]=page.Select(e=>State(e,f)).ToArray(), ["project_saved"]=false
        };
        if (paged) result["pagination"] = Contracts.PageInfo(offset,limit,elements.Count);
        return result;
    }
    private static double? Optional(JsonElement a,string key)=>a.TryGetProperty(key,out var v)?v.GetDouble():null;
    private static void MoveBounds(Element e,double x,double y)
    {
        var b=e.GetBounds();e.SetX(e.GetX()+x-b.XMin);e.SetY(e.GetY()+y-b.YMin);
    }
    private static void ScaleLegend(CIMLegend legend,double scale)
    {
        // Fitting a larger frame only shrinks text; scale the explicit content too.
        // A symbol may be shared by multiple roles, so scale each object once.
        var seen=new HashSet<CIMTextSymbol>(ReferenceEqualityComparer.Instance);
        void Font(CIMSymbolReference? reference)
        {
            if(reference?.Symbol is not CIMTextSymbol font||!seen.Add(font))return;
            var size=font.Height*scale;
            if(size is <4 or >144)throw new BridgeException("INVALID_ARGUMENT","Scaled legend font size must be 4-144 points");
            font.Height=size;
        }
        double Length(double value)=>value>0?value*scale:value; // Preserve automatic/sentinel values.
        void Item(CIMLegendItem item)
        {
            Font(item.LayerNameSymbol);Font(item.HeadingSymbol);Font(item.LabelSymbol);Font(item.DescriptionSymbol);Font(item.GroupLayerNameSymbol);
            item.PatchWidth=Length(item.PatchWidth);item.PatchHeight=Length(item.PatchHeight);
            item.ClassIndent=Length(item.ClassIndent);item.HeadingIndent=Length(item.HeadingIndent);item.LayerNameIndent=Length(item.LayerNameIndent);
        }
        Font(legend.TitleSymbol);
        if(legend.DefaultLegendItem is not null)Item(legend.DefaultLegendItem);
        foreach(var item in legend.Items??[])Item(item);
        legend.DefaultPatchWidth=Length(legend.DefaultPatchWidth);legend.DefaultPatchHeight=Length(legend.DefaultPatchHeight);
        legend.DescriptionWidth=Length(legend.DescriptionWidth);legend.LabelWidth=Length(legend.LabelWidth);
        legend.GroupGap=Length(legend.GroupGap);legend.HeadingGap=Length(legend.HeadingGap);legend.HorizontalItemGap=Length(legend.HorizontalItemGap);
        legend.PatchGap=Length(legend.PatchGap);legend.TextGap=Length(legend.TextGap);legend.TitleGap=Length(legend.TitleGap);
        legend.ItemGap=Length(legend.ItemGap);legend.ClassGap=Length(legend.ClassGap);legend.LayerNameGap=Length(legend.LayerNameGap);legend.GroupLayerNameGap=Length(legend.GroupLayerNameGap);
        legend.MinFontSize=Length(legend.MinFontSize);
        legend.FittingStrategy=LegendFittingStrategy.AdjustFrame;
    }
    private static void Apply(Element element,JsonElement edit,double f)
    {
        var before=element.GetBounds();var x=Optional(edit,"x_cm")/f??before.XMin;var y=Optional(edit,"y_cm")/f??before.YMin;
        var scale=Optional(edit,"scale");var width=Optional(edit,"width_cm")/f;var height=Optional(edit,"height_cm")/f;
        if(scale.HasValue)
        {
            if(element.GetDefinition() is CIMLegend scaledLegend){ScaleLegend(scaledLegend,scale.Value);element.SetDefinition(scaledLegend);}
            else{width=element.GetWidth()*scale.Value;height=element.GetHeight()*scale.Value;}
        }
        var graphic=TextGraphic(element);
        if(graphic is not null&&(edit.TryGetProperty("text",out _)||edit.TryGetProperty("font_size",out _)||scale.HasValue))
        {
            if(edit.TryGetProperty("text",out var text))graphic.Text=SecurityElement.Escape(text.GetString()!)!;
            if(graphic.Symbol?.Symbol is CIMTextSymbol symbol)
            {
                symbol.Height=Optional(edit,"font_size")??symbol.Height*(scale??1);
                if(symbol.Height is <4 or >144)throw new BridgeException("INVALID_ARGUMENT","Scaled font size must be 4-144 points");
            }
            ((GraphicElement)element).SetGraphic(graphic);
        }
        if(width.HasValue||height.HasValue)
        {
            if(element.GetDefinition() is CIMLegend legend){legend.FittingStrategy=LegendFittingStrategy.AdjustSize;element.SetDefinition(legend);}
            var locked=element.GetLockedAspectRatio();element.SetLockedAspectRatio(false);
            try{if(width.HasValue)element.SetWidth(width.Value);if(height.HasValue)element.SetHeight(height.Value);}
            finally{element.SetLockedAspectRatio(locked);}
        }
        if(edit.TryGetProperty("rotation",out var angle))element.SetRotation(angle.GetDouble());
        if(edit.TryGetProperty("visible",out var visible))element.SetVisible(visible.GetBoolean());
        MoveBounds(element,x,y);
    }
    public static object Update(JsonElement a)
    {
        var layout=Layout(a);var f=Factor(layout);var allowLocked=Contracts.Bool(a,"allow_locked");
        var targets=a.GetProperty("edits").EnumerateArray().Select(edit=>(Element:Find(layout,Contracts.Text(edit,"element_name")),Edit:edit)).ToArray();
        // Resolve and validate every target before changing any element.
        foreach(var (element,edit) in targets)
        {
            if(element.IsLocked&&!allowLocked)throw new BridgeException("ELEMENT_LOCKED","Element is locked: "+element.Name);
            if(edit.TryGetProperty("expected_type",out var type)&&element.GetType().Name!=type.GetString())throw new BridgeException("STALE_CONTEXT","Element type changed: "+element.Name);
            if((edit.TryGetProperty("text",out _)||edit.TryGetProperty("font_size",out _))&&TextGraphic(element) is null)throw new BridgeException("INVALID_ARGUMENT","Text/font changes require a text element: "+element.Name);
            if(element is GroupElement)throw new BridgeException("INVALID_ARGUMENT","Edit individual group members explicitly");
        }
        var originals=targets.Select(t=>(t.Element,Definition:(CIMElement)CIMObject.Clone(t.Element.GetDefinition()))).ToArray();
        try
        {
            foreach(var (element,edit) in targets)
            {
                bool locked=element.IsLocked;if(locked)element.SetLocked(false);
                try{Apply(element,edit,f);}finally{if(locked)element.SetLocked(true);}
            }
        }
        catch(Exception failure)
        {
            var errors=new List<string>();foreach(var (element,definition) in originals.Reverse())try{element.SetDefinition(definition);}catch(Exception ex){errors.Add(ex.Message);}
            if(errors.Count>0)throw new BridgeException("PARTIAL_LAYOUT_UPDATE","Failed to restore all edited elements",new{failure=failure.Message,restore_errors=errors});
            throw;
        }
        return new{layout_id=layout.URI,elements=targets.Select(t=>State(t.Element,f)).ToArray(),coordinate_units="centimeters",project_saved=false};
    }
    private static GraphicElement AddText(Layout layout,string name,string text,double x,double y,double? width,double? height,double rotation,double size,string font,string hex,double f)
    {
        var color=ColorFactory.Instance.CreateRGBColor(Convert.ToInt32(hex[1..3],16),Convert.ToInt32(hex[3..5],16),Convert.ToInt32(hex[5..7],16));
        var symbol=SymbolFactory.Instance.ConstructTextSymbol(color,size,font,"Regular");
        Geometry geometry=width.HasValue?EnvelopeBuilderEx.CreateEnvelope(x/f,y/f,(x+width.Value)/f,(y+height!.Value)/f):MapPointBuilderEx.CreateMapPoint(x/f,y/f);
        var element=ElementFactory.Instance.CreateTextGraphicElement(layout,width.HasValue?TextType.RectangleParagraph:TextType.PointText,geometry,symbol,SecurityElement.Escape(text)!,name,false);
        try{element.SetRotation(rotation);MoveBounds(element,x/f,y/f);return element;}
        catch{layout.DeleteElement(element);throw;}
    }
    public static object Add(JsonElement a)
    {
        var layout=Layout(a);var name=Contracts.Text(a,"name");var f=Factor(layout);
        if(layout.GetElementsAsFlattenedList().Any(e=>e.Name==name))throw new BridgeException("OUTPUT_CONFLICT","Element name already exists: "+name);
        var element=AddText(layout,name,Contracts.Text(a,"text"),a.GetProperty("x_cm").GetDouble(),a.GetProperty("y_cm").GetDouble(),Optional(a,"width_cm"),Optional(a,"height_cm"),Optional(a,"rotation")??0,Optional(a,"font_size")??12,Contracts.OptionalText(a,"font_family")??"Microsoft YaHei",PresentationSpec.Color(a,"color","#222222"),f);
        return new{layout_id=layout.URI,element=State(element,f),project_saved=false};
    }
    public static object City(JsonElement a)
    {
        var layout=Layout(a);var f=Factor(layout);var name=Contracts.Text(a,"name");var chars=Contracts.Text(a,"text").EnumerateRunes().Select(c=>c.ToString()).ToArray();var positions=a.GetProperty("positions").EnumerateArray().ToArray();
        var names=chars.Select((_,i)=>name+"_"+(i+1).ToString("D2")).ToArray();
        if(layout.GetElementsAsFlattenedList().Any(e=>names.Contains(e.Name)))throw new BridgeException("OUTPUT_CONFLICT","One or more character element names already exist");
        var created=new List<GraphicElement>();
        try
        {
            for(int i=0;i<chars.Length;i++)
            {
                var p=positions[i];created.Add(AddText(layout,names[i],chars[i],p.GetProperty("x_cm").GetDouble(),p.GetProperty("y_cm").GetDouble(),null,null,Optional(p,"rotation")??0,Optional(p,"font_size")??Optional(a,"font_size")??12,Contracts.OptionalText(a,"font_family")??"Microsoft YaHei",PresentationSpec.Color(a,"color","#222222"),f));
            }
        }
        catch{foreach(var element in created)layout.DeleteElement(element);throw;}
        return new{layout_id=layout.URI,text=Contracts.Text(a,"text"),characters=created.Select(e=>State(e,f)).ToArray(),coordinate_units="centimeters",project_saved=false};
    }
}
