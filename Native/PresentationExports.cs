using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
namespace SafeBridge.Native;

internal sealed class PresentationExports(BridgeConfig config,JobManager jobs)
{
    public async Task<object> Submit(string op,JsonElement args)
    {
        PresentationSpec.Validate(op,args);
        var key=Contracts.Text(args,"idempotency_key");
        var payload=JsonSerializer.SerializeToElement(new{operation=op,args});
        var replay=jobs.Replay(key,payload);if(replay is not null)return replay.Value;
        await QueuedTask.Run(()=>Preflight(op,args));
        var owned=args.Clone();
        return jobs.Submit(key,op,payload,async(id,ct,message)=>
        {
            ct.ThrowIfCancellationRequested();message("Preparing native cartographic export",0);
            return await QueuedTask.Run(()=>
            {
                ct.ThrowIfCancellationRequested();Preflight(op,owned);
                var directory=PathPolicy.OutputFolder(config.output_root,id);
                var format=op=="export_map"?"PNG":Contracts.OptionalText(owned,"format")??"PNG";
                var stem=PathPolicy.OutputName(Contracts.OptionalText(owned,"output_name")??"map");
                var path=Path.Combine(directory,stem+"."+format.ToLowerInvariant());
                var temporary=Path.Combine(directory,stem+".partial."+format.ToLowerInvariant());
                int dpi=(int)PresentationSpec.Number(owned,"dpi",300,72,600);
                message("Exporting with ArcGIS Pro SDK; native export cannot be interrupted mid-render",25);
                if(op=="export_layout")
                {
                    var layout=PresentationOperations.Resolve(Contracts.Text(owned,"project_id"),Contracts.Text(owned,"layout_id"));
                    if(format=="PDF")layout.Export(new PDFFormat{OutputFileName=temporary,Resolution=dpi});
                    else layout.Export(new PNGFormat{OutputFileName=temporary,Resolution=dpi,HasTransparentBackground=false});
                }
                else
                {
                    var view=MapView.Active!;
                    view.Export(new PNGFormat{OutputFileName=temporary,Resolution=dpi,Width=(int)PresentationSpec.Number(owned,"width",2400,256,12000),Height=(int)PresentationSpec.Number(owned,"height",1800,256,12000),HasTransparentBackground=false});
                }
                var bytes=File.ReadAllBytes(temporary);
                if(bytes.Length<100||format=="PDF"&&!bytes.AsSpan(0,5).SequenceEqual("%PDF-"u8)||format=="PNG"&&!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
                    throw new BridgeException("OUTPUT_VALIDATION_FAILED","Export is empty or has an invalid file signature");
                File.Move(temporary,path,false);
                message("Export file validated",100);
                return new JobOutcome([new{path,format,dpi,bytes=bytes.LongLength,sha256=Convert.ToHexString(SHA256.HashData(bytes)),width_px=format=="PNG"?(int?)BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4)):null,height_px=format=="PNG"?(int?)BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4)):null}],new{status="passed",file_signature_valid=true},new{status="not_applicable",project_saved=false},ct.IsCancellationRequested?["CANCELLATION_ARRIVED_AFTER_NATIVE_EXPORT"]:[]);
            });
        });
    }
    private static void Preflight(string op,JsonElement a)
    {
        if(op=="export_layout")
        {
            var layout=PresentationOperations.Resolve(Contracts.Text(a,"project_id"),Contracts.Text(a,"layout_id"));
            var page=layout.GetPage();
            // Convert page units to cm before applying a memory budget.
            var factor=page.Units?.ConversionFactor*100??throw new BridgeException("INVALID_ARGUMENT","Layout page units are undefined");
            PresentationSpec.PixelBudget(page.Width*factor,page.Height*factor,(int)PresentationSpec.Number(a,"dpi",300,72,600));
            if(!layout.GetElementsAsFlattenedList().OfType<ArcGIS.Desktop.Layouts.MapFrame>().Any())throw new BridgeException("INVALID_ARGUMENT","Layout has no map frame");
        }
        else
        {
            MapOperations.Resolve(Contracts.Text(a,"project_id"),Contracts.Text(a,"map_id"));
            if(MapView.Active?.Map.URI!=Contracts.Text(a,"map_id"))throw new BridgeException("NO_ACTIVE_VIEW","Activate the specified 2D map view before exporting");
        }
    }
}
