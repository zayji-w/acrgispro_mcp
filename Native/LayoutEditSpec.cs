using System.Text.Json;
namespace SafeBridge.Native;

internal static class LayoutEditSpec
{
    private static readonly HashSet<string> EditKeys=["element_name","expected_type","x_cm","y_cm","width_cm","height_cm","rotation","scale","font_size","text","visible"];
    public static void Number(JsonElement a,string key,double min,double max)
    {
        if(!a.TryGetProperty(key,out var v))return;
        if(v.ValueKind!=JsonValueKind.Number||!v.TryGetDouble(out var n)||!double.IsFinite(n)||n<min||n>max)
            throw new BridgeException("INVALID_ARGUMENT","Invalid "+key);
    }
    public static void Position(JsonElement p)
    {
        if(p.ValueKind!=JsonValueKind.Object||p.EnumerateObject().Any(v=>v.Name is not("x_cm" or "y_cm" or "rotation" or "font_size"))||!p.TryGetProperty("x_cm",out _)||!p.TryGetProperty("y_cm",out _))
            throw new BridgeException("INVALID_ARGUMENT","Each position requires x_cm/y_cm, optional rotation/font_size");
        Number(p,"x_cm",-1000,1000);Number(p,"y_cm",-1000,1000);Number(p,"rotation",-360,360);Number(p,"font_size",4,144);
    }
    public static void Edit(JsonElement e)
    {
        if(e.ValueKind!=JsonValueKind.Object||e.EnumerateObject().Any(p=>!EditKeys.Contains(p.Name))||!e.TryGetProperty("element_name",out var name)||name.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(name.GetString()))
            throw new BridgeException("INVALID_ARGUMENT","Edit requires a unique element_name and known property keys");
        if(!e.EnumerateObject().Any(p=>p.Name is not("element_name" or "expected_type")))throw new BridgeException("INVALID_ARGUMENT","Edit contains no changes");
        Number(e,"x_cm",-1000,1000);Number(e,"y_cm",-1000,1000);Number(e,"width_cm",.001,1000);Number(e,"height_cm",.001,1000);Number(e,"rotation",-360,360);Number(e,"scale",.05,20);Number(e,"font_size",4,144);
        if(e.TryGetProperty("scale",out _)&&(e.TryGetProperty("width_cm",out _)||e.TryGetProperty("height_cm",out _)))throw new BridgeException("INVALID_ARGUMENT","Use scale or explicit dimensions, not both");
        if(e.TryGetProperty("text",out var text)&&(text.ValueKind!=JsonValueKind.String||text.GetString()!.Length>4096))throw new BridgeException("INVALID_ARGUMENT","Invalid text");
        if(e.TryGetProperty("expected_type",out var type)&&(type.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(type.GetString())))throw new BridgeException("INVALID_ARGUMENT","Invalid expected_type");
        if(e.TryGetProperty("visible",out var visible)&&visible.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new BridgeException("INVALID_ARGUMENT","Invalid visible");
    }
    public static void Validate(string op,JsonElement a)
    {
        if(op=="update_layout_elements")
        {
            var edits=a.GetProperty("edits");
            if(edits.ValueKind!=JsonValueKind.Array||edits.GetArrayLength() is <1 or >100)throw new BridgeException("INVALID_ARGUMENT","Provide 1-100 edits");
            var names=new HashSet<string>();foreach(var e in edits.EnumerateArray()){Edit(e);if(!names.Add(Contracts.Text(e,"element_name")))throw new BridgeException("INVALID_ARGUMENT","Duplicate target in batch");}
        }
        if(op is "add_layout_text" or "place_city_name")
        {
            if(Contracts.Text(a,"name").Length>100||Contracts.Text(a,"text").Length>4096)throw new BridgeException("INVALID_ARGUMENT","Name/text is too long");
            Number(a,"font_size",4,144);PresentationSpec.Color(a,"color","#222222");
            if(op=="add_layout_text")
            {
                Number(a,"x_cm",-1000,1000);Number(a,"y_cm",-1000,1000);Number(a,"width_cm",.001,1000);Number(a,"height_cm",.001,1000);Number(a,"rotation",-360,360);
                if(a.TryGetProperty("width_cm",out _)!=a.TryGetProperty("height_cm",out _))throw new BridgeException("INVALID_ARGUMENT","Paragraph width_cm and height_cm must be supplied together");
            }
            else
            {
                var positions=a.GetProperty("positions");var chars=Contracts.Text(a,"text").EnumerateRunes().Count();
                if(positions.ValueKind!=JsonValueKind.Array||chars is <1 or >64||positions.GetArrayLength()!=chars)throw new BridgeException("INVALID_ARGUMENT","Provide one position per Unicode character, maximum 64");
                foreach(var position in positions.EnumerateArray())Position(position);
            }
        }
    }
    public static double CmPerUnit(double metresPerUnit)
    {
        if(!double.IsFinite(metresPerUnit)||metresPerUnit<=0)throw new BridgeException("INVALID_ARGUMENT","Invalid page units");
        return metresPerUnit*100;
    }
}
