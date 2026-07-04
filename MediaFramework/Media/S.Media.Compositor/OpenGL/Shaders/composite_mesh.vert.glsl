#version 330 core

// Warp-pass mesh vertex shader (Doc/HaPlay-Output-Mapping-Plan.md Phase 4). Unlike
// composite_layer.vert.glsl (unit quad placed by an affine uXform), each vertex carries its
// pre-warped destination position in output pixels (aPos, evaluated CPU-side from the mesh
// control points) plus the section-normalized parameter (aUV) that drives source sampling.
// uXform here only maps output pixels to NDC.

layout(location = 0) in vec2 aUV;
layout(location = 1) in vec2 aPos;
uniform mat3 uXform;
// Source crop sub-rectangle (x0, y0, x1, y1) in [0,1] UV, same semantics as the layer pass.
uniform vec4 uCrop;
out vec2 vUV;

void main()
{
    vec3 p = uXform * vec3(aPos, 1.0);
    gl_Position = vec4(p.xy, 0.0, 1.0);
    vUV = mix(uCrop.xy, uCrop.zw, aUV);
}
