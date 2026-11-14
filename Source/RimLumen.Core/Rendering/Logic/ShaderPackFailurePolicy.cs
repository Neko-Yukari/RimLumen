namespace RimLumen.Core.Rendering.Logic;

internal enum ShaderPackFailureAction : byte
{
    // The built-in default pack failed: RimLumen itself is broken, so detach and stay pixel-neutral.
    TripFailSafe = 0,
    // A custom pack failed while recording: keep the pipeline alive and revert the pack on the main thread.
    RevertPackToDefault = 1,
    // Probe mode (-rimlumen-probe): a default-pack failure stays fail-loud so the run reports it.
    FailLoud = 2,
}

internal static class ShaderPackFailurePolicy
{
    // A custom pack is data, never a reason to tear down the whole pipeline; only the built-in default is.
    public static ShaderPackFailureAction Decide(bool isDefaultPack, bool verboseDiagnostics)
    {
        if (!isDefaultPack)
            return ShaderPackFailureAction.RevertPackToDefault;
        return verboseDiagnostics ? ShaderPackFailureAction.FailLoud : ShaderPackFailureAction.TripFailSafe;
    }
}
