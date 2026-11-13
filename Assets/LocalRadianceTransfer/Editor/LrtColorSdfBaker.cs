using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

internal sealed class LrtColorSdfBaker
{
    internal readonly struct Source
    {
        public readonly Mesh Mesh;
        public readonly Matrix4x4 LocalToRoot;
        public readonly Material[] Materials;

        public Source(Mesh mesh, Matrix4x4 localToRoot, Material[] materials)
        {
            Mesh = mesh;
            LocalToRoot = localToRoot;
            Materials = materials;
        }
    }

    private sealed class TexturePixels
    {
        private readonly Color32[] pixels;
        private readonly int width;
        private readonly int height;
        private readonly TextureWrapMode wrapU;
        private readonly TextureWrapMode wrapV;
        private readonly FilterMode filter;

        public TexturePixels(Texture2D source)
        {
            width = Mathf.Min(source.width, 1024);
            height = Mathf.Min(source.height, 1024);
            wrapU = source.wrapModeU;
            wrapV = source.wrapModeV;
            filter = source.filterMode;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                pixels = copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(copy);
            }
        }

        public Color Sample(Vector2 uv)
        {
            if (filter == FilterMode.Point)
                return Pixel(Mathf.FloorToInt(uv.x * width), Mathf.FloorToInt(uv.y * height));

            float px = uv.x * width - 0.5f;
            float py = uv.y * height - 0.5f;
            int x = Mathf.FloorToInt(px);
            int y = Mathf.FloorToInt(py);
            return Color.Lerp(Color.Lerp(Pixel(x, y), Pixel(x + 1, y), px - x),
                Color.Lerp(Pixel(x, y + 1), Pixel(x + 1, y + 1), px - x), py - y);
        }

        private Color Pixel(int x, int y) => pixels[Wrap(x, width, wrapU) + width * Wrap(y, height, wrapV)];

