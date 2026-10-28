using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace vilemmakovicka.wvbterrain {
    /// <summary>
    /// Component that handles one chunk of the terrain
    /// </summary>
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

            if (vertices.Length > 65000) {
                targetMesh.indexFormat = IndexFormat.UInt32;
            }

            Vector3[] vertexPositions = new Vector3[vertices.Length];
            Vector3[] vertexNormals = new Vector3[vertices.Length];
            Color[]   vertexColors = new Color[vertices.Length];

            for (int i = 0; i < vertices.Length; i++) {
                vertexPositions[i] = vertices[i].position;
                vertexNormals[i] = vertices[i].normal;
                vertexColors[i] = new Color(vertices[i].color.x, vertices[i].color.y, vertices[i].color.z, vertices[i].color.w);
            }

            targetMesh.vertices = vertexPositions;
            targetMesh.normals = vertexNormals;
            targetMesh.colors = vertexColors;
            targetMesh.triangles = indices;

            targetMesh.RecalculateBounds();

#if UNITY_EDITOR
            if (generateLightmapUVs) { // Unwarp lightmap UVs
                UnwrapParam.SetDefaults(out UnwrapParam unwrapSettings);
                unwrapSettings.packMargin = 0.04f;
                unwrapSettings.angleError = 8.0f;
                unwrapSettings.areaError  = 15.0f;
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
        /// Updates LOD group's meshes and material
        /// </summary>
        public void SetupLODGroup(Mesh lod0mesh, Mesh lod1mesh, Mesh lod2mesh, Material material) {
            if (lodGroup == null) lodGroup = gameObject.GetComponent<LODGroup>();
            if (lodGroup == null) lodGroup = gameObject.AddComponent<LODGroup>();

            // Create or configure LOD children
            MeshRenderer lod0 = GetOrCreateLODChild("LOD0", lod0mesh, material);
            MeshRenderer lod1 = GetOrCreateLODChild("LOD1", lod1mesh, material);
            MeshRenderer lod2 = GetOrCreateLODChild("LOD2", lod2mesh, material);

            // Turn off base renderer since children handle display
            GetComponent<MeshRenderer>().enabled = false;

            LOD[] lods = new LOD[3];
            lods[0] = new LOD(0.50f, new Renderer[] { lod0 }); // Transitions to LOD1 at 50% screen height
            lods[1] = new LOD(0.20f, new Renderer[] { lod1 }); // Transitions to LOD2 at 20% screen height
            lods[2] = new LOD(0.04f, new Renderer[] { lod2 }); // Culled below 4% screen height

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
        }

        private MeshRenderer GetOrCreateLODChild(string childName, Mesh mesh, Material material) {
            Transform child = transform.Find(childName); //Get child
            if (child == null) { //If child doesn't exist, create it.
                GameObject go = new GameObject(childName);
                go.transform.parent = transform;
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                child = go.transform;
            }

            //Get or create MeshFilter for this child
            MeshFilter childMeshFilter = child.GetComponent<MeshFilter>();
            if (childMeshFilter == null) {
                childMeshFilter = child.gameObject.AddComponent<MeshFilter>();
            }

            //Get or create MeshRenderer for this child
            MeshRenderer childMeshRenderer = child.GetComponent<MeshRenderer>();
            if (childMeshRenderer == null) {
                childMeshRenderer = child.gameObject.AddComponent<MeshRenderer>();
            }

            //Assign values for this LOD
            childMeshFilter.sharedMesh = mesh;
            childMeshRenderer.sharedMaterial = material;

#if UNITY_EDITOR
            // Ensure child renderers are marked static for lightmapping
            GameObjectUtility.SetStaticEditorFlags(child.gameObject, StaticEditorFlags.ContributeGI);

            // Lightmap optimization
            var childMeshRendererSerialized = new SerializedObject(childMeshRenderer);
            var scaleInLightmap = childMeshRendererSerialized.FindProperty("m_ScaleInLightmap");

            // If the child object exists, change it's scale in lightmaps based on its LOD level
            if (scaleInLightmap != null) {
                scaleInLightmap.floatValue = childName switch {
                    "LOD0" => 1.0f,
                    "LOD1" => 0.5f,
                    "LOD2" => 0.25f,
                    _ => 1.0f
                };
                childMeshRendererSerialized.ApplyModifiedProperties();
            }
#endif
            return childMeshRenderer;
        }
    } 
}