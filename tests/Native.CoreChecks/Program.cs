using System.Text.Json;
using SafeBridge.Native;

int count=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);count++;}
void Reject(Action action,string code)
{try{action();throw new Exception("Expected "+code);}catch(BridgeException ex){Check(ex.Code==code,"Wrong code: "+ex.Code);}}
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
using var cases=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/contracts.json")));
foreach(var test in cases.RootElement.EnumerateArray())
{
    var request=test.GetProperty("request");
    if(test.GetProperty("ok").GetBoolean()){Contracts.Validate(request,"0123456789abcdef0123456789abcdef","test-token",1000);count++;}
    else Reject(()=>Contracts.Validate(request,"0123456789abcdef0123456789abcdef","test-token",1000),Contracts.Text(test,"code"));
}
Reject(()=>Contracts.Parse("{\"x\":1,\"x\":2}"),"INVALID_ARGUMENT");
Reject(()=>Contracts.Parse("{\"x\":\""+new string('a',65536)+"\"}"),"INVALID_ARGUMENT");
Reject(()=>PathPolicy.Local(@"..\outside.shp"),"INVALID_ARGUMENT");
Reject(()=>PathPolicy.Local(@"\\server\share\x.shp"),"INVALID_ARGUMENT");
Reject(()=>PathPolicy.OutputName("../escape"),"INVALID_ARGUMENT");
var state=Path.Combine(Path.GetTempPath(),"GISAgentCoreChecks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(state);
var manager=new JobManager(state);
var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var payload=JsonSerializer.SerializeToElement(new{input="涓枃"});int executions=0;
var accepted=manager.Submit("same-key","test",payload,async(id,ct,msg)=>{Interlocked.Increment(ref executions);started.SetResult();msg("running",null);await Task.Delay(28000,ct);return new JobOutcome([],new{status="passed"},new{status="not_applicable"},[]);});
var jobId=Contracts.Text(accepted,"job_id");await started.Task;
Check(Contracts.Text(manager.Submit("same-key","test",payload,(_,_,_)=>throw new Exception("Duplicate executed")),"job_id")==jobId,"Idempotent retry changed task");
Reject(()=>manager.Submit("same-key","test",JsonSerializer.SerializeToElement(new{input="other"}),(_,_,_)=>throw new Exception()),"IDEMPOTENCY_CONFLICT");
Reject(()=>manager.Submit("different-key","test",payload,(_,_,_)=>throw new Exception()),"BUSY");
// A 28-second executor must not block control/status queries or the caller.
var began=DateTime.UtcNow;
while(DateTime.UtcNow-began<TimeSpan.FromSeconds(26))
{Check(Contracts.Text(manager.Status(jobId),"state")=="running","Running task lost responsiveness");await Task.Delay(1000);}
manager.Cancel(jobId);
for(int n=0;n<100&&manager.HasActive;n++)await Task.Delay(30);
Check(Contracts.Text(manager.Status(jobId),"state")=="cancelled","Cooperative cancellation failed");
Check(executions==1,"Duplicate execution occurred");
var reload=new JobManager(state);
Check(Contracts.Text(reload.Submit("same-key","test",payload,(_,_,_)=>throw new Exception("Restart repeated task")),"job_id")==jobId,"Reconnect lost idempotency");
var pending=new JobRecord{job_id=Guid.NewGuid().ToString("N"),idempotency_key="crash",fingerprint="test",state="running",created_at=Contracts.Now,updated_at=Contracts.Now};
File.WriteAllText(Path.Combine(state,"jobs",pending.job_id+".json"),JsonSerializer.Serialize(pending));
var jobsDir = Path.Combine(state, "jobs");
var malformed = Path.Combine(jobsDir, "malformed.json");
var empty = Path.Combine(jobsDir, "empty.json");
var missingMetadata = Path.Combine(jobsDir, "missing-metadata.json");
const string malformedJson = "{\"job_id\":";
File.WriteAllText(malformed, malformedJson);
File.WriteAllText(empty, "");
File.WriteAllText(missingMetadata, "{}");
var unreadable = Path.Combine(jobsDir, "unreadable.json");
var historyErrors = new List<Exception>();
JobManager restarted;
using (var locked = File.Create(unreadable, 4096, FileOptions.None))
{
    restarted = new JobManager(state, historyErrors.Add);
    Check(File.Exists(unreadable), "Unreadable history was moved or removed");
}
var quarantined = Directory.GetFiles(Path.Combine(jobsDir, "quarantine"));
Check(historyErrors.Count == 4, "History errors were not reported per record");
Check(quarantined.Length == 3, "Invalid history was not quarantined");
Check(quarantined.Select(File.ReadAllText).ToHashSet().SetEquals([malformedJson, "", "{}"]),
    "Quarantine changed the original history content");
Check(File.Exists(Path.Combine(jobsDir, jobId + ".json")), "Valid history was moved or removed");
Check(Contracts.Text(restarted.Replay("same-key", payload)!.Value, "job_id") == jobId,
    "Bad history disrupted idempotent replay");
Check(Contracts.Text(restarted.Status(pending.job_id),"state")=="interrupted","Restart resumed unfinished work");
var afterRecovery = restarted.Submit("after-recovery", "test", payload, (_, _, _) =>
    Task.FromResult(new JobOutcome([], new { status = "passed" }, new { status = "not_applicable" }, [])));
for (int n = 0; n < 100 && restarted.HasActive; n++) await Task.Delay(30);
Check(Contracts.Text(restarted.Status(Contracts.Text(afterRecovery, "job_id")), "state") == "succeeded",
    "History recovery prevented a new task from completing");

// GP catalog v2: strict schema, typed normalization and argument plans.
var dataDir=Path.Combine(Path.GetTempPath(),"GISAgentGpChecks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dataDir);
foreach(var name in new[]{"roads","mask"})
 foreach(var ext in new[]{".shp",".shx",".dbf",".prj"})File.WriteAllText(Path.Combine(dataDir,name+ext),"x");
var roads=Path.Combine(dataDir,"roads.shp");var mask=Path.Combine(dataDir,"mask.shp");
var catalog=GpCatalog.Load(Path.Combine(root,"gp_catalog.json"));
Check(catalog.CatalogVersion==2,"Catalog version");
Check(catalog.Tools.Count>=25,"Catalog tool count: "+catalog.Tools.Count);
foreach(var required in new[]{"analysis.Clip","analysis.Buffer","management.Dissolve","management.Project","analysis.Intersect","analysis.Union","analysis.Erase","analysis.SpatialJoin","management.Merge","analysis.Statistics","management.SelectLayerByAttribute","management.SelectLayerByLocation"})
 Check(catalog.Tools.ContainsKey(required),"Missing catalog tool "+required);
Reject(()=>catalog.Tool("analysis.NotRegistered"),"CAPABILITY_UNAVAILABLE");
Reject(()=>new GpCatalog("{\"catalog_version\":1,\"tools\":{}}"),"CAPABILITY_UNAVAILABLE");
Reject(()=>new GpCatalog("{\"catalog_version\":2,\"tools\":{}}"),"CAPABILITY_UNAVAILABLE");
var clip=catalog.Tool("analysis.Clip");
Reject(()=>catalog.Normalize(clip,JsonSerializer.SerializeToElement(new{input_path=roads})),"INVALID_ARGUMENT");
Reject(()=>catalog.Normalize(clip,JsonSerializer.SerializeToElement(new{input_path=roads,clip_path=mask,unexpected=1})),"INVALID_ARGUMENT");
Reject(()=>catalog.Normalize(clip,JsonSerializer.SerializeToElement(new{input_path="D:\\missing.shp",clip_path=mask})),"DATASET_NOT_FOUND");
Reject(()=>catalog.Normalize(clip,JsonSerializer.SerializeToElement(new{input_path="..\\relative.shp",clip_path=mask})),"INVALID_ARGUMENT");
var clipArgs=catalog.BuildArguments(clip,catalog.Normalize(clip,JsonSerializer.SerializeToElement(new{input_path=roads,clip_path=mask,output_name="roads_clip"})),Path.Combine(state,"out","roads_clip"),null);
Check(clipArgs.Count==3,"Clip argument count");
Check(clipArgs[0].Kind==GpValueKind.Text&&((string)clipArgs[0].Value!).EndsWith("roads.shp",StringComparison.OrdinalIgnoreCase),"Clip input argument");
Check(clipArgs[2].Kind==GpValueKind.GeneratedOutput,"Clip generated output argument");
var buffer=catalog.Tool("analysis.Buffer");
Reject(()=>catalog.Normalize(buffer,JsonSerializer.SerializeToElement(new{input_path=roads,distance=-1})),"INVALID_ARGUMENT");
Reject(()=>catalog.Normalize(buffer,JsonSerializer.SerializeToElement(new{input_path=roads,distance=2,unit="Furlongs"})),"INVALID_ARGUMENT");
Reject(()=>catalog.Normalize(buffer,JsonSerializer.SerializeToElement(new{input_path=roads,distance=2,line_side="SIDEWAYS"})),"INVALID_ARGUMENT");
Reject(()=>catalog.Normalize(buffer,JsonSerializer.SerializeToElement(new{input_path=roads,distance=2,dissolve_option="LIST"})),"INVALID_ARGUMENT");
var bufferArgs=catalog.BuildArguments(buffer,catalog.Normalize(buffer,JsonSerializer.SerializeToElement(new{input_path=roads,distance=2,dissolve_option="LIST",dissolve_field=new[]{"test_id"}})),Path.Combine(state,"out","buffer"),null);
Check(bufferArgs.Count==8,"Buffer argument count");
Check(bufferArgs[2].Kind==GpValueKind.Text&&(string)bufferArgs[2].Value! is "2 Meters","Buffer distance composition");
Check(bufferArgs[3].Value as string=="FULL","Buffer default line side");
Check(bufferArgs[6].Value as string=="test_id","Buffer dissolve field list");
var project=catalog.Tool("management.Project");
var projectArgs=catalog.BuildArguments(project,catalog.Normalize(project,JsonSerializer.SerializeToElement(new{input_path=roads,target_wkid=4326})),Path.Combine(state,"out","project"),null);
Check(projectArgs[2].Kind==GpValueKind.SpatialReferenceWkid&&((int)projectArgs[2].Value!)==4326,"Project spatial reference argument");
Reject(()=>catalog.Normalize(project,JsonSerializer.SerializeToElement(new{input_path=roads,target_wkid=10})),"INVALID_ARGUMENT");
Reject(()=>catalog.Normalize(project,JsonSerializer.SerializeToElement(new{input_path=roads,target_wkid=4326,transform_method=new string('a',300)})),"INVALID_ARGUMENT");
var selection=catalog.Tool("management.SelectLayerByAttribute");
var selectionArgs=catalog.BuildArguments(selection,catalog.Normalize(selection,JsonSerializer.SerializeToElement(new{layer_id="CIMPATH=map/layer.json",where_clause="test_id = 1"})),null,null);
Check(selectionArgs[0].Kind==GpValueKind.MapLayer&&((string)selectionArgs[0].Value!)=="CIMPATH=map/layer.json","Selection layer argument");
Check(selectionArgs[2].Value as string=="test_id = 1","Selection where clause");
Check(catalog.Tools.Count(tool=>tool.Value.OutputKind=="feature_class")>=18,"Feature-class output tool count");
Check(catalog.Tools.Values.All(tool=>tool.Arguments.Count>0),"Every tool has an argument plan");
Check(PresentationSpec.Page(20,10,"A4","auto").Orientation=="landscape","Wide study region uses landscape");
Check(PresentationSpec.Page(10,20,"A4","auto").Orientation=="portrait","Tall study region uses portrait");
Check(PresentationSpec.Page(10,10,"A4","auto").Orientation=="portrait","Square extent tie is deterministic");
Check(PresentationSpec.Page(10,20,"A3","landscape").Width==42,"Explicit orientation overrides auto");
Reject(()=>PresentationSpec.Page(0,10,"A4","auto"),"INVALID_ARGUMENT");
Reject(()=>PresentationSpec.Page(double.NaN,10,"A4","auto"),"INVALID_ARGUMENT");
Reject(()=>PresentationSpec.PixelBudget(100,100,600),"INVALID_ARGUMENT");
PresentationSpec.PixelBudget(29.7,21,300);
Reject(()=>Contracts.ValidateArgs("export_map",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",idempotency_key="k",width=12000,height=12000})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("export_layout",JsonSerializer.SerializeToElement(new{project_id="p",layout_id="l",idempotency_key="k",output_name="../data"})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("set_simple_renderer",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",layer_id="l",color="red"})),"INVALID_ARGUMENT");
Check(Math.Abs(LayoutEditSpec.CmPerUnit(.001)-.1)<1e-12,"Millimeter pages convert to cm");
Check(Math.Abs(LayoutEditSpec.CmPerUnit(.0254)-2.54)<1e-12,"Inch pages convert to cm");
Reject(()=>LayoutEditSpec.CmPerUnit(0),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("update_layout_elements",JsonSerializer.SerializeToElement(new{project_id="p",layout_id="l",edits=new[]{new{element_name="x",scale=2,width_cm=4}}})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("update_layout_elements",JsonSerializer.SerializeToElement(new{project_id="p",layout_id="l",edits=new[]{new{element_name="x",x_cm=2},new{element_name="x",x_cm=3}}})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("place_city_name",JsonSerializer.SerializeToElement(new{project_id="p",layout_id="l",name="Hefei",text="合肥市",positions=new[]{new{x_cm=1,y_cm=2}}})),"INVALID_ARGUMENT");
Contracts.ValidateArgs("place_city_name",JsonSerializer.SerializeToElement(new{project_id="p",layout_id="l",name="Hefei",text="合肥市",positions=new[]{new{x_cm=1,y_cm=2},new{x_cm=2,y_cm=3},new{x_cm=3,y_cm=4}}}));
Check(true,"City positions accepted");
Contracts.ValidateArgs("place_city_name",JsonSerializer.SerializeToElement(new{project_id="p",layout_id="l",name="Unicode",text="𠮷",positions=new[]{new{x_cm=1,y_cm=2}}}));
Check(true,"Supplementary Unicode character accepted");
Contracts.ValidateArgs("raster_clip",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",input_path="a",idempotency_key="k",bbox=new double[]{1,1,2,2}}));
Check(true,"Raster bounds accepted");
Reject(()=>Contracts.ValidateArgs("raster_clip",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",input_path="a",idempotency_key="k",bbox=new double[]{1,1,2,2},mask_path="b"})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("raster_resample",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",input_path="a",idempotency_key="k",cell_size=0})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("raster_index",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",positive_path="a",negative_path="b",index="arbitrary",idempotency_key="k"})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("raster_project",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",input_path="a",idempotency_key="k",target_wkid=4326,cell_size=.1,transform_method="a;b"})),"INVALID_ARGUMENT");
Check(RasterSpec.Aligned(.0089,.0087,.0001)&&!RasterSpec.Aligned(.00895,.0087,.0001),"Raster alignment distinguishes a half-cell shift");
Contracts.ValidateArgs("set_graduated_colors_renderer",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",layer_id="l",field="D",breaks=new[]{0,10,20}}));
Check(ThematicSpec.RangeLabel(10,20,false)=="(10, 20]", "Class interval label shows the open lower bound");
Reject(()=>Contracts.ValidateArgs("set_graduated_colors_renderer",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",layer_id="l",field="D",breaks=new[]{0,10,10}})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("set_graduated_colors_renderer",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",layer_id="l",field="D",method="manual"})),"INVALID_ARGUMENT");
Reject(()=>Contracts.ValidateArgs("set_unique_value_renderer",JsonSerializer.SerializeToElement(new{project_id="p",map_id="m",layer_id="l",field="F",colors=new[]{"red"}})),"INVALID_ARGUMENT");
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,assertions=count,state_dir=state}));