        private static int Wrap(int index, int size, TextureWrapMode mode)
        {
            if (mode == TextureWrapMode.Repeat)
                return (index % size + size) % size;
            if (mode == TextureWrapMode.Mirror)
            {
                int period = size * 2;
                int wrapped = (index % period + period) % period;
                return wrapped < size ? wrapped : period - 1 - wrapped;
            }
            if (mode == TextureWrapMode.MirrorOnce && index < 0)
                index = -1 - index;
            return Mathf.Clamp(index, 0, size - 1);
        }
    }

    private sealed class Surface
    {
        public Color Tint = Color.white;
        public Vector2 Scale = Vector2.one;
        public Vector2 Offset;
        public TexturePixels Texture;

        public Color32 Sample(Vector2 uv) => Tint * (Texture?.Sample(Vector2.Scale(uv, Scale) + Offset) ?? Color.white);
    }

    private struct Triangle
    {
        public Vector3 A, B, C;
        public Vector2 UvA, UvB, UvC;
        public Bounds Bounds;
        public Surface Surface;
    }

    private sealed class Node
    {
        public Bounds Bounds;
        public int Start, Count;
        public Node Left, Right;
    }

    private readonly Dictionary<Texture2D, TexturePixels> textures = new Dictionary<Texture2D, TexturePixels>();
    private readonly Dictionary<Material, Surface> surfaces = new Dictionary<Material, Surface>();
    private readonly Surface defaultSurface = new Surface();

    internal static bool IsOpaque(Material material) => material == null ||
        (material.renderQueue < (int)UnityEngine.Rendering.RenderQueue.Transparent &&
         (!material.HasProperty("_Surface") || material.GetFloat("_Surface") < 0.5f));

    public LrtColorSdf Bake(List<Source> sources, int resolution)
    {
        var triangles = new List<Triangle>();
        foreach (Source source in sources)
            ReadTriangles(source, triangles);
        if (triangles.Count == 0)
            return null;

        Triangle[] mesh = triangles.ToArray();
        Node tree = Build(mesh, 0, mesh.Length);
        float spacing = Mathf.Max(tree.Bounds.size.x, tree.Bounds.size.y, tree.Bounds.size.z) / resolution;
        Vector3Int size = Vector3Int.CeilToInt(tree.Bounds.size / spacing) + Vector3Int.one * 4;
        var bounds = new Bounds(tree.Bounds.center, (Vector3)size * spacing);
        Vector3 first = bounds.min + Vector3.one * (spacing * 0.5f);
        int count = size.x * size.y * size.z;
        var distances = new float[count];
        var outside = new bool[count];
        var data = new byte[count * 4];
        float shell = spacing * 0.8660254f;
        int slice = size.x * size.y;
        int index = 0;
        for (int z = 0; z < size.z; z++)
        for (int y = 0; y < size.y; y++)
        for (int x = 0; x < size.x; x++, index++)
        {
            Vector3 position = first + new Vector3(x, y, z) * spacing;
            float best = float.PositiveInfinity;
            int nearest = 0;
            Vector2 uv = default;
            FindNearest(tree, mesh, position, ref best, ref nearest, ref uv);
            distances[index] = Mathf.Sqrt(best);
            Color32 color = mesh[nearest].Surface.Sample(uv);
            data[index * 4 + 1] = color.r;
            data[index * 4 + 2] = color.g;
            data[index * 4 + 3] = color.b;
        }

        // The padded border is outside. Triangle-intersecting voxels close the surface;
        // flood through empty space to distinguish enclosed interiors from exterior air.
        var queue = new Queue<int>();
        Visit(0);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % size.x;
            int y = i / size.x % size.y;
            int z = i / slice;
            if (x > 0) Visit(i - 1);
            if (x + 1 < size.x) Visit(i + 1);
            if (y > 0) Visit(i - size.x);
            if (y + 1 < size.y) Visit(i + size.x);
            if (z > 0) Visit(i - slice);
            if (z + 1 < size.z) Visit(i + slice);
        }

        float distanceScale = 4f * spacing / 127f;
        for (int i = 0; i < count; i++)
        {
            float distance = distances[i];
            float signed = outside[i] || distance <= shell ? distance - shell : -distance;
            data[i * 4] = (byte)(128 + Mathf.Clamp(Mathf.RoundToInt(signed / distanceScale), -127, 127));
        }

        var asset = ScriptableObject.CreateInstance<LrtColorSdf>();
        asset.SetBake(bounds, size, distanceScale, data);
        return asset;

        void Visit(int i)
        {
            if (outside[i] || distances[i] <= shell)
                return;
            outside[i] = true;
            queue.Enqueue(i);
        }
    }

    private void ReadTriangles(Source source, List<Triangle> triangles)
    {
        using var meshData = MeshUtility.AcquireReadOnlyMeshData(source.Mesh);
        Mesh.MeshData mesh = meshData[0];
        using var vertices = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp);
        using var uvs = new NativeArray<Vector2>(mesh.vertexCount, Allocator.Temp);
        mesh.GetVertices(vertices);
        if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
            mesh.GetUVs(0, uvs);
        NativeArray<Vector3> positions = vertices;
        for (int i = 0; i < vertices.Length; i++)
            positions[i] = source.LocalToRoot.MultiplyPoint3x4(vertices[i]);

        int subMeshCount = Mathf.Max(mesh.subMeshCount, source.Materials.Length);
        for (int i = 0; i < subMeshCount && mesh.subMeshCount > 0; i++)
        {
            int subMesh = Mathf.Min(i, mesh.subMeshCount - 1);
            var descriptor = mesh.GetSubMesh(subMesh);
            Material material = i < source.Materials.Length ? source.Materials[i] : null;
            if (descriptor.topology != MeshTopology.Triangles || !IsOpaque(material))
                continue;
            Surface surface = GetSurface(material);
            using var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp);
            mesh.GetIndices(indices, subMesh);
            for (int j = 0; j + 2 < indices.Length; j += 3)
            {
                int a = indices[j], b = indices[j + 1], c = indices[j + 2];
                Vector3 va = vertices[a], vb = vertices[b], vc = vertices[c];
                if (Vector3.Cross(vb - va, vc - va).sqrMagnitude <= 1e-18f)
                    continue;
                var bounds = new Bounds(va, Vector3.zero);
                bounds.Encapsulate(vb);
                bounds.Encapsulate(vc);
                triangles.Add(new Triangle
                {
                    A = va, B = vb, C = vc, UvA = uvs[a], UvB = uvs[b], UvC = uvs[c],
                    Bounds = bounds, Surface = surface
                });
            }
        }
    }

    private Surface GetSurface(Material material)
    {
        if (material == null)
            return defaultSurface;
        if (surfaces.TryGetValue(material, out Surface surface))
            return surface;

        surface = new Surface();
        string color = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
        string map = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
        if (material.HasProperty(color))
        {
            surface.Tint = material.GetColor(color);
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                surface.Tint = surface.Tint.linear;
        }
        if (material.HasProperty(map) && material.GetTexture(map) is Texture2D texture)
        {
            if (!textures.TryGetValue(texture, out TexturePixels pixels))
                textures.Add(texture, pixels = new TexturePixels(texture));
            surface.Texture = pixels;
            surface.Scale = material.GetTextureScale(map);
            surface.Offset = material.GetTextureOffset(map);
        }
        surfaces.Add(material, surface);
        return surface;
    }

    private static Node Build(Triangle[] mesh, int start, int count)
    {
        Bounds bounds = mesh[start].Bounds;
        for (int i = start + 1; i < start + count; i++)
            bounds.Encapsulate(mesh[i].Bounds);
        var node = new Node { Bounds = bounds, Start = start, Count = count };
        if (count <= 8)
            return node;

        Vector3 size = bounds.size;
        int axis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
        Array.Sort(mesh, start, count, Comparer<Triangle>.Create(
            (a, b) => a.Bounds.center[axis].CompareTo(b.Bounds.center[axis])));
        int half = count / 2;
        node.Left = Build(mesh, start, half);
        node.Right = Build(mesh, start + half, count - half);
        return node;
    }

    private static void FindNearest(Node node, Triangle[] mesh, Vector3 position, ref float best, ref int nearest, ref Vector2 uv)
    {
        if (node.Left != null)
        {
            Node first = node.Left, second = node.Right;
            float a = first.Bounds.SqrDistance(position), b = second.Bounds.SqrDistance(position);
            if (a > b)
            {
                (first, second) = (second, first);
                (a, b) = (b, a);
            }
            if (a <= best)
                FindNearest(first, mesh, position, ref best, ref nearest, ref uv);
            if (b <= best)
                FindNearest(second, mesh, position, ref best, ref nearest, ref uv);
            return;
        }

        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            Triangle triangle = mesh[i];
            Vector3 barycentric = ClosestBarycentric(position, triangle);
            Vector3 nearestPoint = barycentric.x * triangle.A + barycentric.y * triangle.B + barycentric.z * triangle.C;
            float squared = (position - nearestPoint).sqrMagnitude;
            if (squared >= best)
                continue;
            best = squared;
            nearest = i;
            uv = barycentric.x * triangle.UvA + barycentric.y * triangle.UvB + barycentric.z * triangle.UvC;
        }
    }

    private static Vector3 ClosestBarycentric(Vector3 p, Triangle t)
    {
        Vector3 ab = t.B - t.A, ac = t.C - t.A, ap = p - t.A;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.right;

        Vector3 bp = p - t.B;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.up;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v = d1 / (d1 - d3);
            return new Vector3(1 - v, v, 0);
        }

        Vector3 cp = p - t.C;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.forward;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w = d2 / (d2 - d6);
            return new Vector3(1 - w, 0, w);
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 >= d3 && d5 >= d6)
        {
            float w = (d4 - d3) / (d4 - d3 + d5 - d6);
            return new Vector3(0, 1 - w, w);
        }

        float inverse = 1f / (va + vb + vc);
        return new Vector3(va, vb, vc) * inverse;
    }
}
