using System.Security.Cryptography;
using System.Text.Json;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
namespace SafeBridge.Native;

// Catalog-driven GP executor. The only place that knows ArcGIS SDK types.
internal sealed class GpOperations
{
    private readonly BridgeConfig config;
    private readonly JobManager jobs;
    private readonly GpCatalog catalog;
    private readonly string sha256;
    public GpOperations(BridgeConfig config,JobManager jobs)
    {
        this.config=config;this.jobs=jobs;
        var bytes=File.ReadAllBytes(config.gp_catalog_path);
        catalog=GpCatalog.Load(config.gp_catalog_path);
        sha256=Convert.ToHexString(SHA256.HashData(bytes));
    }
    public object Catalog()
    {
        var summary=JsonSerializer.SerializeToElement(catalog.Summary());
        return new{catalog_version=catalog.CatalogVersion,sha256,tools=summary.GetProperty("tools")};
    }
    public async Task<object> Submit(JsonElement args)
    {
        var tool=catalog.Tool(Contracts.Text(args,"tool"));
        var normalized=catalog.Normalize(tool,args.GetProperty("parameters"));
        var projectId=Contracts.Text(args,"project_id");var mapId=Contracts.Text(args,"map_id");
        var payload=JsonSerializer.SerializeToElement(new{project_id=projectId,map_id=mapId,tool=tool.Name,parameters=normalized.Values,output_name=normalized.OutputName});
        var replay=jobs.Replay(Contracts.Text(args,"idempotency_key"),payload);
        if(replay is not null)return replay.Value;
        // Cheap preflight on the MCT fails bad requests before a job exists (fast-fail semantics).
        var preflight=await QueuedTask.Run(()=>Preflight(tool,normalized,projectId,mapId));
        return jobs.Submit(Contracts.Text(args,"idempotency_key"),tool.Name,payload,(id,ct,message)=>Execute(id,projectId,mapId,tool,normalized,preflight,ct,message));
    }

    private sealed class PreflightResult
    {
        public SpatialReference? OutputCrs {get;set;}
        public Dictionary<string,object> Checks {get;set;}=new(StringComparer.Ordinal);
    }

    private static PreflightResult Preflight(GpTool tool,NormalizedGp normalized,string projectId,string mapId)
    {
        var result=new PreflightResult();
        foreach(var parameter in tool.Params.Values.OrderBy(p=>p.Name,StringComparer.Ordinal))
        {
            if(!normalized.Values.TryGetValue(parameter.Name,out var value))continue;
            if(parameter.Type=="map_layer")
            {
                var layerId=value.GetString()!;var layer=MapOperations.Layer(projectId,mapId,layerId);
                if(layer is not FeatureLayer)throw new BridgeException("INVALID_ARGUMENT","Selection requires a feature layer: "+layerId);
                result.Checks[parameter.Name]=new{layer_id=layer.URI,name=layer.Name};
                continue;
            }
            if(parameter.Type is not ("dataset" or "dataset_list"))continue;
            var paths=parameter.Type=="dataset"?new[]{value.GetString()!}:value.EnumerateArray().Select(item=>item.GetString()!).ToArray();
            var geometry=new List<string>();var crsList=new List<SpatialReference>();var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var path in paths)
            {
                var (type,sr)=DatasetOperations.GeometryInfo(path);
                if(parameter.Geometry is {Length:>0} allowed&&!allowed.Contains(type.ToString(),StringComparer.OrdinalIgnoreCase))
                    throw new BridgeException("INVALID_GEOMETRY",$"{parameter.Name} geometry {type} is not supported by {tool.Name}");
                if(sr.IsUnknown)throw new BridgeException("UNKNOWN_CRS","Input spatial reference is unknown: "+path);
                geometry.Add(type.ToString());crsList.Add(sr);
                if(fields.Count==0)fields=DatasetOperations.Fields(path);
            }
            if(tool.RequiresSameGcs&&crsList.Count>1)
            {
                var first=crsList[0];
                foreach(var sr in crsList.Skip(1))
                    if(first.GcsWkid!=sr.GcsWkid||(first.GcsWkid==0&&first.GcsWkt!=sr.GcsWkt))
                        throw new BridgeException("DATUM_TRANSFORM_REQUIRED","Inputs use different geographic coordinate systems; project inputs first or use a tool with an explicit transform parameter");
            }
            foreach(var fieldParameter in tool.Params.Values.Where(p=>p.Type is "field" or "field_list"&&p.DatasetParam==parameter.Name))
            {
                if(!normalized.Values.TryGetValue(fieldParameter.Name,out var fieldValue))continue;
                var names=fieldParameter.Type=="field"?new[]{fieldValue.GetString()!}:fieldValue.EnumerateArray().Select(item=>item.GetString()!).ToArray();
                foreach(var name in names)
                    if(!fields.ContainsKey(name))throw new BridgeException("FIELD_NOT_FOUND","Field not found in "+paths[0]+": "+name);
            }
            if(normalized.Values.TryGetValue("statistics_fields",out var statistics)&&statistics.ValueKind==JsonValueKind.String)
                foreach(var token in statistics.GetString()!.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
                {
                    var name=token.Split(' ',StringSplitOptions.RemoveEmptyEntries)[0];
                    if(!fields.ContainsKey(name))throw new BridgeException("FIELD_NOT_FOUND","Statistics field not found in "+paths[0]+": "+name);
                }
            if(result.OutputCrs is null)result.OutputCrs=crsList[0];
            result.Checks[parameter.Name]=new{paths,geometry,crs=new{wkid=crsList[0].Wkid,gcs_wkid=crsList[0].GcsWkid}};
        }
        if(tool.OutputCrsFrom is string crsFrom&&normalized.Values.TryGetValue(crsFrom,out var crsValue))
        {
            var first=crsValue.ValueKind==JsonValueKind.Array?crsValue.EnumerateArray().First().GetString()!:crsValue.GetString()!;
            result.OutputCrs=DatasetOperations.GeometryInfo(first).Sr;
        }
        return result;
    }

