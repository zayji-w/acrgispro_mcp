namespace SafeBridge.Native;

/// <summary>The optional bridge must never propagate lifecycle failures into its host.</summary>
internal static class StartupGuard
{
    public static bool Run(Action action, Action<Exception> report, Action? cleanup = null)
    {
        try { action(); return true; }
        catch (Exception ex)
        {
            try { cleanup?.Invoke(); } catch { /* Host survival takes priority over bridge cleanup. */ }
            try { report(ex); } catch { /* Logging failure must not become another host failure. */ }
            return false;
        }
    }
}
