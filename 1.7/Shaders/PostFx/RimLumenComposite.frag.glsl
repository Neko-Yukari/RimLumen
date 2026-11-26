#version 450
#include "RimLumenPackComposite.glslinc"
layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 6) uniform sampler2D distortionImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = lumenCompositeDefault(lumenSampleLayer(hdrImage, bloomImage, distortionImage, uv, pc.value.x), sceneImage);
}
