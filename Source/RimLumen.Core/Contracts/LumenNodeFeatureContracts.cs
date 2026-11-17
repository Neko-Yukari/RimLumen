using System.Numerics;

namespace RimLumen.Core.Contracts;

// Node features driven by EffectInstanceHandle through a backend registered in LumenHostRegistry.
public enum LumenNodeFeature : byte
{
    // EffectNodeSpec.Distortion; backend owned by screen-distortion.
    Distortion = 0,
    // EffectNodeSpec.Ribbon; backend owned by ribbon-trails.
    Ribbon = 1,
}

// One node's state for the current frame.
// Transform: the node's world matrix and world rotation, parent composed (the same values emitters receive).
// TargetWorld: LumenPlayRequest.TargetWorld. MapIndex: the handle's resolved Map.uniqueID, fixed for its lifetime.
// Intensity: the handle's "intensity" parameter (finite >= 0). Active: the node belongs to the playing state;
// false after its state exits and while the handle drains. DeltaTime: seconds this frame, already the unscaled
// delta for a real-time handle; 0 in the frame passed to CreateInstance.
public readonly record struct LumenNodeFeatureFrame(
    LumenEmitterTransform Transform,
    Vector3? TargetWorld,
    int MapIndex,
    float Intensity,
    bool Active,
    float DeltaTime);

// Implemented by the line that owns a LumenNodeFeature; registered once through
// LumenHostRegistry.RegisterNodeFeatureBackend from the backend's own [StaticConstructorOnStartup] bootstrap.
// A backend that is also an ILumenFrameContributor calls LumenSceneBootstrap.EnsureInitialized() before
// LumenHostRegistry.RegisterContributor, so the VFX service pushes this frame's node updates before the backend captures.
// All calls arrive on the main thread from the per-frame advance or from ILumenEffectHandle calls. Lifecycle of one
// instance (one per node carrying the feature, per played handle):
//   CreateInstance   at Play, before the first frame. The instance starts inactive and shows nothing.
//   RestartInstance  each time the node's state is entered (including the initial state right after creation) and on
//                    ILumenEffectHandle.Trigger(node name): the node becomes active and its own timeline restarts at 0
//                    (ring radius, Duration, StrengthOverTime, trail points, beam noise).
//   UpdateInstance   once per frame while the instance is bound, and immediately with Active = false when the node's
//                    state exits. Inactive instances produce no new output; output already produced may finish.
//   DestroyInstance(id, false)  on Stop(true) drain: no new output; the handle keeps calling UpdateInstance (Active =
//                    false) and stays alive until IsInstanceAlive(id) returns false, then never uses id again.
//   DestroyInstance(id, true)   on Stop(false) / Dispose, also while draining: release now; id is never used again.
public interface ILumenNodeFeatureBackend
{
    LumenNodeFeature Feature { get; }

    // Requires: node carries this backend's feature spec (validated by LumenAssetRules); seed != 0.
    // Ensures: returns an instance id >= 0, unique among this backend's live instances.
    int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed);

    void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame);

    void RestartInstance(int instanceId);

    void DestroyInstance(int instanceId, bool immediate);

    // Ensures: false once a drained instance has nothing left to show, and after an immediate destroy.
    bool IsInstanceAlive(int instanceId);
}
