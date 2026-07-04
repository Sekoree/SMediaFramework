namespace S.Media.Source.MMD;

/// <summary>
/// Rudimentary z-buffered software renderer for the MMD prototype: flat-shaded triangles with the
/// material diffuse colour and one directional light, MMD→right-handed conversion, VMD camera
/// conventions (orbit target + distance + XYZ euler + vertical fov). Good enough to SEE the animated
/// model and place the camera; the real material/toon/outline pipeline is the staged GL work (NXT-10).
/// </summary>
public sealed class MMDSoftwareRenderer(int width, int height)
{
    private readonly float[] _depth = new float[width * height];

    public int Width { get; } = width > 0 ? width : throw new ArgumentOutOfRangeException(nameof(width));
    public int Height { get; } = height > 0 ? height : throw new ArgumentOutOfRangeException(nameof(height));

    /// <summary>Renders the posed model into a BGRA32 buffer (opaque black background).</summary>
    /// <summary>One decoded RGBA texture for the CPU rasterizer (nearest-neighbor sampling).</summary>
    public sealed record MMDCpuTexture(int Width, int Height, byte[] Rgba);

    private MMDCpuTexture?[]? _materialTextures;

    /// <summary>Per-material diffuse textures (index-aligned with the model's materials; null entries
    /// render with the flat diffuse color). Loaded once by the host — the preview/CPU-fallback path
    /// then shows the real model textures instead of a grayscale raster (operator request).</summary>
    public void SetTextures(MMDCpuTexture?[]? materialTextures) => _materialTextures = materialTextures;

    public void Render(
        PMXDocument model, Vector3[] positions, VMDCameraFrame camera, byte[] bgra,
        VMDLightFrame? lightFrame = null, bool visible = true,
        IReadOnlyList<Vector2>? posedUvs = null,
        IReadOnlyList<MMDMaterialState>? materialStates = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(positions);
        if (bgra.Length < Width * Height * 4)
            throw new ArgumentException("bgra buffer too small", nameof(bgra));

        Array.Fill(_depth, float.PositiveInfinity);
        Array.Clear(bgra);
        for (var i = 3; i < Width * Height * 4; i += 4)
            bgra[i] = 255; // opaque
        if (!visible)
            return;

        // MMD is left-handed with +Z into the screen; VMD camera distance is negative toward the viewer.
        // View: orbit around Target by euler (x pitch, y yaw, z roll), then push out by |distance|.
        var rotation =
            Matrix4x4.CreateRotationY(camera.RotationRadians.Y) *
            Matrix4x4.CreateRotationX(-camera.RotationRadians.X) *
            Matrix4x4.CreateRotationZ(-camera.RotationRadians.Z);
        var forward = Vector3.TransformNormal(new Vector3(0, 0, 1), rotation);
        var up = Vector3.TransformNormal(new Vector3(0, 1, 0), rotation);
        var eye = camera.Target - forward * MathF.Abs(camera.Distance);
        var view = Matrix4x4.CreateLookAt(eye, camera.Target, up);
        var fov = Math.Clamp(camera.FovDegrees, 1f, 175f) * MathF.PI / 180f;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, (float)Width / Height, 0.5f, 2000f);
        var viewProjection = view * projection;
        var lightState = lightFrame ?? new VMDLightFrame(0, Vector3.One, new Vector3(-0.3f, -1f, 0.6f));
        var light = lightState.Direction.LengthSquared() > 1e-8f
            ? Vector3.Normalize(lightState.Direction)
            : Vector3.Normalize(new Vector3(-0.3f, -1f, 0.6f));

        // Project all vertices once (positions are already posed/skinned, in MMD space: flip Z for RH).
        var screen = new Vector4[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            var clip = Vector4.Transform(new Vector4(p.X, p.Y, -p.Z, 1f), viewProjection);
            screen[i] = clip;
        }

