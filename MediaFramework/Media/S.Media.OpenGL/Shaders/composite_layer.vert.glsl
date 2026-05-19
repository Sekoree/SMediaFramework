#version 330 core

// Compositor vertex shader. Each layer is drawn as a 6-vertex quad in [0,1]^2 UV space; the host
// pre-bakes the source-pixel scale, the layer's LayerTransform2D, the output-NDC mapping, and a
// Y-flip (so glReadPixels in bottom-up order yields a top-down framebuffer) into uXform.

layout(location = 0) in vec2 aUV;
uniform mat3 uXform;
out vec2 vUV;

void main()
{
    vec3 p = uXform * vec3(aUV, 1.0);
    gl_Position = vec4(p.xy, 0.0, 1.0);
    vUV = aUV;
}
