#version 330 core

// Full-screen triangle from gl_VertexID 0..2 — no VBO needed.
//   v0 (-1,-1)  v1 (3,-1)  v2 (-1,3)
// covers the [-1,1]² NDC quad with one triangle (extends past corners).
//
// UV is derived from NDC and Y-flipped so the texture's row 0 lands at
// the top of the screen (textures uploaded with row-0-on-top via
// glTexImage2D's default origin).

out vec2 v_uv;

void main()
{
    vec2 pos = vec2(
        (gl_VertexID == 1) ?  3.0 : -1.0,
        (gl_VertexID == 2) ?  3.0 : -1.0
    );
    v_uv = (pos + 1.0) * 0.5;
    v_uv.y = 1.0 - v_uv.y;
    gl_Position = vec4(pos, 0.0, 1.0);
}
