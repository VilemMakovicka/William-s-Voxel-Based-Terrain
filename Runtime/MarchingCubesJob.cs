using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace vilemmakovicka.wvbterrain {
    public struct TerrainVertex {
        public float3 position;
        public float3 normal;
        public float4 color;
    }

    /// <summary>
    /// Builds the full resolution (LOD0) mesh of one chunk with marching cubes.
    ///
    /// The output is an INDEXED mesh in voxel units (multiply by voxelScale afterwards):
    ///  - every surface vertex lives on exactly one grid edge and is created once and shared by all
    ///    triangles around it (welded),
    ///  - every grid edge is always interpolated from its lower corner to its upper corner, so two
    ///    neighbouring chunks compute bit-identical positions for the vertices on their shared face.
    ///
    /// Lower LODs are NOT made here, they are made by <see cref="MeshDecimator"/> from this mesh.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct MarchingCubesJob : IJob {
        [ReadOnly] public int3 chunkSize;
        [ReadOnly] public float isoLevel;
        [ReadOnly] public NativeArray<float> densities;
        [ReadOnly] public NativeArray<float4> colors;
        [ReadOnly] public NativeArray<int> triTable;
        [ReadOnly] public NativeArray<int> edgeTable;
        [ReadOnly] public NativeArray<int2> edgeCorners;

        public NativeList<TerrainVertex> outputVertices;
        public NativeList<int> outputIndices;

        private int GetVoxelIndex(int x, int y, int z) {
            int sx = chunkSize.x + 3;
            int sy = chunkSize.y + 3;
            return x + y * sx + z * sx * sy;
        }

        private float DensityAt(int x, int y, int z) {
            return densities[GetVoxelIndex(x + 1, y + 1, z + 1)];
        }

        private float4 SampleColor(int x, int y, int z) {
            return colors[GetVoxelIndex(x + 1, y + 1, z + 1)];
        }

        private static int3 CornerOffset(int corner) {
            switch (corner) {
                case 0: return new int3(0, 0, 0);
                case 1: return new int3(1, 0, 0);
                case 2: return new int3(1, 0, 1);
                case 3: return new int3(0, 0, 1);
                case 4: return new int3(0, 1, 0);
                case 5: return new int3(1, 1, 0);
                case 6: return new int3(1, 1, 1);
                default: return new int3(0, 1, 1);
            }
        }

        private float SampleDensityTrilinear(float3 p) {
            float px = p.x + 1f;
            float py = p.y + 1f;
            float pz = p.z + 1f;

            int sx = chunkSize.x + 3;
            int sy = chunkSize.y + 3;
            int sz = chunkSize.z + 3;

            int x0 = math.clamp((int)math.floor(px), 0, sx - 2);
            int y0 = math.clamp((int)math.floor(py), 0, sy - 2);
            int z0 = math.clamp((int)math.floor(pz), 0, sz - 2);
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            float fx = math.saturate(px - x0);
            float fy = math.saturate(py - y0);
            float fz = math.saturate(pz - z0);

            float c000 = densities[x0 + y0 * sx + z0 * sx * sy];
            float c100 = densities[x1 + y0 * sx + z0 * sx * sy];
            float c010 = densities[x0 + y1 * sx + z0 * sx * sy];
            float c110 = densities[x1 + y1 * sx + z0 * sx * sy];
            float c001 = densities[x0 + y0 * sx + z1 * sx * sy];
            float c101 = densities[x1 + y0 * sx + z1 * sx * sy];
            float c011 = densities[x0 + y1 * sx + z1 * sx * sy];
            float c111 = densities[x1 + y1 * sx + z1 * sx * sy];

            float c00 = math.lerp(c000, c100, fx);
            float c10 = math.lerp(c010, c110, fx);
            float c01 = math.lerp(c001, c101, fx);
            float c11 = math.lerp(c011, c111, fx);

            float c0 = math.lerp(c00, c10, fy);
            float c1 = math.lerp(c01, c11, fy);

            return math.lerp(c0, c1, fz);
        }

        /// <summary>Density gradient normal. Uses the padding layer, so it is identical in neighbouring chunks.</summary>
        private float3 CalculateNormal(float3 localVoxelPos) {
            const float h = 0.5f;

            float dx = SampleDensityTrilinear(localVoxelPos + new float3(h, 0, 0)) - SampleDensityTrilinear(localVoxelPos - new float3(h, 0, 0));
            float dy = SampleDensityTrilinear(localVoxelPos + new float3(0, h, 0)) - SampleDensityTrilinear(localVoxelPos - new float3(0, h, 0));
            float dz = SampleDensityTrilinear(localVoxelPos + new float3(0, 0, h)) - SampleDensityTrilinear(localVoxelPos - new float3(0, 0, h));

            float3 n = new float3(-dx, -dy, -dz);
            float lenSq = math.lengthsq(n);

            return lenSq > 1e-6f ? math.normalize(n) : new float3(0f, 1f, 0f);
        }

        /// <summary>Where along lowerValue -> upperValue the iso surface is (0..1).</summary>
        private float InterpolateT(float lowerValue, float upperValue) {
            if (math.abs(isoLevel - lowerValue) < 1e-5f) return 0f;
            if (math.abs(isoLevel - upperValue) < 1e-5f) return 1f;
            if (math.abs(lowerValue - upperValue) < 1e-5f) return 0f;
            return math.clamp((isoLevel - lowerValue) / (upperValue - lowerValue), 0f, 1f);
        }

        public unsafe void Execute() {
            float* cornerValues = stackalloc float[8];
            int* edgeVertexIndex = stackalloc int[12];

            // One slot per grid edge: (lower corner xyz, axis) -> index of the vertex on that edge (-1 = not created yet)
            int ex = chunkSize.x + 1;
            int ey = chunkSize.y + 1;
            int ez = chunkSize.z + 1;
            NativeArray<int> edgeCache = new NativeArray<int>(ex * ey * ez * 3, Allocator.Temp);
            for (int i = 0; i < edgeCache.Length; i++) edgeCache[i] = -1;

            for (int x = 0; x < chunkSize.x; x++) {
                for (int y = 0; y < chunkSize.y; y++) {
                    for (int z = 0; z < chunkSize.z; z++) {
                        for (int c = 0; c < 8; c++) {
                            int3 o = CornerOffset(c);
                            cornerValues[c] = DensityAt(x + o.x, y + o.y, z + o.z);
                        }

                        int cubeIndex = 0;
                        if (cornerValues[0] > isoLevel) cubeIndex |= 1;
                        if (cornerValues[1] > isoLevel) cubeIndex |= 2;
                        if (cornerValues[3] > isoLevel) cubeIndex |= 4;
                        if (cornerValues[2] > isoLevel) cubeIndex |= 8;
                        if (cornerValues[4] > isoLevel) cubeIndex |= 16;
                        if (cornerValues[5] > isoLevel) cubeIndex |= 32;
                        if (cornerValues[7] > isoLevel) cubeIndex |= 64;
                        if (cornerValues[6] > isoLevel) cubeIndex |= 128;

                        if (cubeIndex == 0 || cubeIndex == 255) continue;
                        int edgeMask = edgeTable[cubeIndex];
                        if (edgeMask == 0) continue;

                        for (int i = 0; i < 12; i++) {
                            if ((edgeMask & (1 << i)) == 0) continue;

                            int ia = edgeCorners[i].x;
                            int ib = edgeCorners[i].y;
                            int3 oa = CornerOffset(ia);
                            int3 ob = CornerOffset(ib);

                            // Canonical direction: always lower corner -> upper corner
                            if (oa.x + oa.y + oa.z > ob.x + ob.y + ob.z) {
                                int ti = ia; ia = ib; ib = ti;
                                int3 to = oa; oa = ob; ob = to;
                            }

                            int3 dir = ob - oa;
                            int axis = dir.x != 0 ? 0 : (dir.y != 0 ? 1 : 2);
                            int3 lower = new int3(x, y, z) + oa;
                            int3 upper = lower + dir;

                            int key = ((lower.z * ey + lower.y) * ex + lower.x) * 3 + axis;
                            int vertexIndex = edgeCache[key];

                            if (vertexIndex < 0) {
                                float t = InterpolateT(cornerValues[ia], cornerValues[ib]);
                                float3 p = new float3(lower.x, lower.y, lower.z) + new float3(dir.x, dir.y, dir.z) * t;

                                vertexIndex = outputVertices.Length;
                                outputVertices.Add(new TerrainVertex {
                                    position = p, // voxel units
                                    normal = CalculateNormal(p),
                                    color = math.lerp(SampleColor(lower.x, lower.y, lower.z), SampleColor(upper.x, upper.y, upper.z), t)
                                });
                                edgeCache[key] = vertexIndex;
                            }

                            edgeVertexIndex[i] = vertexIndex;
                        }

                        int tableOffset = cubeIndex * 16;
                        for (int i = 0; triTable[tableOffset + i] != -1; i += 3) {
                            int e0 = triTable[tableOffset + i];
                            int e1 = triTable[tableOffset + i + 1];
                            int e2 = triTable[tableOffset + i + 2];

                            // Same winding as before (e2, e1, e0)
                            outputIndices.Add(edgeVertexIndex[e2]);
                            outputIndices.Add(edgeVertexIndex[e1]);
                            outputIndices.Add(edgeVertexIndex[e0]);
                        }
                    }
                }
            }

            edgeCache.Dispose();
        }
    }
}
