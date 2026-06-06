#version 330 core

// Compositor vertex shader. Each layer is drawn as a 6-vertex quad in [0,1]^2 UV space; the host
// pre-bakes the source-pixel scale, the layer's LayerTransform2D, the output-NDC mapping, and a
// Y-flip (so glReadPixels in bottom-up order yields a top-down framebuffer) into uXform.

layout(location = 0) in vec2 aUV;
uniform mat3 uXform;
// Source crop sub-rectangle (x0, y0, x1, y1) in [0,1] UV. (0,0,1,1) = whole frame (no crop).
uniform vec4 uCrop;
out vec2 vUV;

void main()
{
    // Remap the base [0,1] quad onto the crop sub-rectangle, then place it via uXform. Only the
    // cropped region is rasterized and sampled, so trimmed edges never spill onto the canvas.
    vec2 uv = mix(uCrop.xy, uCrop.zw, aUV);
    vec3 p = uXform * vec3(uv, 1.0);
    gl_Position = vec4(p.xy, 0.0, 1.0);
    vUV = uv;
}
