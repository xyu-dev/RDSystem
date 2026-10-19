using UnityEngine;

/// <summary>
/// Generates a dense latitude/longitude sphere at runtime, because the deformation is a vertex
/// displacement and the room model does not have the vertex count to show a smooth dent.
///
/// A lat/long sphere rather than a cube-sphere, despite the uneven vertex spacing, for one
/// reason: its UVs are exactly equirectangular, so `hit.textureCoord` returns the same UV the
/// simulation is indexed by and the gaze lands where the viewer is actually looking. A
/// cube-sphere has more even vertices but no natural equirectangular UV, and the seam triangles
/// would interpolate across the whole sphere - which shows up as the ring jumping when someone
/// looks at the seam.
///
/// The longitude seam is handled the standard way, by duplicating the u = 1 column.
/// </summary>
public static class RDSphereMesh
{
    /// <summary>
    /// Builds the sphere. segmentsU should be about twice segmentsV, matching the 2:1 aspect of an
    /// equirectangular map. 256 x 128 gives 33k vertices - comfortably inside the 16-bit index
    /// limit - and about 25 cm between vertices at the equator of a 10 m sphere, which is finer
    /// than any dent worth looking at.
    /// </summary>
    public static Mesh Build(float radius, int segmentsU = 256, int segmentsV = 128, string name = "RD Sphere")
    {
        segmentsU = Mathf.Clamp(segmentsU, 8, 512);
        segmentsV = Mathf.Clamp(segmentsV, 4, 256);

        int columns = segmentsU + 1;     // the extra column closes the seam
        int rows = segmentsV + 1;

        int vertexCount = columns * rows;

        if (vertexCount > 65000)
        {
            Debug.LogWarning("[RDSphereMesh] " + segmentsU + "x" + segmentsV + " needs " +
                             vertexCount + " vertices; switching to a 32-bit index buffer.");
        }

        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];

        for (int y = 0; y < rows; y++)
        {
            // v maps to colatitude, exactly as SphereUVToDir does in the shaders.
            float v = y / (float)segmentsV;
            float theta = v * Mathf.PI;

            float sinTheta = Mathf.Sin(theta);
            float cosTheta = Mathf.Cos(theta);

            for (int x = 0; x < columns; x++)
            {
                float u = x / (float)segmentsU;
                float phi = u * 2f * Mathf.PI;

                Vector3 dir = new Vector3(
                    sinTheta * Mathf.Cos(phi),
                    cosTheta,
                    sinTheta * Mathf.Sin(phi));

                int i = y * columns + x;

                vertices[i] = dir * radius;
                normals[i] = dir;            // outward, so displacing along +normal grows the space
                uvs[i] = new Vector2(u, v);
            }
        }

        int[] triangles = new int[segmentsU * segmentsV * 6];
        int t = 0;

        for (int y = 0; y < segmentsV; y++)
        {
            for (int x = 0; x < segmentsU; x++)
            {
                int a = y * columns + x;
                int b = a + 1;
                int c = a + columns;
                int d = c + 1;

                triangles[t++] = a;
                triangles[t++] = c;
                triangles[t++] = b;

                triangles[t++] = b;
                triangles[t++] = c;
                triangles[t++] = d;
            }
        }

        Mesh mesh = new Mesh { name = name };
        mesh.indexFormat = vertexCount > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;

        // The displacement happens on the GPU, so the bounds have to be widened by hand or the
        // sphere gets frustum-culled the moment a dent pushes past its original radius.
        mesh.RecalculateBounds();

        return mesh;
    }

    /// <summary>
    /// Widen the bounds to allow for GPU displacement. Without this the renderer is culled using
    /// the undeformed radius and the surface vanishes when the viewer turns away from a deep dent.
    /// </summary>
    public static void ExpandBoundsForDisplacement(Mesh mesh, float maxDisplacement)
    {
        if (mesh == null) return;

        Bounds bounds = mesh.bounds;
        bounds.Expand(Mathf.Max(0f, maxDisplacement) * 2f);
        mesh.bounds = bounds;
    }
}
