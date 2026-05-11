#version 330 core

// Full-screen triangle from gl_VertexID 0..2 — no VBO needed.
//   v0 (-1,-1)  v1 (3,-1)  v2 (-1,3)
// covers the [-1,1]² NDC quad with one triangle (extends past corners).
//
// UV is derived from NDC. When yUvFlip is 1, the V coordinate is flipped so
// row 0 of uploaded textures (FFmpeg / typical CPU buffers) appears at
// the top of the screen. Set yUvFlip to 0 for bottom-up sources (e.g. some
// FBO attachments) so sampling matches GL's default texture origin.

uniform float yUvFlip;

out vec2 v_uv;

void main()
{
    vec2 pos = vec2(
        (gl_VertexID == 1) ?  3.0 : -1.0,
        (gl_VertexID == 2) ?  3.0 : -1.0
    );
    v_uv = (pos + 1.0) * 0.5;
    v_uv.y = mix(v_uv.y, 1.0 - v_uv.y, yUvFlip);
    gl_Position = vec4(pos, 0.0, 1.0);
}
