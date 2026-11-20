using SafeBridge.Native;
using System.Text.Json;

var root=Path.Combine(Path.GetTempPath(),"GISAgentStartupChecks-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed=0;
void Check(bool condition){if(!condition)throw new Exception("Startup regression failed");passed++;}
Exception? recorded=null;bool cleaned=false;
Check(!StartupGuard.Run(()=>BridgeConfig.Load(Path.Combine(root,"missing.json")),ex=>recorded=ex,()=>cleaned=true));
Check(recorded is BridgeException && cleaned);
var invalid=Path.Combine(root,"invalid.json");File.WriteAllText(invalid,"{");
Check(!StartupGuard.Run(()=>BridgeConfig.Load(invalid),_=>throw new IOException("Cannot write diagnostic"),()=>throw new IOException("Cleanup failed")));
var valid=Path.Combine(root,"valid.json");
File.WriteAllText(valid,JsonSerializer.Serialize(new{runtime_root=root,state_root=Path.Combine(root,"state"),output_root=Path.Combine(root,"out"),gp_catalog_path=Path.Combine(root,"catalog.json")}));
Check(StartupGuard.Run(()=>BridgeConfig.Load(valid),_=>throw new Exception("Unexpected failure")));
var bad=Path.Combine(root,"bad-path.json");File.WriteAllText(bad,"{\"runtime_root\":\"relative\"}");
Check(!StartupGuard.Run(()=>BridgeConfig.Load(bad),_=>{}));
Check(BridgeConfig.ConfigPath==Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!,"GIS-Agent","bridge.config.json"));
Check(StartupGuard.Run(()=>{},_=>{}));
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,assertions=passed,test_root=root}));