        // Materials own consecutive face-vertex ranges, in order.
        var indices = model.Indices;
        var faceCursor = 0;
        var vertices = model.Vertices;
        for (var mi = 0; mi < model.Materials.Count; mi++)
        {
            var material = model.Materials[mi];
            var materialState = materialStates is not null && mi < materialStates.Count
                ? materialStates[mi]
                : MMDMaterialState.From(material);
            var color = materialState.Diffuse;
            color.X *= lightState.Color.X;
            color.Y *= lightState.Color.Y;
            color.Z *= lightState.Color.Z;
            var texture = _materialTextures is { } set && mi < set.Length ? set[mi] : null;
            var end = Math.Min(faceCursor + material.FaceVertexCount, indices.Count);
            for (var i = faceCursor; i + 2 < end + 0; i += 3)
            {
                int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                RasterizeTriangle(
                    screen[i0], screen[i1], screen[i2],
                    positions[i0], positions[i1], positions[i2],
                    posedUvs is not null ? posedUvs[i0] : vertices[i0].Uv,
                    posedUvs is not null ? posedUvs[i1] : vertices[i1].Uv,
                    posedUvs is not null ? posedUvs[i2] : vertices[i2].Uv,
                    color, materialState.TextureMultiply, materialState.TextureAdd,
                    light, material.DoubleSided, texture, bgra);
            }

            faceCursor = end;
        }
    }

    private void RasterizeTriangle(
        Vector4 c0, Vector4 c1, Vector4 c2,
        Vector3 w0, Vector3 w1, Vector3 w2,
        Vector2 uv0, Vector2 uv1, Vector2 uv2,
        Vector4 diffuse, Vector4 textureMultiply, Vector4 textureAdd,
        Vector3 light, bool doubleSided, MMDCpuTexture? texture, byte[] bgra)
    {
        // Reject triangles behind the near plane entirely (no clipping in the prototype).
        if (c0.W <= 0.01f || c1.W <= 0.01f || c2.W <= 0.01f)
            return;

        var p0 = ToScreen(c0);
        var p1 = ToScreen(c1);
        var p2 = ToScreen(c2);
        var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (area == 0 || (!doubleSided && area > 0))
            return; // degenerate or back-facing (MMD winding is clockwise-front after the Z flip)

        var minX = Math.Max(0, (int)MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        var maxX = Math.Min(Width - 1, (int)MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(Height - 1, (int)MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        if (minX > maxX || minY > maxY)
            return;

        // Flat shade from the geometric normal (posed world space, MMD handedness).
        var normal = Vector3.Cross(w1 - w0, w2 - w0);
        var len = normal.Length();
        if (len < 1e-9f)
            return;
        normal /= len;
        var intensity = Math.Clamp(MathF.Abs(Vector3.Dot(normal, -light)), 0f, 1f) * 0.75f + 0.25f;
        var b = (byte)Math.Clamp((int)(diffuse.Z * intensity * 255f), 0, 255);
        var g = (byte)Math.Clamp((int)(diffuse.Y * intensity * 255f), 0, 255);
        var r = (byte)Math.Clamp((int)(diffuse.X * intensity * 255f), 0, 255);

        var invArea = 1f / area;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                var e0 = (p1.X - p0.X) * (py - p0.Y) - (px - p0.X) * (p1.Y - p0.Y);
                var e1 = (p2.X - p1.X) * (py - p1.Y) - (px - p1.X) * (p2.Y - p1.Y);
                var e2 = (p0.X - p2.X) * (py - p2.Y) - (px - p2.X) * (p0.Y - p2.Y);
                var inside = area < 0
                    ? e0 <= 0 && e1 <= 0 && e2 <= 0
                    : e0 >= 0 && e1 >= 0 && e2 >= 0;
                if (!inside)
                    continue;

                // Barycentric depth (screen-space linear — adequate for a preview renderer).
                var b1 = e2 * invArea;
                var b2 = e0 * invArea;
                var b0 = 1f - b1 - b2;
                var depth = b0 * p0.Z + b1 * p1.Z + b2 * p2.Z;
                var index = y * Width + x;
                if (depth >= _depth[index])
                    continue;
                _depth[index] = depth;
                var offset = index * 4;
                if (texture is not null)
                {
                    // Nearest-neighbor texel (screen-space barycentric UV — adequate for the preview).
                    var u = b0 * uv0.X + b1 * uv1.X + b2 * uv2.X;
                    var v = b0 * uv0.Y + b1 * uv1.Y + b2 * uv2.Y;
                    var tx = Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1);
                    var ty = Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1);
                    var t = (ty * texture.Width + tx) * 4;
                    var tr = ApplyTextureMorph(texture.Rgba[t] / 255f, textureMultiply.X,
                        textureMultiply.W, textureAdd.X, textureAdd.W);
                    var tg = ApplyTextureMorph(texture.Rgba[t + 1] / 255f, textureMultiply.Y,
                        textureMultiply.W, textureAdd.Y, textureAdd.W);
                    var tb = ApplyTextureMorph(texture.Rgba[t + 2] / 255f, textureMultiply.Z,
                        textureMultiply.W, textureAdd.Z, textureAdd.W);
                    var ta = texture.Rgba[t + 3] / 255f;
                    if (ta * diffuse.W < 8f / 255f)
                        continue; // fully transparent texel — leave what is behind
                    bgra[offset] = ToByte(tb * diffuse.Z * intensity);
                    bgra[offset + 1] = ToByte(tg * diffuse.Y * intensity);
                    bgra[offset + 2] = ToByte(tr * diffuse.X * intensity);
                    bgra[offset + 3] = ToByte(ta * diffuse.W);
                    continue;
                }

                bgra[offset] = b;
                bgra[offset + 1] = g;
                bgra[offset + 2] = r;
                bgra[offset + 3] = 255;
            }
        }

        static byte ToByte(float value) =>
            (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        static float ApplyTextureMorph(float sampled, float multiply, float multiplyBlend,
            float add, float addBlend)
        {
            sampled = float.Lerp(1f, sampled * multiply, multiplyBlend);
            return Math.Clamp(sampled + (sampled - 1f) * addBlend, 0f, 1f) + add;
        }
    }

    private Vector3 ToScreen(Vector4 clip)
    {
        var inv = 1f / clip.W;
        return new Vector3(
            (clip.X * inv * 0.5f + 0.5f) * Width,
            (1f - (clip.Y * inv * 0.5f + 0.5f)) * Height,
            clip.Z * inv);
    }
}