    private object?[] BuildValues(GpTool tool,NormalizedGp normalized,string projectId,string mapId,string? outputPath,string? gdbPath)
    {
        var values=new List<object?>();
        foreach(var item in catalog.BuildArguments(tool,normalized,outputPath,gdbPath))
            values.Add(item.Kind switch
            {
                GpValueKind.Null=>null,
                GpValueKind.Text=>item.Value,
                GpValueKind.Number=>item.Value,
                GpValueKind.Integer=>item.Value,
                GpValueKind.Boolean=>item.Value,
                GpValueKind.GeneratedOutput=>item.Value,
                GpValueKind.GeneratedGdb=>item.Value,
                GpValueKind.SpatialReferenceWkid=>SpatialReferenceBuilder.CreateSpatialReference((int)item.Value!),
                GpValueKind.MapLayer=>MapOperations.Layer(projectId,mapId,(string)item.Value!),
                _=>throw new BridgeException("CAPABILITY_UNAVAILABLE","Unsupported GP value")
            });
        return values.ToArray();
    }

    private static async Task<IGPResult> Run(string tool,object?[] values,CancellationToken ct,Action<string,int?> message)
    {
        var env=Geoprocessing.MakeEnvironmentArray(overwriteoutput:false);
        // No .Result/.Wait and no enclosing QueuedTask: GP owns its execution thread.
        var result=await Geoprocessing.ExecuteToolAsync(tool,Geoprocessing.MakeValueArray(values!),env,ct,null,GPExecuteToolFlags.GPThread|GPExecuteToolFlags.AddToHistory);
        foreach(var m in result.Messages??[])message(m.Text,null);
        if(result.IsCanceled||ct.IsCancellationRequested)throw new OperationCanceledException(ct);
        if(result.IsFailed)
        {
            throw new BridgeException("GP_FAILED",$"{tool} failed",new{gp_error_code=result.ErrorCode,messages=result.Messages?.Select(m=>m.Text).TakeLast(20).ToArray()});
        }
        return result;
    }

