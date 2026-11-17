// CPU twin: Scene/ParticleCollisionMath.cs. Point sweep through XZ cells, walls extruded over Y=[0,2].
bool slab(float p, float d, float low, float high, vec3 axis, inout float enter, inout float exit, inout vec3 normal)
{
    if (d == 0.0) return p >= low && p < high;
    float a = (low - p) / d, b = (high - p) / d;
    if (a > b) { float swap = a; a = b; b = swap; }
    if (a >= enter) { enter = a; normal = d > 0.0 ? -axis : axis; }
    exit = min(exit, b);
    return enter <= exit;
}

void nearestFace(float distance, vec3 axis, inout float nearest, inout vec3 normal)
{
    if (distance < nearest) { nearest = distance; normal = axis; }
}

// 0=no hit, 1=wall, 2=roof expiry (not a collision event).
uint traceMap(GpuEmitterParams emitter, vec3 from, vec3 to, out vec3 position, out vec3 normal)
{
    position = to; normal = vec3(0.0);
    int width = int(emitter.Map.y), height = int(emitter.Map.z);
    if (width == 0) return 0u;
    vec3 delta = to - from, unused = vec3(0.0);
    float begin = 0.0, end = 1.0;
    if (!slab(from.x, delta.x, 0.0, float(width), vec3(1,0,0), begin, end, unused) ||
        !slab(from.z, delta.z, 0.0, float(height), vec3(0,0,1), begin, end, unused)) return 0u;
    vec3 start = from + delta * begin;
    int x = clamp(int(floor(start.x)), 0, width - 1), z = clamp(int(floor(start.z)), 0, height - 1);
    int sx = int(sign(delta.x)), sz = int(sign(delta.z));
    float infinity = uintBitsToFloat(0x7f800000u);
    float tx = sx == 0 ? infinity : (float(sx > 0 ? x + 1 : x) - from.x) / delta.x;
    float tz = sz == 0 ? infinity : (float(sz > 0 ? z + 1 : z) - from.z) / delta.z;
    while (x >= 0 && z >= 0 && x < width && z < height && begin <= end)
    {
        uint cell = mapCells[uint(emitter.Map.x) + uint(z * width + x)];
        if (emitter.Map.w > 0.5 && (cell & 2u) != 0u)
        { position = from + delta * begin; return 2u; }
        if (emitter.Collision.x != 0.0 && (cell & 1u) != 0u)
        {
            float enter = 0.0, exit = 1.0;
            normal = vec3(0.0);
            if (slab(from.x, delta.x, float(x), float(x+1), vec3(1,0,0), enter, exit, normal) &&
                slab(from.y, delta.y, 0.0, 2.0, vec3(0,1,0), enter, exit, normal) &&
                slab(from.z, delta.z, float(z), float(z+1), vec3(0,0,1), enter, exit, normal))
            {
                position = from + delta * enter;
                if (normal == vec3(0.0))
                {
                    float nearest = infinity;
                    nearestFace(position.x - float(x), vec3(-1,0,0), nearest, normal);
                    nearestFace(float(x+1) - position.x, vec3(1,0,0), nearest, normal);
                    nearestFace(position.y, vec3(0,-1,0), nearest, normal);
                    nearestFace(2.0 - position.y, vec3(0,1,0), nearest, normal);
                    nearestFace(position.z - float(z), vec3(0,0,-1), nearest, normal);
                    nearestFace(float(z+1) - position.z, vec3(0,0,1), nearest, normal);
                    position += normal * nearest;
                }
                position += normal * 0.0001;
                return 1u;
            }
        }
        if (tx < tz) { begin = tx; tx += 1.0 / abs(delta.x); x += sx; }
        else { begin = tz; tz += 1.0 / abs(delta.z); z += sz; }
    }
    return 0u;
}
