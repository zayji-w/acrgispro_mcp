using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcGIS.Desktop.Framework.Threading.Tasks;
namespace SafeBridge.Native;

internal static class BridgeService
{
    private static readonly object Lifecycle=new();
    private static CancellationTokenSource? cancellation;
    private static Task? worker;
    private static Timer? heartbeat;
    private static string folder="",sessionId="",token="";
    private static volatile bool active,stopping;
    private static BridgeConfig? config;
    private static JobManager? jobs;
    private static GpOperations? gp;
    private static RasterOperations? raster;
    private static PresentationExports? presentation;
    public static void Write(string path,object value)
        => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
    private static void WriteBytes(string path, byte[] json)
    {
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllBytes(temp,json);File.Move(temp,path,true);
    }
    private static void Heartbeat()
    {
        if(folder.Length==0)return;
        try{Write(Path.Combine(folder,"heartbeat.json"),new{active,time=Contracts.Now,pid=Environment.ProcessId,backend="native-sdk",version=Contracts.NativeVersion,protocol_version=2,ui_nonblocking=true,capabilities=Contracts.Operations,stopping});}
        catch(Exception ex){Log(ex);}
    }
    private static void Log(Exception ex)
    {
        try{File.AppendAllText(Path.Combine(folder,"error.log"),ex+"\n");}catch{}
    }
    public static void Start()
    {
        lock(Lifecycle)
        {
            if(active||worker is {IsCompleted:false})return;
            config=BridgeConfig.Load();Directory.CreateDirectory(config.state_root);
            var index=Path.Combine(config.state_root,"session.json");
            if(File.Exists(index))
            {
                try
                {
                    using var s=JsonDocument.Parse(File.ReadAllText(index));var id=Contracts.Text(s.RootElement,"id");
                    if(Regex.IsMatch(id,"^[0-9a-f]{32}$"))
                    {
                        using var h=JsonDocument.Parse(File.ReadAllText(Path.Combine(config.state_root,id,"heartbeat.json")));
                        double age=Contracts.Now-h.RootElement.GetProperty("time").GetDouble();
                        if(h.RootElement.GetProperty("active").GetBoolean() && age>=0 && age<5)throw new BridgeException("BUSY","Another Pro bridge session is active");
                    }
                }
                catch(Exception ex) when(ex is IOException or JsonException or KeyNotFoundException){ }
            }
            jobs=new JobManager(config.state_root, ex => BridgeDiagnostics.Record("job_history", ex));
            gp=new GpOperations(config,jobs);presentation=new PresentationExports(config,jobs);raster=new RasterOperations(config,jobs);
            sessionId=Guid.NewGuid().ToString("N");token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();folder=Path.Combine(config.state_root,sessionId);
            Directory.CreateDirectory(folder);Write(index,new{id=sessionId,token});active=true;stopping=false;cancellation=new();
            Heartbeat();heartbeat=new Timer(_=>Heartbeat(),null,1000,1000);worker=Task.Run(()=>Loop(cancellation.Token));
        }
    }
    public static void Stop(bool force=false)
    {
        lock(Lifecycle)
        {
            stopping=true;
            try { jobs?.CancelActive(); }
            finally
            {
                if(force || jobs?.HasActive!=true)
                {
                    active=false;cancellation?.Cancel();heartbeat?.Dispose();heartbeat=null;
                }
                Heartbeat();
            }
        }
    }
    private static async Task Loop(CancellationToken cancel)
    {
        try
        {
            while(!cancel.IsCancellationRequested)
            {
                foreach(var path in Directory.EnumerateFiles(folder,"cmd_*.json"))
                {
                    if(cancel.IsCancellationRequested)break;
                    var name=Path.GetFileName(path);if(!Regex.IsMatch(name,"^cmd_[0-9a-f]{32}\\.json$"))continue;
                    var id=name.Substring(4,32);var claimed=Path.ChangeExtension(path,".running");
                    try{File.Move(path,claimed);}catch(IOException){continue;}
                    var watch=Stopwatch.StartNew();string op="";bool ok=false;object response;
                    try
                    {
                        if(new FileInfo(claimed).Length>65536)throw new BridgeException("INVALID_ARGUMENT","Request exceeds 64 KiB");
                        using var doc=Contracts.Parse(await File.ReadAllTextAsync(claimed,cancel));var request=Contracts.Validate(doc.RootElement,id,token);op=request.Op;
                        if(stopping && op is not("version" or "map_state" or "job_status" or "cancel_job" or "stop"))throw new BridgeException("BUSY","Bridge is stopping");
                        // Short SDK work is dispatched to MCT; GP submission returns without waiting for execution.
                        if(Contracts.Now>doc.RootElement.GetProperty("expires").GetDouble())throw new BridgeException("REQUEST_EXPIRED","Request expired before dispatch");
                        var data=await Dispatch(op,request.Args,doc.RootElement.GetProperty("expires").GetDouble());
                        response=request.V2?new{ok=true,request_id=id,data,warnings=Array.Empty<string>(),error=(object?)null}:(object)new{ok=true,result=data};ok=true;
                    }
                    catch(Exception ex){response=new{ok=false,request_id=id,data=(object?)null,warnings=Array.Empty<string>(),error=Contracts.Error(ex)};}
                    var json = JsonSerializer.SerializeToUtf8Bytes(response);
                    if (json.Length > Contracts.MaxResponseBytes)
                    {
                        ok = false;
                        json = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            ok, request_id=id, data=(object?)null, warnings=Array.Empty<string>(),
                            error=Contracts.Error(new BridgeException("RESPONSE_TOO_LARGE", "Response exceeds 4 MiB. Use a paged read where available."))
                        });
                    }
                    WriteBytes(Path.Combine(folder,"result_"+id+".json"),json);File.Delete(claimed);
                    File.AppendAllText(Path.Combine(folder,"audit.jsonl"),JsonSerializer.Serialize(new{time=Contracts.Now,id,op,ok,elapsed_ms=watch.ElapsedMilliseconds})+"\n");
                    if(ok && op=="stop")Stop();
                }
                if(stopping && jobs?.HasActive!=true){Stop();break;}
                await Task.Delay(100,cancel);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Log(ex);}
        finally{active=false;heartbeat?.Dispose();heartbeat=null;Heartbeat();}
    }
    private static async Task<object> Dispatch(string op,JsonElement args,double expires)
    {
        if(op=="version")return new{arcgis_version=FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule!.FileName).FileVersion,pid=Environment.ProcessId,backend="native-sdk",bridge_version=Contracts.NativeVersion,protocol_version=2,dotnet_version=Environment.Version.ToString(),python_window_required=false};
        if(op=="stop")return new{stopping=true};
        if(op=="job_status")return jobs!.Status(Contracts.Text(args,"job_id"));
        if(op=="cancel_job")return jobs!.Cancel(Contracts.Text(args,"job_id"));
        if(op=="list_gp_tools")return gp!.Catalog();
        if(op=="run_gp")return await gp!.Submit(args);
        if(RasterSpec.IsJob(op))return await raster!.Submit(op,args,expires);
        if(op is "export_layout" or "export_map")return await presentation!.Submit(op,args);
        if(op=="open_layout")return await PresentationOperations.Open(args);
        return await QueuedTask.Run(()=>
        {
            if(!active||Contracts.Now>expires)throw new BridgeException("REQUEST_EXPIRED","Request expired before MCT execution");
            return op switch
            {
                "map_state"=>MapOperations.State(args),
                "describe_dataset"=>DatasetOperations.Describe(Contracts.Text(args,"path"),Contracts.Bool(args,"include_count")),
                "read_table"=>TableReadOperations.Read(args),
                "describe_raster"=>RasterData.Read(Contracts.Text(args,"path")).Summary(),
                "add_raster_layer"=>RasterData.Add(args),
                "add_layer"=>MapOperations.Add(Contracts.Text(args,"project_id"),Contracts.Text(args,"map_id"),Contracts.Text(args,"path"),Contracts.OptionalText(args,"display_name")),
                "set_layer_visibility"=>MapOperations.Visibility(args),
                "zoom_to_layer"=>MapOperations.Zoom(args),
                "set_simple_renderer"=>PresentationOperations.Style(args),
                "set_unique_value_renderer"=>ThematicOperations.Unique(args),
                "set_graduated_colors_renderer"=>ThematicOperations.Graduated(args),
                "get_thematic_renderer"=>ThematicOperations.Read(args),
                "set_labels"=>PresentationOperations.Labels(args),
                "layer_presentation"=>PresentationOperations.State(args),
                "list_layouts"=>PresentationOperations.List(args),
                "create_layout"=>PresentationOperations.Create(args),
                "list_layout_elements"=>LayoutEditOperations.List(args),
                "update_layout_elements"=>LayoutEditOperations.Update(args),
                "add_layout_text"=>LayoutEditOperations.Add(args),
                "place_city_name"=>LayoutEditOperations.City(args),
                "add_demo"=>DemoOperations.Add(),"remove_demo"=>DemoOperations.Remove(),
                _=>throw new BridgeException("UNKNOWN_OPERATION","Unknown operation")
            };
        });
    }
}

