using UnityEngine;

namespace ImmerseTheBay.Media
{
    /// <summary>Latitude/longitude geometry with inward winding and normals, +Z at the panorama centre.</summary>
    internal static class PanoramaMesh
    {
        public static Mesh Create(MediaProjection projection, float radius)
        {
            const int columns = 64, rows = 32;
            var vertices = new Vector3[(columns + 1) * (rows + 1)];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[columns * rows * 6];
            float longitudeRange = projection == MediaProjection.Dome180 ? Mathf.PI : 2f * Mathf.PI;
            for (int row = 0; row <= rows; row++)
            {
                float v = (float)row / rows;
                float latitude = (v - 0.5f) * Mathf.PI;
                for (int col = 0; col <= columns; col++)
                {
                    float u = (float)col / columns;
                    float longitude = (u - 0.5f) * longitudeRange;
                    var direction = new Vector3(Mathf.Sin(longitude) * Mathf.Cos(latitude),
                        Mathf.Sin(latitude), Mathf.Cos(longitude) * Mathf.Cos(latitude));
                    int i = row * (columns + 1) + col;
                    vertices[i] = direction * radius;
                    normals[i] = -direction;
                    uv[i] = new Vector2(u, v);
                    if (row == rows || col == columns) continue;
                    int t = (row * columns + col) * 6;
                    int next = i + columns + 1;
                    triangles[t] = i; triangles[t + 1] = next; triangles[t + 2] = i + 1;
                    triangles[t + 3] = i + 1; triangles[t + 4] = next; triangles[t + 5] = next + 1;
                }
            }
            var mesh = new Mesh { name = $"{projection} (inward)" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
