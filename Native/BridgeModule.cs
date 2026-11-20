using ArcGIS.Core.Events;
using ArcGIS.Desktop.Core.Events;
using ArcGIS.Desktop.Framework.Contracts;

namespace SafeBridge.Native;

internal class BridgeModule : Module
{
    private SubscriptionToken? closed;
    protected override bool Initialize()
    {
        StartupGuard.Run(() =>
        {
            closed = ProjectClosedEvent.Subscribe(_ => MapOperations.ResetProject(), true);
            BridgeService.Start();
        }, ex => BridgeDiagnostics.Record("initialize", ex), () => BridgeService.Stop(true));
        return true;
    }
    protected override bool CanUnload()
    {
        StartupGuard.Run(() => BridgeService.Stop(true), ex => BridgeDiagnostics.Record("unload", ex));
        return true;
    }
    protected override void Uninitialize()
    {
        StartupGuard.Run(() =>
        {
            if (closed is not null) ProjectClosedEvent.Unsubscribe(closed);
        }, ex => BridgeDiagnostics.Record("unsubscribe", ex));
        StartupGuard.Run(() => BridgeService.Stop(true), ex => BridgeDiagnostics.Record("uninitialize", ex));
    }
}
internal class StartButton : Button
{
    protected override void OnClick()
    {
        StartupGuard.Run(BridgeService.Start, ex => BridgeDiagnostics.Record("start_button", ex), () => BridgeService.Stop(true));
    }
}
internal class StopButton : Button
{
    protected override void OnClick()
    {
        StartupGuard.Run(() => BridgeService.Stop(), ex => BridgeDiagnostics.Record("stop_button", ex));
    }
}
