using System.Text.RegularExpressions;
namespace SafeBridge.Native;

internal static class PathPolicy
{
    public static void EnsureNoLinks(string path)
    {
        for (var current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new BridgeException("INVALID_ARGUMENT","Reparse-point paths are unsupported");
    }
    public static string Local(string path)
    {
        if(!Regex.IsMatch(path,@"^[a-zA-Z]:[\\/]")||!Path.IsPathFullyQualified(path)||path[2..].Contains(':'))throw new BridgeException("INVALID_ARGUMENT","Use a local absolute drive path");
        var full=Path.GetFullPath(path);EnsureNoLinks(full);return full.TrimEnd('\\','/');
    }
    public static (string Path,string Store,string Name,bool Shape) Dataset(string path)
    {
        var full=Local(path);
        if(full.EndsWith(".shp",StringComparison.OrdinalIgnoreCase))
        {
            foreach(var ext in new[]{".shp",".shx",".dbf",".prj"})
                if(!File.Exists(Path.ChangeExtension(full,ext)))throw new BridgeException("DATASET_NOT_FOUND","Missing shapefile component: "+ext);
            return(full,Path.GetDirectoryName(full)!,Path.GetFileName(full),true);
        }
        var match=Regex.Match(full,@"(?i)\.gdb[\\/]");
        if(match.Success)
        {
            var store=full[..(match.Index+4)];var name=full[(match.Index+5)..];
            if(!Directory.Exists(store)||string.IsNullOrWhiteSpace(name))throw new BridgeException("DATASET_NOT_FOUND","File GDB or dataset is missing");
            return(full,store,name,false);
        }
        throw new BridgeException("UNSUPPORTED_DATASET","Use a shapefile or a dataset inside a local File GDB");
    }
    public static string OutputFolder(string root,string id)
    {
        if(!Regex.IsMatch(id,"^[0-9a-f]{32}$"))throw new BridgeException("INVALID_ARGUMENT","Invalid output job ID");
        var canonical=Local(root);var folder=Path.GetFullPath(Path.Combine(canonical,id));
        if(!folder.StartsWith(canonical+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new BridgeException("INVALID_ARGUMENT","Output escapes root");
        EnsureNoLinks(folder);
        if(Directory.Exists(folder)||File.Exists(folder))throw new BridgeException("OUTPUT_CONFLICT","Output already exists");
        Directory.CreateDirectory(folder);return folder;
    }
    public static string OutputName(string? name)
    {
        name ??= "clip";
        if(!Regex.IsMatch(name,"^[A-Za-z][A-Za-z0-9_]{0,63}$"))throw new BridgeException("INVALID_ARGUMENT","Output name must be 1–64 ASCII letters/digits/underscores, starting with a letter");
        return name;
    }
}
