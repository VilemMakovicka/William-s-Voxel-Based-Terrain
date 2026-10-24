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

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct MarchingCubesJob : IJob {
        [ReadOnly] public int3 chunkSize;
        [ReadOnly] public float voxelScale;
        [ReadOnly] public float isoLevel;
        [ReadOnly] public int lodStride; // 1 = LOD0, 2 = LOD1, 4 = LOD2
        [ReadOnly] public bool emitSkirts;
        [ReadOnly] public int maxLodStride;
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

        private float4 SampleColor(int x, int y, int z) {
            return colors[GetVoxelIndex(x + 1, y + 1, z + 1)];
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

        private float3 CalculateNormal(float3 localVoxelPos, int stride) {
            float h = 0.5f * stride;

            float dx = SampleDensityTrilinear(localVoxelPos + new float3(h, 0, 0)) - SampleDensityTrilinear(localVoxelPos - new float3(h, 0, 0));
            float dy = SampleDensityTrilinear(localVoxelPos + new float3(0, h, 0)) - SampleDensityTrilinear(localVoxelPos - new float3(0, h, 0));
            float dz = SampleDensityTrilinear(localVoxelPos + new float3(0, 0, h)) - SampleDensityTrilinear(localVoxelPos - new float3(0, 0, h));

            float3 n = new float3(-dx, -dy, -dz);
            float lenSq = math.lengthsq(n);

            return lenSq > 1e-6f ? math.normalize(n) : new float3(0f, 1f, 0f);
        }

        private float3 InterpolatePosition(float3 p1, float3 p2, float v1, float v2, out float t) {
            if (math.abs(isoLevel - v1) < 1e-5f) { t = 0f; return p1; }
            if (math.abs(isoLevel - v2) < 1e-5f) { t = 1f; return p2; }
            if (math.abs(v1 - v2) < 1e-5f) { t = 0f; return p1; }
            t = math.clamp((isoLevel - v1) / (v2 - v1), 0f, 1f);
            return math.lerp(p1, p2, t);
        }

        public unsafe void Execute() {
            float3* cornerPositions = stackalloc float3[8];
            float* cornerValues = stackalloc float[8];
            float4* cornerColors = stackalloc float4[8];

            float3* edgeVertices = stackalloc float3[12];
            float3* edgeNormals = stackalloc float3[12];
            float4* edgeColors = stackalloc float4[12];

            // -------------------------------------------------------------
            // 1. PRIMARY MARCHING CUBES VOLUME PASS
            // -------------------------------------------------------------
            for (int x = 0; x < chunkSize.x; x += lodStride) {
                for (int y = 0; y < chunkSize.y; y += lodStride) {
                    for (int z = 0; z < chunkSize.z; z += lodStride) {
                        cornerPositions[0] = new float3(x, y, z);
                        cornerPositions[1] = new float3(x + lodStride, y, z);
                        cornerPositions[2] = new float3(x + lodStride, y, z + lodStride);
                        cornerPositions[3] = new float3(x, y, z + lodStride);
                        cornerPositions[4] = new float3(x, y + lodStride, z);
                        cornerPositions[5] = new float3(x + lodStride, y + lodStride, z);
                        cornerPositions[6] = new float3(x + lodStride, y + lodStride, z + lodStride);
                        cornerPositions[7] = new float3(x, y + lodStride, z + lodStride);

                        cornerValues[0] = SampleDensityTrilinear(new float3(x, y, z));
                        cornerValues[1] = SampleDensityTrilinear(new float3(x + lodStride, y, z));
                        cornerValues[2] = SampleDensityTrilinear(new float3(x + lodStride, y, z + lodStride));
                        cornerValues[3] = SampleDensityTrilinear(new float3(x, y, z + lodStride));
                        cornerValues[4] = SampleDensityTrilinear(new float3(x, y + lodStride, z));
                        cornerValues[5] = SampleDensityTrilinear(new float3(x + lodStride, y + lodStride, z));
                        cornerValues[6] = SampleDensityTrilinear(new float3(x + lodStride, y + lodStride, z + lodStride));
                        cornerValues[7] = SampleDensityTrilinear(new float3(x, y + lodStride, z + lodStride));

                        cornerColors[0] = SampleColor(x, y, z);
                        cornerColors[1] = SampleColor(x + lodStride, y, z);
                        cornerColors[2] = SampleColor(x + lodStride, y, z + lodStride);
                        cornerColors[3] = SampleColor(x, y, z + lodStride);
                        cornerColors[4] = SampleColor(x, y + lodStride, z);
                        cornerColors[5] = SampleColor(x + lodStride, y + lodStride, z);
                        cornerColors[6] = SampleColor(x + lodStride, y + lodStride, z + lodStride);
                        cornerColors[7] = SampleColor(x, y + lodStride, z + lodStride);

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
                        if (edgeTable[cubeIndex] == 0) continue;

                        for (int i = 0; i < 12; i++) {
                            if ((edgeTable[cubeIndex] & (1 << i)) != 0) {
                                int a = edgeCorners[i].x;
                                int b = edgeCorners[i].y;
                                float t;
                                float3 p = InterpolatePosition(cornerPositions[a], cornerPositions[b], cornerValues[a], cornerValues[b], out t);

                                edgeVertices[i] = p * voxelScale;
                                edgeNormals[i] = CalculateNormal(p, lodStride);
                                edgeColors[i] = math.lerp(cornerColors[a], cornerColors[b], t);
                            }
                        }

                        int tableOffset = cubeIndex * 16;
                        for (int i = 0; triTable[tableOffset + i] != -1; i += 3) {
                            int e0 = triTable[tableOffset + i];
                            int e1 = triTable[tableOffset + i + 1];
                            int e2 = triTable[tableOffset + i + 2];

                            float3 p0 = edgeVertices[e0];
                            float3 p1 = edgeVertices[e1];
                            float3 p2 = edgeVertices[e2];

                            float3 n0 = edgeNormals[e0];
                            float3 n1 = edgeNormals[e1];
                            float3 n2 = edgeNormals[e2];

                            float3 geoNorm = math.normalize(math.cross(p1 - p2, p0 - p2));
                            if (math.dot(n0, geoNorm) < 0.1f) n0 = geoNorm;
                            if (math.dot(n1, geoNorm) < 0.1f) n1 = geoNorm;
                            if (math.dot(n2, geoNorm) < 0.1f) n2 = geoNorm;

                            TerrainVertex v0 = new TerrainVertex { position = p0, normal = n0, color = edgeColors[e0] };
                            TerrainVertex v1 = new TerrainVertex { position = p1, normal = n1, color = edgeColors[e1] };
                            TerrainVertex v2 = new TerrainVertex { position = p2, normal = n2, color = edgeColors[e2] };

                            int startIdx = outputVertices.Length;

                            outputVertices.Add(v2);
                            outputVertices.Add(v1);
                            outputVertices.Add(v0);

                            outputIndices.Add(startIdx);
                            outputIndices.Add(startIdx + 1);
                            outputIndices.Add(startIdx + 2);
                        }
                    }
                }
            }

            // -------------------------------------------------------------
            // 2. 2D WATERTIGHT BOUNDARY WALL CAPPING (LOD1 & LOD2 ONLY)
            // Evaluates all 6 faces of the chunk box: X, Z, and Y (vertical)!
            // -------------------------------------------------------------
            if (emitSkirts && lodStride > 1) {
                // -X Wall (x = 0) & +X Wall (x = max)
                CapBoundaryFace(0, 0, new float3(-1, 0, 0));
                CapBoundaryFace(chunkSize.x, 0, new float3(1, 0, 0));

                // -Z Wall (z = 0) & +Z Wall (z = max)
                CapBoundaryFace(0, 2, new float3(0, 0, -1));
                CapBoundaryFace(chunkSize.z, 2, new float3(0, 0, 1));

                // -Y Floor (y = 0) & +Y Ceiling (y = max) -> Fixes vertical holes!
                CapBoundaryFace(0, 1, new float3(0, -1, 0));
                CapBoundaryFace(chunkSize.y, 1, new float3(0, 1, 0));
            }
        }

        // Evaluates a 2D slice on any boundary plane:
        // fixedAxis: 0 = X plane, 1 = Y plane (horizontal floor/ceiling), 2 = Z plane
        private void CapBoundaryFace(int fixedCoord, int fixedAxis, float3 planeNormal) {
            int uMax, vMax;

            if (fixedAxis == 0) { // X plane: U = Z, V = Y
                uMax = chunkSize.z;
                vMax = chunkSize.y;
            }
            else if (fixedAxis == 1) { // Y plane: U = X, V = Z (HORIZONTAL CAP)
                uMax = chunkSize.x;
                vMax = chunkSize.z;
            }
            else { // Z plane: U = X, V = Y
                uMax = chunkSize.x;
                vMax = chunkSize.y;
            }

            for (int u = 0; u < uMax; u += lodStride) {
                for (int v = 0; v < vMax; v += lodStride) {
                    float3 p0, p1, p2, p3;

                    if (fixedAxis == 0) {
                        p0 = new float3(fixedCoord, v, u);
                        p1 = new float3(fixedCoord, v, u + lodStride);
                        p2 = new float3(fixedCoord, v + lodStride, u + lodStride);
                        p3 = new float3(fixedCoord, v + lodStride, u);
                    }
                    else if (fixedAxis == 1) { // Horizontal floor/ceiling slice
                        p0 = new float3(u, fixedCoord, v);
                        p1 = new float3(u + lodStride, fixedCoord, v);
                        p2 = new float3(u + lodStride, fixedCoord, v + lodStride);
                        p3 = new float3(u, fixedCoord, v + lodStride);
                    }
                    else {
                        p0 = new float3(u, v, fixedCoord);
                        p1 = new float3(u + lodStride, v, fixedCoord);
                        p2 = new float3(u + lodStride, v + lodStride, fixedCoord);
                        p3 = new float3(u, v + lodStride, fixedCoord);
                    }

                    float d0 = SampleDensityTrilinear(p0);
                    float d1 = SampleDensityTrilinear(p1);
                    float d2 = SampleDensityTrilinear(p2);
                    float d3 = SampleDensityTrilinear(p3);

                    int mask = 0;
                    if (d0 > isoLevel) mask |= 1;
                    if (d1 > isoLevel) mask |= 2;
                    if (d2 > isoLevel) mask |= 4;
                    if (d3 > isoLevel) mask |= 8;

                    if (mask == 0 || mask == 15) continue; // Pure air or pure deep solid

                    EmitMarchingSquaresCell(p0, p1, p2, p3, d0, d1, d2, d3, mask, planeNormal);
                }
            }
        }

        private void EmitMarchingSquaresCell(float3 p0, float3 p1, float3 p2, float3 p3,
                                             float d0, float d1, float d2, float d3,
                                             int mask, float3 planeNormal) {
            // Interpolate midpoints on edges
            float t;
            float3 e0 = InterpolatePosition(p0, p1, d0, d1, out t) * voxelScale; // Bottom edge
            float3 e1 = InterpolatePosition(p1, p2, d1, d2, out t) * voxelScale; // Right edge
            float3 e2 = InterpolatePosition(p3, p2, d3, d2, out t) * voxelScale; // Top edge
            float3 e3 = InterpolatePosition(p0, p3, d0, d3, out t) * voxelScale; // Left edge

            float3 c0 = p0 * voxelScale;
            float3 c1 = p1 * voxelScale;
            float3 c2 = p2 * voxelScale;
            float3 c3 = p3 * voxelScale;

            // Triangulate the 14 cutting cases of 2D Marching Squares
            switch (mask) {
                case 1: AddCapTriangle(c0, e0, e3, planeNormal); break;
                case 2: AddCapTriangle(c1, e1, e0, planeNormal); break;
                case 3: AddCapQuad(c0, c1, e1, e3, planeNormal); break;
                case 4: AddCapTriangle(c2, e2, e1, planeNormal); break;
                case 5: AddCapTriangle(c0, e0, e3, planeNormal); AddCapTriangle(c2, e2, e1, planeNormal); break;
                case 6: AddCapQuad(c1, c2, e2, e0, planeNormal); break;
                case 7: AddCapPolygon5(c0, c1, c2, e2, e3, planeNormal); break;
                case 8: AddCapTriangle(c3, e3, e2, planeNormal); break;
                case 9: AddCapQuad(c3, c0, e0, e2, planeNormal); break;
                case 10: AddCapTriangle(c1, e1, e0, planeNormal); AddCapTriangle(c3, e3, e2, planeNormal); break;
                case 11: AddCapPolygon5(c3, c0, c1, e1, e2, planeNormal); break;
                case 12: AddCapQuad(c2, c3, e3, e1, planeNormal); break;
                case 13: AddCapPolygon5(c2, c3, c0, e0, e1, planeNormal); break;
                case 14: AddCapPolygon5(c1, c2, c3, e3, e0, planeNormal); break;
            }
        }

        private void AddCapTriangle(float3 a, float3 b, float3 c, float3 planeNormal) {
            int idx = outputVertices.Length;
            float4 col = new float4(1f, 0f, 0f, 0f);

            outputVertices.Add(new TerrainVertex { position = a, normal = planeNormal, color = col });
            outputVertices.Add(new TerrainVertex { position = b, normal = planeNormal, color = col });
            outputVertices.Add(new TerrainVertex { position = c, normal = planeNormal, color = col });

            // Double-sided
            outputIndices.Add(idx); outputIndices.Add(idx + 1); outputIndices.Add(idx + 2);
            outputIndices.Add(idx); outputIndices.Add(idx + 2); outputIndices.Add(idx + 1);
        }

        private void AddCapQuad(float3 a, float3 b, float3 c, float3 d, float3 planeNormal) {
            AddCapTriangle(a, b, c, planeNormal);
            AddCapTriangle(a, c, d, planeNormal);
        }

        private void AddCapPolygon5(float3 a, float3 b, float3 c, float3 d, float3 e, float3 planeNormal) {
            AddCapTriangle(a, b, c, planeNormal);
            AddCapTriangle(a, c, d, planeNormal);
            AddCapTriangle(a, d, e, planeNormal);
        }
    } 
}