#ifndef LUMEN_FORCE_ARROW_TYPES
#define LUMEN_FORCE_ARROW_TYPES

// Dev-only force arrows (Showcase). Lives outside 1.7/Shaders so the release package never carries it; the includes
// reach the shipped particle sources through the repository layout. C# twin: ShowcaseForceArrows.

// std430, 48 bytes. Start.w is the channel + 1 (0 = no arrow); Vector is world-space, already scaled and clamped.
struct GpuForceArrow
{
    vec4 Start;  // xyz: particle world position, w: channel + 1
    vec4 Vector; // xyz: arrow world vector, w: drawn length (cells)
    vec4 Anchor; // xy: particle ground anchor XZ for the Vertical3D display rule
};

// Sampled particles per emitter and arrows per sampled particle.
const uint LUMEN_ARROW_SAMPLES = 32u;
const uint LUMEN_ARROW_CHANNELS = 7u;

// Channels: 0 velocity, 1 gravity (GravityDrag acceleration), 2 drag (-drag * velocity), 3 vortex, 4 curl turbulence,
// 5 point attractor, 6 ring wave / world pulse (kinds 4..7).
const uint LUMEN_ARROW_VELOCITY = 0u;
const uint LUMEN_ARROW_DRAG = 2u;

// Drawn length = min(|v| * scale, LUMEN_ARROW_MAX_LENGTH) cells; scale is seconds for velocity, seconds^2 for forces.
const float LUMEN_ARROW_VELOCITY_SCALE = 0.25;
const float LUMEN_ARROW_FORCE_SCALE = 0.08;
const float LUMEN_ARROW_MAX_LENGTH = 1.5;

const vec3 LUMEN_ARROW_COLORS[7] = vec3[7](
    vec3(1.0, 1.0, 1.0),   // velocity: white
    vec3(1.0, 0.85, 0.1),  // gravity: yellow
    vec3(0.55, 0.55, 0.55),// drag: grey
    vec3(0.1, 0.85, 1.0),  // vortex: cyan
    vec3(1.0, 0.2, 0.9),   // curl turbulence: magenta
    vec3(0.2, 1.0, 0.3),   // attractor: green
    vec3(1.0, 0.5, 0.1));  // ring wave / pulse: orange

uint lumenArrowChannelOfKind(uint kind)
{
    return kind == 0u ? 1u : kind == 1u ? 3u : kind == 2u ? 4u : kind == 3u ? 5u : 6u;
}

#endif
