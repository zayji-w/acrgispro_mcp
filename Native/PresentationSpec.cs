using System.Text.Json;
using System.Text.RegularExpressions;
namespace SafeBridge.Native;

// Pure validation and page planning: no Pro dependency, shared with regression checks.
internal static class PresentationSpec
{
    public static double Number(JsonElement args,string key,double fallback,double min,double max)
    {
        double value=args.TryGetProperty(key,out var p)?p.GetDouble():fallback;
        if(!double.IsFinite(value)||value<min||value>max)throw new BridgeException("INVALID_ARGUMENT",$"{key} must be between {min} and {max}");
        return value;
    }
    public static string Choice(JsonElement a,string key,string fallback,params string[] allowed)
    {
        var value=Contracts.OptionalText(a,key)??fallback;
        if(!allowed.Contains(value))throw new BridgeException("INVALID_ARGUMENT","Invalid "+key);
        return value;
    }
    public static string Color(JsonElement a,string key,string fallback)
    {
        var text=Contracts.OptionalText(a,key)??fallback;
        if(!Regex.IsMatch(text,"^#[0-9a-fA-F]{6}$"))throw new BridgeException("INVALID_ARGUMENT",key+" must be #RRGGBB");
        return text.ToUpperInvariant();
    }
    public static void Validate(string op,JsonElement a)
    {
        if(op=="set_simple_renderer")
        {
            Color(a,"color","#4C78A8");Color(a,"outline_color","#333333");
            Number(a,"width",1.5,0.1,20);Number(a,"size",8,1,72);Number(a,"outline_width",0.5,0,10);
        }
        if(op=="set_labels")
        {
            Color(a,"color","#222222");Number(a,"font_size",10,4,72);
            if(Contracts.Text(a,"field").Length>128)throw new BridgeException("INVALID_ARGUMENT","Field name exceeds 128 characters");
        }
        if(op=="create_layout")
        {
            Choice(a,"orientation","auto","auto","landscape","portrait");Choice(a,"page_size","A4","A4","A3");
            if(Contracts.Text(a,"name").Length>100||Contracts.Text(a,"title").Length>200)throw new BridgeException("INVALID_ARGUMENT","Layout name/title is too long");
        }
        if(op is "export_layout" or "export_map")
        {
            var dpi=Number(a,"dpi",300,72,600);if(dpi!=Math.Truncate(dpi))throw new BridgeException("INVALID_ARGUMENT","dpi must be integer");
            if(Contracts.Text(a,"idempotency_key").Length>128)throw new BridgeException("INVALID_ARGUMENT","Idempotency key exceeds 128 characters");
            PathPolicy.OutputName(Contracts.OptionalText(a,"output_name")??"map");
            if(op=="export_layout")Choice(a,"format","PNG","PNG","PDF");
            else
            {
                var w=Number(a,"width",2400,256,12000);var h=Number(a,"height",1800,256,12000);
                if(w!=Math.Truncate(w)||h!=Math.Truncate(h)||w*h>40000000)throw new BridgeException("INVALID_ARGUMENT","Invalid pixel dimensions or image exceeds 40 megapixels");
            }
        }
    }
    public static (double Width,double Height,string Orientation) Page(double extentWidth,double extentHeight,string size,string orientation)
    {
        if(!double.IsFinite(extentWidth)||!double.IsFinite(extentHeight)||extentWidth<=0||extentHeight<=0)throw new BridgeException("INVALID_ARGUMENT","Study area must have a nonzero, finite extent");
        if(orientation=="auto")orientation=extentWidth>extentHeight?"landscape":"portrait";
        var shortSide=size=="A3"?29.7:21.0;var longSide=size=="A3"?42.0:29.7;
        return orientation=="landscape"?(longSide,shortSide,orientation):(shortSide,longSide,orientation);
    }
    public static void PixelBudget(double widthCm,double heightCm,int dpi)
    {
        if(widthCm<=0||heightCm<=0||widthCm*heightCm*dpi*dpi/6.4516>80000000)
            throw new BridgeException("INVALID_ARGUMENT","Layout export exceeds 80 megapixels; reduce dpi");
    }
}
