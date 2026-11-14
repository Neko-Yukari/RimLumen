namespace RimLumen.Core.Rendering.Probes;

// Fixtures for the Route B probe step. They live here (not inline in the coroutine) so the offline
// suite can compile the exact bytes the real-machine probe writes before it ever runs.
internal static class ShaderPackProbeSources
{
    internal const string UboPrelude =
        """
        #version 450
        layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
        {
            mat4 InvViewProjection;
            vec4 ScreenAndMapSize;
            vec4 EnvParams;
            vec4 BloomParams;
            vec4 PackParam0;
            vec4 PackParam1;
            vec4 PackParam2;
            vec4 PackParam3;
        } ubo;
        layout(push_constant) uniform Params { vec4 value; } pc;
        """;

    // Each output channel is driven by exactly one input path, with no constant offsets, so the readback can
    // tell which paths really ran:
    //   R = scene depth only   -> 0 on both flat images (reverse-Z cleared depth 0.0, depth1x1 placeholder 1.0),
    //       > 0 only for drawn geometry strictly inside (0, 1)
    //   G = unprojected map light field (plus its own feedback) -> 0 unless InvViewProjection lands on the map
    //       and the light-field texture holds non-zero bytes there
    //   B = the runtime parameter path, bottom-right quadrant only -> 0 everywhere in the left 40%
    internal static readonly string Accum = UboPrelude + "\n" +
        """
        layout(set = 0, binding = 0) uniform sampler2D hdrImage;
        layout(set = 0, binding = 1) uniform sampler2D prevHistory;
        layout(set = 0, binding = 2) uniform sampler2D depthImage;
        layout(set = 0, binding = 3) uniform sampler2D lightField;
        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 outColor;

        vec3 unproject(vec2 ndc, float z)
        {
            vec4 p = ubo.InvViewProjection * vec4(ndc, z, 1.0);
            return p.xyz / p.w;
        }

        void main()
        {
            float depth = texture(depthImage, uv).r;
            float r = clamp(min(depth, 1.0 - depth) * 10.0, 0.0, 1.0);

            vec2 ndc = uv * 2.0 - 1.0;
            vec3 nearPoint = unproject(ndc, 0.0);
            vec3 farPoint = unproject(ndc, 1.0);
            float span = farPoint.y - nearPoint.y;
            float t = clamp(-nearPoint.y / (abs(span) < 1e-6 ? 1e-6 : span), 0.0, 1.0);
            vec3 world = mix(nearPoint, farPoint, t);

            // ScreenAndMapSize.zw is the map size in cells, so world XZ / size is normalized cell space.
            vec2 fieldUv = world.xz / max(ubo.ScreenAndMapSize.zw, vec2(1.0));
            vec2 clampedUv = clamp(fieldUv, vec2(0.0), vec2(1.0));
            bool insideMap = abs(clampedUv.x - fieldUv.x) < 1e-6 && abs(clampedUv.y - fieldUv.y) < 1e-6;
            vec4 field = insideMap ? texture(lightField, clampedUv) : vec4(0.0);
            float g = clamp(max(max(field.r, field.g), max(field.b, field.a))
                + texture(prevHistory, uv).g * 0.25, 0.0, 1.0);

            float b = (uv.x > 0.5 && uv.y > 0.5) ? (ubo.PackParam0.x + pc.value.z) : 0.0;

            outColor = vec4(r, g, b, 1.0);
        }
        """;

    internal static readonly string Blur = UboPrelude + "\n" +
        """
        layout(set = 0, binding = 0) uniform sampler2D historyImage;
        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec4 sum = texture(historyImage, uv + pc.value.xy * vec2(-1.0, 0.0));
            sum += texture(historyImage, uv + pc.value.xy * vec2(1.0, 0.0));
            outColor = vec4(sum.rgb * 0.5, 1.0);
        }
        """;

    // Custom built-in-bloom prefilter. The runtime's bloom prefilter pass binds exactly {0 hdr, 2 scene,
    // 3 depth, 4 mapLightField}, so those are the only samplers declared here. Setting
    // BloomPrefilterShaderPath on a pack makes the runtime record the whole 6-mip down/up chain even on
    // frames without HDR draws, which is the path the real-machine probe has to exercise under VVL.
    internal static readonly string Prefilter = UboPrelude + "\n" +
        """
        layout(set = 0, binding = 0) uniform sampler2D hdrImage;
        layout(set = 0, binding = 2) uniform sampler2D sceneImage;
        layout(set = 0, binding = 3) uniform sampler2D depthImage;
        layout(set = 0, binding = 4) uniform sampler2D lightField;
        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            float depth = texture(depthImage, uv).r;
            // Geometry-only mask: reverse-Z clears to 0.0 and the depth1x1 placeholder is 1.0, so only real
            // drawn depths strictly inside (0, 1) survive.
            float geometry = min(depth, 1.0 - depth);
            float mask = step(0.0001, geometry);
            vec3 color = texture(hdrImage, uv).rgb
                + vec3(texture(lightField, uv).g * 0.01)
                + texture(sceneImage, uv).rgb * 0.05;
            float brightness = max(color.r, max(color.g, color.b));
            float threshold = pc.value.z;
            float knee = pc.value.w;
            float soft = clamp(brightness - threshold + knee, 0.0, 2.0 * knee);
            soft = soft * soft / max(4.0 * knee, 1e-5);
            float contribution = max(brightness - threshold, soft) / max(brightness, 0.00001);
            outColor = vec4(color * contribution * mask, 0.0);
        }
        """;

    internal static readonly string Final = UboPrelude + "\n" +
        """
        layout(set = 0, binding = 0) uniform sampler2D hdrImage;
        layout(set = 0, binding = 1) uniform sampler2D bloomImage;
        layout(set = 0, binding = 2) uniform sampler2D blurImage;
        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec3 color = texture(hdrImage, uv).rgb
                + texture(bloomImage, uv).rgb * ubo.BloomParams.x
                + texture(blurImage, uv).rgb;
            outColor = vec4(color, 1.0);
        }
        """;

    internal const string Broken =
        """
        #version 450
        layout(location = 0) out vec4 outColor;
        void main() { outColor = undefined_symbol; }
        """;
}