    private async Task<(string Status,string[] LayerIds,string? Error)> Bind(string projectId,string mapId,string path,string? name,CancellationToken ct)
    {
        try
        {
            return await QueuedTask.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return MapOperations.BindOutput(projectId,mapId,path,name);
            });
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex){return("not_attached",[],ex.Message);}
    }

    private async Task<JobOutcome> Execute(string id,string projectId,string mapId,GpTool tool,NormalizedGp normalized,PreflightResult preflight,CancellationToken ct,Action<string,int?> message)
    {
        ct.ThrowIfCancellationRequested();
        var needsOutput=tool.OutputKind is "feature_class" or "table" or "feature_classes";
        string? folder=null,outputPath=null,gdbPath=null;
        if(needsOutput)
        {
            folder=PathPolicy.OutputFolder(config.output_root,id);
            gdbPath=Path.Combine(folder,"result.gdb");
            if(tool.OutputKind is "feature_class" or "table")outputPath=Path.Combine(gdbPath,normalized.OutputName??tool.DefaultName);
            message("Creating a new output geodatabase",null);
            await Run("management.CreateFileGDB",[folder,"result.gdb"],ct,message);
            ct.ThrowIfCancellationRequested();
        }
        // Layer and spatial-reference GP values must be created on the MCT.
        var values=await QueuedTask.Run(()=>BuildValues(tool,normalized,projectId,mapId,outputPath,gdbPath));
        var environment=Geoprocessing.MakeEnvironmentArray(overwriteoutput:false,outputCoordinateSystem:preflight.OutputCrs,extent:"MAXOF",mask:"");
        message("Running "+tool.Name+" on full datasets",null);
        IGPResult gp;
        try
        {
            gp=await Geoprocessing.ExecuteToolAsync(tool.Name,Geoprocessing.MakeValueArray(values!),environment,ct,null,GPExecuteToolFlags.GPThread|GPExecuteToolFlags.AddToHistory);
        }
        catch(OperationCanceledException){throw;}
        foreach(var m in gp.Messages??[])message(m.Text,null);
        if(gp.IsCanceled||ct.IsCancellationRequested)throw new OperationCanceledException(ct);
        if(gp.IsFailed)
        {
            throw new BridgeException("GP_FAILED",tool.Name+" failed",new{gp_error_code=gp.ErrorCode,messages=gp.Messages?.Select(m=>m.Text).TakeLast(20).ToArray()});
        }
        var messages=gp.Messages?.Select(m=>m.Text).TakeLast(20).ToArray()??[];
        object[] outputs;object validation;object binding;var warnings=new List<string>();
        switch(tool.OutputKind)
        {
            case "feature_class":
            {
                var description=await DescribeOutput(outputPath!);
                var count=CountOf(description);
                outputs=[new{path=outputPath,count,description}];
                if(count==0)warnings.Add("EMPTY_RESULT");
                var bound=await Bind(projectId,mapId,outputPath!,normalized.OutputName??tool.DefaultName,ct);
                binding=Binding(bound,mapId);
                if(bound.Status!="attached")warnings.Add("MAP_NOT_ATTACHED");
                validation=new{status="passed",checks=new{count}};
                break;
            }
            case "table":
            {
                var description=await DescribeTable(outputPath!);
                var count=CountOf(description);
                outputs=[new{path=outputPath,count,description}];
                if(count==0)warnings.Add("EMPTY_RESULT");
                binding=new{status="not_applicable"};
                validation=new{status="passed",checks=new{count}};
                break;
            }
            case "feature_classes":
            {
                var featureClasses=await QueuedTask.Run(()=>DatasetOperations.ListFeatureClasses(gdbPath!));
                if(featureClasses.Length==0)throw new BridgeException("OUTPUT_VALIDATION_FAILED","The generated geodatabase contains no feature classes",new{output=gdbPath});
                var outputList=new List<object>();var counts=new Dictionary<string,long>(StringComparer.Ordinal);var layerIds=new List<string>();var bindingStatus="attached";string? bindingError=null;
                foreach(var featureClass in featureClasses)
                {
                    var description=await DescribeOutput(featureClass);var count=CountOf(description);
                    counts[Path.GetFileName(featureClass)]=count;outputList.Add(new{path=featureClass,count,description});
                    if(count==0)warnings.Add("EMPTY_RESULT");
                    var bound=await Bind(projectId,mapId,featureClass,Path.GetFileName(featureClass),ct);
                    if(bound.Status!="attached"){bindingStatus="partially_attached";bindingError=bound.Error;warnings.Add("MAP_NOT_ATTACHED");}
                    layerIds.AddRange(bound.LayerIds);
                }
                outputs=outputList.ToArray();
                binding=new{status=bindingStatus,map_id=mapId,layer_ids=layerIds.ToArray(),error=bindingError};
                validation=new{status="passed",checks=new{feature_classes=featureClasses.Length,counts}};
                break;
            }
            case "selection":
            {
                var layerId=normalized.Values["layer_id"].GetString()!;
                var selected=await QueuedTask.Run(()=>{var layer=MapOperations.FeatureLayer(projectId,mapId,layerId);return(layer.URI,layer.Name,layer.SelectionCount);});
                outputs=[new{layer_id=selected.URI,selected_count=selected.SelectionCount,name=selected.Name}];
                binding=new{status="not_applicable"};
                validation=new{status="passed",checks=new{selected_count=selected.SelectionCount}};
                break;
            }
            default:
                outputs=[];validation=new{status="passed",checks=(object?)null};binding=new{status="not_applicable"};
                break;
        }
        if(folder is not null)
            File.WriteAllText(Path.Combine(folder,"validation.json"),JsonSerializer.Serialize(new{job_id=id,tool=tool.Name,parameters=normalized.Values,output_path=outputPath,gdb=gdbPath,preflight=preflight.Checks,outputs,validation,map_binding=binding,warnings,messages}));
        return new JobOutcome(outputs,validation,binding,warnings.ToArray());
    }

    private static object Binding((string Status,string[] LayerIds,string? Error) bound,string mapId)=>new{status=bound.Status,map_id=mapId,layer_ids=bound.LayerIds,error=bound.Error};

    private static async Task<object> DescribeOutput(string path)
    {
        try
        {
            var description=await QueuedTask.Run(()=>DatasetOperations.Describe(path,true));
            return description;
        }
        catch(BridgeException){throw;}
        catch(Exception ex){throw new BridgeException("OUTPUT_VALIDATION_FAILED",ex.Message,new{output=path});}
    }

    private static async Task<object> DescribeTable(string path)
    {
        try{return await QueuedTask.Run(()=>DatasetOperations.DescribeTable(path,true));}
        catch(BridgeException){throw;}
        catch(Exception ex){throw new BridgeException("OUTPUT_VALIDATION_FAILED",ex.Message,new{output=path});}
    }

    private static long CountOf(object description)
    {
        var element=JsonSerializer.SerializeToElement(description);
        return element.TryGetProperty("count",out var count)&&count.ValueKind==JsonValueKind.Number?count.GetInt64():0;
    }
}
