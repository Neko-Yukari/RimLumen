using System.Numerics;

namespace RimLumen.Core.Contracts;

// World uses Numerics row-vector SRT, including scale/shear. Rotation is the unit world quaternion
// composed from root yaw and the full authored parent rotation chain, independent of all scales.
// Callers supply both explicitly and update them together; never infer Rotation from World.
public readonly record struct LumenEmitterTransform(Matrix4x4 World, Quaternion Rotation);
