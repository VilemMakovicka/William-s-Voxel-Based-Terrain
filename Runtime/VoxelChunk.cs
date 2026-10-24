using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace vilemmakovicka.wvbterrain {
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class VoxelChunk : MonoBehaviour {
        private MeshFilter meshFilter;
        private MeshCollider meshCollider;
        private LODGroup lodGroup;

        public void ApplyMeshData(TerrainVertex[] vertices, int[] indices, bool generateLightmapUVs = false) {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshCollider == null) meshCollider = GetComponent<MeshCollider>();

            Mesh targetMesh = meshFilter.sharedMesh;
            if (targetMesh == null) {
                targetMesh = new Mesh { name = "ChunkMesh_" + gameObject.name };
                meshFilter.sharedMesh = targetMesh;
            }

            targetMesh.Clear();

            if (vertices.Length == 0) {
                meshCollider.sharedMesh = null;
                return;
            }

            if (vertices.Length > 65000)
                targetMesh.indexFormat = IndexFormat.UInt32;

            Vector3[] pos = new Vector3[vertices.Length];
            Vector3[] norms = new Vector3[vertices.Length];
            Color[] colors = new Color[vertices.Length];

            for (int i = 0; i < vertices.Length; i++) {
                pos[i] = vertices[i].position;
                norms[i] = vertices[i].normal;
                colors[i] = new Color(vertices[i].color.x, vertices[i].color.y, vertices[i].color.z, vertices[i].color.w);
            }

            targetMesh.vertices = pos;
            targetMesh.normals = norms;
            targetMesh.colors = colors;
            targetMesh.triangles = indices;
            targetMesh.RecalculateBounds();

#if UNITY_EDITOR
            if (generateLightmapUVs) {
                UnwrapParam unwrapSettings;
                UnwrapParam.SetDefaults(out unwrapSettings);
                unwrapSettings.packMargin = 0.04f;
                unwrapSettings.angleError = 8.0f;
                unwrapSettings.areaError = 15.0f;
                Unwrapping.GenerateSecondaryUVSet(targetMesh, unwrapSettings);
            }
#endif

            meshFilter.sharedMesh = targetMesh;
            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = targetMesh; // Collider always uses LOD0
        }

        /// <summary>
        /// Restores base chunk rendering for live sculpting and cleans up LOD child renderers.
        /// </summary>
        public void ResetToLiveEditing() {
            // Re-enable base chunk renderer for sculpting visibility
            MeshRenderer baseRenderer = GetComponent<MeshRenderer>();
            if (baseRenderer != null) {
                baseRenderer.enabled = true;
            }

            // Remove or disable LODGroup so it doesn't fight live editing
            LODGroup group = GetComponent<LODGroup>();
            if (group != null) {
                if (Application.isPlaying) Destroy(group);
                else DestroyImmediate(group);
            }

            // Remove LOD child containers if present
            string[] lodNames = { "LOD0", "LOD1", "LOD2" };
            foreach (var name in lodNames) {
                Transform child = transform.Find(name);
                if (child != null) {
                    if (Application.isPlaying) Destroy(child.gameObject);
                    else DestroyImmediate(child.gameObject);
                }
            }
        }

        /// <summary>
        /// Configures Unity's native LODGroup across LOD0, LOD1, and LOD2 child meshes.
        /// </summary>
        public void SetupLODGroup(Mesh lod0, Mesh lod1, Mesh lod2, Material mat) {
            if (lodGroup == null) lodGroup = gameObject.GetComponent<LODGroup>();
            if (lodGroup == null) lodGroup = gameObject.AddComponent<LODGroup>();

            // Create or configure LOD children
            MeshRenderer r0 = GetOrCreateLODChild("LOD0", lod0, mat);
            MeshRenderer r1 = GetOrCreateLODChild("LOD1", lod1, mat);
            MeshRenderer r2 = GetOrCreateLODChild("LOD2", lod2, mat);

            // Turn off base renderer since children handle display
            GetComponent<MeshRenderer>().enabled = false;

            LOD[] lods = new LOD[3];
            lods[0] = new LOD(0.50f, new Renderer[] { r0 }); // Transitions to LOD1 at 50% screen height
            lods[1] = new LOD(0.20f, new Renderer[] { r1 }); // Transitions to LOD2 at 20% screen height
            lods[2] = new LOD(0.04f, new Renderer[] { r2 }); // Culled below 4% screen height

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
        }

        private MeshRenderer GetOrCreateLODChild(string childName, Mesh m, Material mat) {
            Transform child = transform.Find(childName);
            if (child == null) {
                GameObject go = new GameObject(childName);
                go.transform.parent = transform;
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                child = go.transform;
            }

            // Explicit check using Unity's overloaded == operator
            MeshFilter mf = child.GetComponent<MeshFilter>();
            if (mf == null) {
                mf = child.gameObject.AddComponent<MeshFilter>();
            }

            MeshRenderer mr = child.GetComponent<MeshRenderer>();
            if (mr == null) {
                mr = child.gameObject.AddComponent<MeshRenderer>();
            }

            mf.sharedMesh = m;
            mr.sharedMaterial = mat;

#if UNITY_EDITOR
            // Ensure child renderers are marked static for lightmapping
            UnityEditor.GameObjectUtility.SetStaticEditorFlags(child.gameObject, UnityEditor.StaticEditorFlags.ContributeGI);

            // Optimize lightmap resolution across LOD steps
            SerializedObject so = new SerializedObject(mr);
            SerializedProperty scaleProp = so.FindProperty("m_ScaleInLightmap");
            if (scaleProp != null) {
                if (childName == "LOD0") scaleProp.floatValue = 1.0f;
                else if (childName == "LOD1") scaleProp.floatValue = 0.5f;
                else if (childName == "LOD2") scaleProp.floatValue = 0.25f;
                so.ApplyModifiedProperties();
            }
#endif

            return mr;
        }
    } 
}