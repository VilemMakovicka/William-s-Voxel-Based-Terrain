using System.Collections.Generic;
using Unity.Mathematics;

namespace vilemmakovicka.wvbterrain {
    /// <summary>
    /// Boundary preserving mesh simplification used to build the lower LODs of a chunk.
    ///
    /// It takes the welded LOD0 mesh and removes vertices with quadric error metric edge collapses.
    /// Every collapse keeps one of the two original vertices (no new positions, no moved vertices),
    /// so all surviving vertices still lie exactly on the iso surface.
    ///
    /// Vertices on an open border of the mesh (= the cut where the surface meets a chunk face)
    /// are never removed or moved. Because those border vertices are computed from the shared density
    /// padding, they are the same in the neighbouring chunk's LOD0, LOD1 and LOD2 mesh, so any LOD
    /// connects to any other LOD without cracks and without skirts.
    /// </summary>
    public static class MeshDecimator {
        private struct Quadric {
            public double a2, ab, ac, ad, b2, bc, bd, c2, cd, d2;

            public static Quadric FromPlane(double a, double b, double c, double d, double weight) {
                return new Quadric {
                    a2 = a * a * weight, ab = a * b * weight, ac = a * c * weight, ad = a * d * weight,
                    b2 = b * b * weight, bc = b * c * weight, bd = b * d * weight,
                    c2 = c * c * weight, cd = c * d * weight,
                    d2 = d * d * weight
                };
            }

            public static Quadric operator +(Quadric l, Quadric r) {
                return new Quadric {
                    a2 = l.a2 + r.a2, ab = l.ab + r.ab, ac = l.ac + r.ac, ad = l.ad + r.ad,
                    b2 = l.b2 + r.b2, bc = l.bc + r.bc, bd = l.bd + r.bd,
                    c2 = l.c2 + r.c2, cd = l.cd + r.cd,
                    d2 = l.d2 + r.d2
                };
            }

            public double Error(double3 p) {
                return a2 * p.x * p.x + 2 * ab * p.x * p.y + 2 * ac * p.x * p.z + 2 * ad * p.x
                     + b2 * p.y * p.y + 2 * bc * p.y * p.z + 2 * bd * p.y
                     + c2 * p.z * p.z + 2 * cd * p.z
                     + d2;
            }
        }

        private struct HeapEntry {
            public double cost;
            public int a, b;      // edge endpoints
            public int va, vb;    // vertex versions when the entry was created (stale check)
        }

        /// <summary>Minimal binary min-heap (System.Collections.Generic.PriorityQueue is not available in Unity).</summary>
        private sealed class Heap {
            private readonly List<HeapEntry> items = new List<HeapEntry>();
            public int Count => items.Count;

            private static bool Less(HeapEntry x, HeapEntry y) {
                if (x.cost != y.cost) return x.cost < y.cost;
                if (x.a != y.a) return x.a < y.a;
                return x.b < y.b;
            }

            public void Push(HeapEntry e) {
                items.Add(e);
                int i = items.Count - 1;
                while (i > 0) {
                    int parent = (i - 1) / 2;
                    if (!Less(items[i], items[parent])) break;
                    HeapEntry tmp = items[i]; items[i] = items[parent]; items[parent] = tmp;
                    i = parent;
                }
            }

            public HeapEntry Pop() {
                HeapEntry top = items[0];
                int last = items.Count - 1;
                items[0] = items[last];
                items.RemoveAt(last);
                int i = 0;
                int n = items.Count;
                while (true) {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < n && Less(items[l], items[m])) m = l;
                    if (r < n && Less(items[r], items[m])) m = r;
                    if (m == i) break;
                    HeapEntry tmp = items[i]; items[i] = items[m]; items[m] = tmp;
                    i = m;
                }
                return top;
            }
        }

        private sealed class State {
            public TerrainVertex[] verts;
            public int[] tris;                 // 3 indices per triangle
            public bool[] triAlive;
            public List<int>[] vertTris;       // triangles around each vertex
            public Quadric[] quadrics;
            public bool[] locked;              // vertices on an open border: never removed
            public bool[] vertAlive;
            public int[] version;
            public Heap heap = new Heap();
            public float minNormalDot;

            public readonly HashSet<int> setA = new HashSet<int>();
            public readonly HashSet<int> setB = new HashSet<int>();
            public readonly List<int> tmp = new List<int>();
        }

