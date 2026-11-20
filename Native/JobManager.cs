using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace SafeBridge.Native;

internal sealed record JobOutcome(object[] Outputs,object Validation,object MapBinding,string[] Warnings);
internal sealed class JobRecord
{
    public string job_id {get;set;}="";
    public string idempotency_key {get;set;}="";
    public string fingerprint {get;set;}="";
    public string state {get;set;}="accepted";
    public string tool {get;set;}="";
    public string input_scope {get;set;}="dataset";
    public double created_at {get;set;}
    public double updated_at {get;set;}
    public int? progress_percent {get;set;}
    public List<string> messages {get;set;}=[];
    public object[] outputs {get;set;}=[];
    public object validation {get;set;}=new{status="pending"};
    public object map_binding {get;set;}=new{status="pending"};
    public string[] warnings {get;set;}=[];
    public object? error {get;set;}
    public string? log_path {get;set;}
}

internal sealed class JobManager
{
    private readonly object gate=new();
    private readonly string folder;
    private readonly Dictionary<string,JobRecord> jobs=[];
    private CancellationTokenSource? cancellation;
    private string? active;
    public bool HasActive { get {lock(gate)return active is not null;} }
    public JobManager(string stateRoot, Action<Exception>? reportHistoryError = null)
    {
        folder=Path.Combine(stateRoot,"jobs");Directory.CreateDirectory(folder);
        foreach(var path in Directory.EnumerateFiles(folder,"*.json"))
        {
            JobRecord job;
            try
            {
                job = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(path))
                    ?? throw new JsonException("Persisted job is null");
                if (string.IsNullOrWhiteSpace(job.job_id)
                    || !string.Equals(job.job_id, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(job.idempotency_key)
                    || string.IsNullOrWhiteSpace(job.fingerprint)
                    || job.updated_at <= 0
                    || job.state is not ("accepted" or "running" or "cancel_requested" or "succeeded"
                        or "cancelled" or "failed" or "interrupted"))
                    throw new JsonException("Persisted job metadata is invalid");
            }
            catch (JsonException ex)
            {
                reportHistoryError?.Invoke(new IOException($"Invalid persisted job at '{path}'", ex));
                try
                {
                    var quarantine = Path.Combine(folder, "quarantine");
                    Directory.CreateDirectory(quarantine);
                    var name = Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".corrupt";
                    File.Move(path, Path.Combine(quarantine, name));
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    reportHistoryError?.Invoke(new IOException($"Could not quarantine persisted job at '{path}'", moveError));
                }
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reportHistoryError?.Invoke(new IOException($"Could not read persisted job at '{path}'", ex));
                continue;
            }
            if(job.state is "accepted" or "running" or "cancel_requested")
            {
                job.state = "interrupted";
                job.error = Contracts.Error(new BridgeException("INTERRUPTED","Pro or bridge exited before recording completion"));
                try { Save(job); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    reportHistoryError?.Invoke(new IOException($"Could not update persisted job at '{path}'", ex));
                }
            }
            if(Contracts.Now-job.updated_at>30*86400)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    reportHistoryError?.Invoke(new IOException($"Could not remove expired job at '{path}'", ex));
                }
                continue;
            }
            if (!jobs.TryAdd(job.job_id, job))
                reportHistoryError?.Invoke(new IOException($"Duplicate persisted job at '{path}'"));
        }
    }
    private void Save(JobRecord job)
    {
        job.updated_at=Contracts.Now;
        var path=Path.Combine(folder,job.job_id+".json");var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(job));File.Move(temp,path,true);
    }
    private static JsonElement Snapshot(JobRecord job)=>JsonSerializer.SerializeToElement(job);
    public JsonElement? Replay(string key,JsonElement payload)
    {
        lock(gate)
        {
            var prior=jobs.Values.FirstOrDefault(j=>j.idempotency_key==key);
            if(prior is null)return null;
            var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Contracts.Canonical(payload))));
            if(prior.fingerprint!=fingerprint)throw new BridgeException("IDEMPOTENCY_CONFLICT","Key already belongs to different arguments");
            return Snapshot(prior);
        }
    }
    public JsonElement Submit(string key,string tool,JsonElement payload,Func<string,CancellationToken,Action<string,int?>,Task<JobOutcome>> execute)
    {
        lock(gate)
        {
            var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Contracts.Canonical(payload))));
            var prior=Replay(key,payload);if(prior is not null)return prior.Value;
            if(active is not null)throw new BridgeException("BUSY","One GP task is already active",new{active_job_id=active});
            var job=new JobRecord{job_id=Guid.NewGuid().ToString("N"),idempotency_key=key,fingerprint=fingerprint,tool=tool,created_at=Contracts.Now};
            job.log_path=Path.Combine(folder,job.job_id+".jsonl");Save(job);jobs.Add(job.job_id,job);active=job.job_id;cancellation=new();
            var ct=cancellation.Token;
            _=Task.Run(async()=>
            {
                try
                {
                    lock(gate){if(job.state!="cancel_requested")job.state="running";Save(job);}
                    ct.ThrowIfCancellationRequested();
                    var outcome=await execute(job.job_id,ct,(message,progress)=>
                    {
                        lock(gate)
                        {
                            job.messages.Add(message);if(job.messages.Count>20)job.messages.RemoveAt(0);
                            job.progress_percent=progress;File.AppendAllText(job.log_path!,JsonSerializer.Serialize(new{time=Contracts.Now,message,progress})+"\n");Save(job);
                        }
                    });
                    lock(gate){job.outputs=outcome.Outputs;job.validation=outcome.Validation;job.map_binding=outcome.MapBinding;job.warnings=outcome.Warnings;job.state="succeeded";}
                }
                catch(OperationCanceledException){lock(gate){job.state="cancelled";job.warnings=["PARTIAL_OUTPUTS_MAY_EXIST"];}}
                catch(Exception ex){lock(gate){job.state="failed";job.error=Contracts.Error(ex);job.warnings=["PARTIAL_OUTPUTS_MAY_EXIST"];}}
                finally{lock(gate){Save(job);active=null;cancellation?.Dispose();cancellation=null;}}
            });
            return Snapshot(job);
        }
    }
    public JsonElement Status(string id)
    {
        lock(gate){if(!jobs.TryGetValue(id,out var job))throw new BridgeException("JOB_NOT_FOUND","Task not found");return Snapshot(job);}
    }
    public JsonElement Cancel(string id)
    {
        lock(gate)
        {
            if(!jobs.TryGetValue(id,out var job))throw new BridgeException("JOB_NOT_FOUND","Task not found");
            if(active==id && job.state is "accepted" or "running" or "cancel_requested"){job.state="cancel_requested";Save(job);cancellation?.Cancel();}
            return Snapshot(job);
        }
    }
    public void CancelActive(){lock(gate){if(active is not null)Cancel(active);}}
}