        private static long EdgeKey(int a, int b) {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        /// <summary>
        /// Simplifies a welded indexed triangle mesh.
        /// </summary>
        /// <param name="targetRatio">Wanted triangle count relative to the input (0.5 = half). It can end up higher when
        /// the locked border or the flip checks do not allow more collapses.</param>
        /// <param name="minNormalDot">A collapse is rejected when it would turn a neighbouring triangle by more than
        /// acos(minNormalDot). Higher = safer shapes, fewer collapses.</param>
        public static void Decimate(TerrainVertex[] vertices, int[] indices, float targetRatio,
                                    out TerrainVertex[] outVertices, out int[] outIndices, float minNormalDot = 0.2f) {
            int triCount = indices.Length / 3;
            if (targetRatio >= 0.999f || triCount < 8) {
                outVertices = (TerrainVertex[])vertices.Clone();
                outIndices = (int[])indices.Clone();
                return;
            }

            State s = new State {
                verts = (TerrainVertex[])vertices.Clone(),
                tris = (int[])indices.Clone(),
                triAlive = new bool[triCount],
                vertTris = new List<int>[vertices.Length],
                quadrics = new Quadric[vertices.Length],
                locked = new bool[vertices.Length],
                vertAlive = new bool[vertices.Length],
                version = new int[vertices.Length],
                minNormalDot = minNormalDot
            };

            for (int v = 0; v < vertices.Length; v++) {
                s.vertTris[v] = new List<int>(6);
                s.vertAlive[v] = true;
            }

            // --- adjacency, edge face counts ---
            Dictionary<long, int> edgeFaces = new Dictionary<long, int>(triCount * 2);
            for (int t = 0; t < triCount; t++) {
                s.triAlive[t] = true;
                for (int k = 0; k < 3; k++) {
                    int a = s.tris[t * 3 + k];
                    int b = s.tris[t * 3 + (k + 1) % 3];
                    s.vertTris[a].Add(t);
                    long key = EdgeKey(a, b);
                    int count;
                    edgeFaces.TryGetValue(key, out count);
                    edgeFaces[key] = count + 1;
                }
            }

            // An edge that does not have exactly two faces is an open border (chunk face cut) or a defect:
            // both end vertices are locked and never touched.
            foreach (KeyValuePair<long, int> kv in edgeFaces) {
                if (kv.Value != 2) {
                    s.locked[(int)(kv.Key >> 32)] = true;
                    s.locked[(int)(kv.Key & 0xffffffffL)] = true;
                }
            }

            // --- quadrics: sum of (area weighted) squared distances to the planes of the adjacent triangles ---
            for (int t = 0; t < triCount; t++) {
                double3 p0 = s.verts[s.tris[t * 3]].position;
                double3 p1 = s.verts[s.tris[t * 3 + 1]].position;
                double3 p2 = s.verts[s.tris[t * 3 + 2]].position;
                double3 n = math.cross(p1 - p0, p2 - p0);
                double len = math.length(n);
                if (len < 1e-12) continue; // degenerate triangle has no plane
                n /= len;
                Quadric q = Quadric.FromPlane(n.x, n.y, n.z, -math.dot(n, p0), 0.5 * len);
                for (int k = 0; k < 3; k++) {
                    int v = s.tris[t * 3 + k];
                    s.quadrics[v] = s.quadrics[v] + q;
                }
            }

            foreach (KeyValuePair<long, int> kv in edgeFaces) {
                PushEdge(s, (int)(kv.Key >> 32), (int)(kv.Key & 0xffffffffL));
            }

            int aliveTris = triCount;
            int target = math.max(4, (int)(triCount * targetRatio));

            while (aliveTris > target && s.heap.Count > 0) {
                HeapEntry e = s.heap.Pop();
                if (!s.vertAlive[e.a] || !s.vertAlive[e.b]) continue;
                if (s.version[e.a] != e.va || s.version[e.b] != e.vb) continue; // stale

                double cost;
                int keep;
                if (!TryCost(s, e.a, e.b, out cost, out keep)) continue;
                int remove = keep == e.a ? e.b : e.a;

                if (!CanCollapse(s, remove, keep)) continue;

                // ---- collapse: 'remove' is merged into 'keep' ----
                s.tmp.Clear();
                s.tmp.AddRange(s.vertTris[remove]);
                for (int i = 0; i < s.tmp.Count; i++) {
                    int t = s.tmp[i];
                    int i0 = s.tris[t * 3], i1 = s.tris[t * 3 + 1], i2 = s.tris[t * 3 + 2];
                    if (i0 == keep || i1 == keep || i2 == keep) {
                        // triangle collapses to an edge
                        s.triAlive[t] = false;
                        aliveTris--;
                        s.vertTris[i0].Remove(t);
                        s.vertTris[i1].Remove(t);
                        s.vertTris[i2].Remove(t);
                    }
                    else {
                        for (int k = 0; k < 3; k++) {
                            if (s.tris[t * 3 + k] == remove) s.tris[t * 3 + k] = keep;
                        }
                        s.vertTris[keep].Add(t);
                    }
                }

                s.vertTris[remove].Clear();
                s.vertAlive[remove] = false;
                s.quadrics[keep] = s.quadrics[keep] + s.quadrics[remove];
                s.version[keep]++;

                CollectNeighbors(s, keep, s.setA);
                foreach (int w in s.setA) PushEdge(s, keep, w);
            }

            // --- compact ---
            int[] remap = new int[vertices.Length];
            for (int v = 0; v < remap.Length; v++) remap[v] = -1;

            List<int> outIdx = new List<int>(aliveTris * 3);
            List<TerrainVertex> outVerts = new List<TerrainVertex>();
            for (int t = 0; t < triCount; t++) {
                if (!s.triAlive[t]) continue;
                for (int k = 0; k < 3; k++) {
                    int v = s.tris[t * 3 + k];
                    if (remap[v] < 0) {
                        remap[v] = outVerts.Count;
                        outVerts.Add(s.verts[v]);
                    }
                    outIdx.Add(remap[v]);
                }
            }

            outVertices = outVerts.ToArray();
            outIndices = outIdx.ToArray();
        }

        private static void PushEdge(State s, int u, int v) {
            double cost;
            int keep;
            if (!TryCost(s, u, v, out cost, out keep)) return;
            s.heap.Push(new HeapEntry { cost = cost, a = u, b = v, va = s.version[u], vb = s.version[v] });
        }

        /// <summary>Cost of collapsing the edge and which of the two vertices survives.</summary>
        private static bool TryCost(State s, int u, int v, out double cost, out int keep) {
            cost = 0;
            keep = -1;
            if (s.locked[u] && s.locked[v]) return false; // two border vertices: never

            Quadric q = s.quadrics[u] + s.quadrics[v];
            double3 pu = s.verts[u].position;
            double3 pv = s.verts[v].position;
            double tieBreak = math.lengthsq(pu - pv) * 1e-6; // prefers short edges on flat areas

            if (s.locked[u]) { keep = u; cost = q.Error(pu) + tieBreak; return true; }
            if (s.locked[v]) { keep = v; cost = q.Error(pv) + tieBreak; return true; }

            double eu = q.Error(pu);
            double ev = q.Error(pv);
            if (eu <= ev) { keep = u; cost = eu + tieBreak; }
            else { keep = v; cost = ev + tieBreak; }
            return true;
        }

        private static void CollectNeighbors(State s, int u, HashSet<int> set) {
            set.Clear();
            List<int> list = s.vertTris[u];
            for (int i = 0; i < list.Count; i++) {
                int t = list[i];
                for (int k = 0; k < 3; k++) {
                    int w = s.tris[t * 3 + k];
                    if (w != u) set.Add(w);
                }
            }
        }

        private static double3 TriNormal(State s, int a, int b, int c) {
            double3 pa = s.verts[a].position;
            return math.cross((double3)s.verts[b].position - pa, (double3)s.verts[c].position - pa);
        }

        /// <summary>Topology (link condition) and shape (no flipped triangles) checks.</summary>
        private static bool CanCollapse(State s, int remove, int keep) {
            // Triangles that contain both vertices disappear: a manifold edge has 1 (border) or 2 of them.
            int shared = 0;
            List<int> list = s.vertTris[remove];
            for (int i = 0; i < list.Count; i++) {
                int t = list[i];
                if (s.tris[t * 3] == keep || s.tris[t * 3 + 1] == keep || s.tris[t * 3 + 2] == keep) shared++;
            }
            if (shared < 1 || shared > 2) return false;

            // Link condition: the only common neighbours are the opposite corners of those triangles,
            // otherwise the collapse would pinch the surface or fuse two sheets.
            CollectNeighbors(s, remove, s.setA);
            CollectNeighbors(s, keep, s.setB);
            s.setA.IntersectWith(s.setB);
            if (s.setA.Count != shared) return false;

            // Moving 'remove' onto 'keep' must not fold or flatten any remaining neighbouring triangle.
            for (int i = 0; i < list.Count; i++) {
                int t = list[i];
                int i0 = s.tris[t * 3], i1 = s.tris[t * 3 + 1], i2 = s.tris[t * 3 + 2];
                if (i0 == keep || i1 == keep || i2 == keep) continue;

                double3 oldN = TriNormal(s, i0, i1, i2);
                double3 newN = TriNormal(s, i0 == remove ? keep : i0, i1 == remove ? keep : i1, i2 == remove ? keep : i2);
                double lo = math.length(oldN);
                double ln = math.length(newN);
                if (ln < 1e-12) return false;
                if (lo > 1e-12 && math.dot(oldN, newN) < s.minNormalDot * lo * ln) return false;
            }
            return true;
        }
    }
}
