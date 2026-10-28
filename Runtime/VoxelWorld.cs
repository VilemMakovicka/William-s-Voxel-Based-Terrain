using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using System.IO;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace vilemmakovicka.wvbterrain {
    /// <summary>
    /// Component for the terrain object. 
    /// </summary>
    [ExecuteAlways]
    [SelectionBase]
    public class VoxelWorld : MonoBehaviour {

        public int chunksX = 4;
        public int chunksY = 2;
        public int chunksZ = 4;

        /// <summary>
        /// Size of each chunk in voxels. 
        /// </summary>
        public static readonly int3 ChunkSize = new int3(16, 16, 16);
        public static Vector3 ChunkSizeV3 => new Vector3(ChunkSize.x, ChunkSize.y, ChunkSize.z);
        /// <summary>
        /// Size of individual voxels of chunks.
        /// For example: If chunkSize is (16, 16, 16) and the voxelScale is 2, size of one chunk is (32m x 32m x 32m)
        /// </summary>
        public float voxelScale = 1.0f;

        public float isoLevel = 0.5f;

        public Material terrainMaterial;

        public float lod1TriangleRatio = 0.5f;
        public float lod2TriangleRatio = 0.2f;

        private readonly Dictionary<Vector3Int, VoxelChunk> chunks = new Dictionary<Vector3Int, VoxelChunk>();
        private float[,,] worldDensity;
        private Color[,,] worldColors;

        private int TotalVoxelsX => chunksX * ChunkSize.x + 1;
        private int TotalVoxelsY => chunksY * ChunkSize.y + 1;
        private int TotalVoxelsZ => chunksZ * ChunkSize.z + 1;

        private NativeArray<int> nativeTriTable;
        private NativeArray<int> nativeEdgeTable;
        private NativeArray<int2> nativeEdgeCorners;

        [SerializeField] private int uniqueTerrainID = 0;
        public TextAsset terrainDataAsset;

        [SerializeField] private bool isBaked = false;
        [System.NonSerialized] public bool isEditingMode = false;

        public bool IsBaked => isBaked;

        /// <summary>
        /// 
        /// </summary>
        public void LockAndBake() {
#if UNITY_EDITOR
            BakeMeshesForBuild();
            isBaked = true;
            isEditingMode = false;
            EditorUtility.SetDirty(this);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            Debug.Log("<color=cyan>[ Voxel terrain ]</color> Terrain locked and baked to static meshes.");
#endif
        }

#if UNITY_EDITOR
        public void BakeMeshesForBuild() {
            CreateChunkObjects();

            string folderPath = $"Assets/TerrainData/Meshes/{SaveFileName}";
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

            Material mat = GetValidMaterial();

            foreach (var pair in chunks) {
                Vector3Int coord = pair.Key;
                VoxelChunk chunk = pair.Value;

                // LOD0 is the full marching cubes mesh, LOD1/LOD2 are decimated versions of it that keep every
                // vertex on the chunk border, so all LOD combinations of neighbouring chunks line up (no skirts needed).
                Mesh m0 = GenerateChunkMesh(coord, 1f, generateLightmapUVs: true);
                Mesh m1 = GenerateChunkMesh(coord, lod1TriangleRatio, generateLightmapUVs: true);
                Mesh m2 = GenerateChunkMesh(coord, lod2TriangleRatio, generateLightmapUVs: true);

                // Collider mesh: LOD0 without lightmap UVs
                Mesh mCol = GenerateChunkMesh(coord, 1f, generateLightmapUVs: false);

                // Save .asset files
                SaveMeshAsset($"{folderPath}/{chunk.name}_LOD0.asset", m0);
                SaveMeshAsset($"{folderPath}/{chunk.name}_LOD1.asset", m1);
                SaveMeshAsset($"{folderPath}/{chunk.name}_LOD2.asset", m2);
                SaveMeshAsset($"{folderPath}/{chunk.name}_Collider.asset", mCol);

                // Configure Unity LODGroup component
                chunk.SetupLODGroup(m0, m1, m2, mat);

                // Skirt-free LOD0 on the base collider
                chunk.GetComponent<MeshCollider>().sharedMesh = mCol;
                EditorUtility.SetDirty(chunk.gameObject);
            }

            SaveToDiskInternal();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            isBaked = true;
            isEditingMode = false;

            EditorUtility.SetDirty(this);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);

            Debug.Log($"<color=cyan>[VoxelWorld]</color> Baked dynamic LODs (LOD0, LOD1, LOD2) to {folderPath}!");
#endif
        }

        private void SaveMeshAsset(string path, Mesh m) {
#if UNITY_EDITOR
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) {
                AssetDatabase.CreateAsset(m, path);
            }
            else {
                existing.Clear();
                EditorUtility.CopySerialized(m, existing);
            }
#endif
        }

        /// <summary>
        /// Internal save that writes the binary data WITHOUT calling AssetDatabase.Refresh()
        /// so it doesn't interrupt active mesh serialization.
        /// </summary>
        private void SaveToDiskInternal() {
#if UNITY_EDITOR
            if (worldDensity == null || worldColors == null) return;

            if (!Directory.Exists(SaveDirectory))
                Directory.CreateDirectory(SaveDirectory);

            using BinaryWriter writer = new BinaryWriter(File.Open(SaveFilePath, FileMode.Create));
            writer.Write(chunksX);
            writer.Write(chunksY);
            writer.Write(chunksZ);
            writer.Write(hasBakedLightmapUVs);

            int totalX = TotalVoxelsX;
            int totalY = TotalVoxelsY;
            int totalZ = TotalVoxelsZ;

            // Save all voxel's densities
            for (int x = 0; x < totalX; x++) {
                for (int y = 0; y < totalY; y++) {
                    for (int z = 0; z < totalZ; z++) {
                        writer.Write(worldDensity[x, y, z]);
                    }
                }
            }

            // Save all voxel's colors
            for (int x = 0; x < totalX; x++) {
                for (int y = 0; y < totalY; y++) {
                    for (int z = 0; z < totalZ; z++) {
                        Color c = worldColors[x, y, z];
                        writer.Write((byte)Mathf.RoundToInt(c.r * 255f));
                        writer.Write((byte)Mathf.RoundToInt(c.g * 255f));
                        writer.Write((byte)Mathf.RoundToInt(c.b * 255f));
                        writer.Write((byte)Mathf.RoundToInt(c.a * 255f));
                    }
                }
            }
#endif
        }

        public string SaveFileName {
            get {
                string currentSceneName = gameObject.scene.name;
                if (string.IsNullOrEmpty(currentSceneName)) {
                    currentSceneName = SceneManager.GetActiveScene().name;
                }
                if (string.IsNullOrEmpty(currentSceneName)) {
                    currentSceneName = "UntitledScene";
                }

                EnsureUniqueID();
                return $"{currentSceneName}_VoxelWorld_{uniqueTerrainID}";
            }
        }

        private string SaveDirectory => Path.Combine(Application.dataPath, "TerrainData");
        private string SaveFilePath => Path.Combine(SaveDirectory, $"{SaveFileName}.bytes");

        private void Reset() {
            EnsureUniqueID(forceNew: true);
        }

        public void EnsureUniqueID(bool forceNew = false) {
            if (uniqueTerrainID != 0 && !forceNew) return;

            VoxelWorld[] allWorlds = FindObjectsOfType<VoxelWorld>();
            int highestID = 0;

            foreach (var world in allWorlds) {
                if (world != this && world.uniqueTerrainID > highestID) {
                    highestID = world.uniqueTerrainID;
                }
            }

            uniqueTerrainID = highestID + 1;

#if UNITY_EDITOR
            if (!Application.isPlaying) {
                EditorUtility.SetDirty(this);
            }
#endif
        }

        public void UnlockForEditing() {
            isBaked = false;
            isEditingMode = true;

            InitializeNativeTables();
            LoadFromDisk();
            CreateChunkObjects();

            foreach (var pair in chunks) {
                // Clear old LOD structures if upgrading from an older version
                pair.Value.ResetToLiveEditing();

                MeshFilter mf = pair.Value.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) {
                    mf.sharedMesh = Instantiate(mf.sharedMesh);
                    MeshCollider mc = pair.Value.GetComponent<MeshCollider>();
                    if (mc != null) mc.sharedMesh = mf.sharedMesh;
                }
            }

            RebuildAllChunks();

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
            Debug.Log("<color=green>[VoxelWorld]</color> Terrain unlocked! Ready to sculpt.");
        }

#if UNITY_EDITOR
        public void SaveToDisk() {
            if (worldDensity == null || worldColors == null) return;

            if (!Directory.Exists(SaveDirectory))
                Directory.CreateDirectory(SaveDirectory);

            using (BinaryWriter writer = new BinaryWriter(File.Open(SaveFilePath, FileMode.Create))) {
                writer.Write(chunksX);
                writer.Write(chunksY);
                writer.Write(chunksZ);
                writer.Write(hasBakedLightmapUVs);

                int totalX = TotalVoxelsX;
                int totalY = TotalVoxelsY;
                int totalZ = TotalVoxelsZ;

                for (int x = 0; x < totalX; x++) {
                    for (int y = 0; y < totalY; y++) {
                        for (int z = 0; z < totalZ; z++) {
                            writer.Write(worldDensity[x, y, z]);
                        }
                    }
                }

                for (int x = 0; x < totalX; x++) {
                    for (int y = 0; y < totalY; y++) {
                        for (int z = 0; z < totalZ; z++) {
                            Color c = worldColors[x, y, z];
                            writer.Write((byte)Mathf.RoundToInt(c.r * 255f));
                            writer.Write((byte)Mathf.RoundToInt(c.g * 255f));
                            writer.Write((byte)Mathf.RoundToInt(c.b * 255f));
                            writer.Write((byte)Mathf.RoundToInt(c.a * 255f));
                        }
                    }
                }
            }

            AssetDatabase.Refresh();
            string relativePath = $"Assets/TerrainData/{SaveFileName}.bytes";
            terrainDataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(relativePath);
            EditorUtility.SetDirty(this);
            Debug.Log($"<color=green>[VoxelWorld]</color> Saved terrain to {SaveFilePath}");
        }
#endif

        public bool LoadFromDisk() {
            Stream dataStream = null;

            if (terrainDataAsset != null) {
                dataStream = new MemoryStream(terrainDataAsset.bytes);
            }
#if UNITY_EDITOR
            else if (File.Exists(SaveFilePath)) {
                dataStream = File.Open(SaveFilePath, FileMode.Open, FileAccess.Read);
            }
#endif

            if (dataStream == null) return false;

            try {
                using (BinaryReader reader = new BinaryReader(dataStream)) {
                    chunksX = reader.ReadInt32();
                    chunksY = reader.ReadInt32();
                    chunksZ = reader.ReadInt32();
                    hasBakedLightmapUVs = reader.ReadBoolean();

                    int totalX = TotalVoxelsX;
                    int totalY = TotalVoxelsY;
                    int totalZ = TotalVoxelsZ;

                    worldDensity = new float[totalX, totalY, totalZ];
                    worldColors = new Color[totalX, totalY, totalZ];

                    for (int x = 0; x < totalX; x++)
                        for (int y = 0; y < totalY; y++)
                            for (int z = 0; z < totalZ; z++)
                                worldDensity[x, y, z] = reader.ReadSingle();

                    for (int x = 0; x < totalX; x++)
                        for (int y = 0; y < totalY; y++)
                            for (int z = 0; z < totalZ; z++) {
                                float r = reader.ReadByte() / 255f;
                                float g = reader.ReadByte() / 255f;
                                float b = reader.ReadByte() / 255f;
                                float a = reader.ReadByte() / 255f;
                                worldColors[x, y, z] = new Color(r, g, b, a);
                            }
                }

                return true;
            }
            catch (System.Exception ex) {
                Debug.LogError($"[VoxelWorld] Error loading binary terrain: {ex.Message}");
                return false;
            }
            finally {
                dataStream?.Dispose();
            }
        }

        private void Awake() {
            if (isBaked) return;

            if (Application.isPlaying && HasExistingMeshes()) {
                return;
            }

            InitializeNativeTables();
            InitializeWorld(force: false);
        }

        private void OnEnable() {
            if (isBaked || Application.isPlaying) return;

#if UNITY_EDITOR
            InitializeNativeTables();
            if (!HasExistingMeshes()) {
                InitializeWorld(force: false);
            }
            UpdateTransformVisibility();
#endif
        }

        private void OnDisable() {
            DisposeNativeTables();
            if (transform != null) {
                transform.hideFlags &= ~HideFlags.HideInInspector;
            }
        }

        private void OnValidate() {
            UpdateChunkMaterials();
            UpdateTransformVisibility();
        }

        public void UpdateTransformVisibility() {
            if (transform != null) {
                transform.hideFlags = HideFlags.HideInInspector;
            }
        }

        public void UpdateChunkMaterials() {
            Material matToApply = GetValidMaterial();
            VoxelChunk[] childChunks = GetComponentsInChildren<VoxelChunk>(true);
            foreach (var chunk in childChunks) {
                MeshRenderer mr = chunk.GetComponent<MeshRenderer>();
                if (mr != null) {
                    mr.sharedMaterial = matToApply;
                }
            }
        }

        private Material GetValidMaterial() {
            if (terrainMaterial != null) return terrainMaterial;
            Shader defaultShader = Shader.Find("WVBT/URP_triplanar") ?? Shader.Find("Universal Render Pipeline/Lit");
            return new Material(defaultShader);
        }

        private void InitializeNativeTables() {
            if (!nativeTriTable.IsCreated)
                nativeTriTable = new NativeArray<int>(MarchingTablesBurst.FlatTriTable, Allocator.Persistent);
            if (!nativeEdgeTable.IsCreated)
                nativeEdgeTable = new NativeArray<int>(MarchingTablesBurst.FlatEdgeTable, Allocator.Persistent);
            if (!nativeEdgeCorners.IsCreated)
                nativeEdgeCorners = new NativeArray<int2>(MarchingTablesBurst.EdgeCorners, Allocator.Persistent);
        }

        private void DisposeNativeTables() {
            if (nativeTriTable.IsCreated) nativeTriTable.Dispose();
            if (nativeEdgeTable.IsCreated) nativeEdgeTable.Dispose();
            if (nativeEdgeCorners.IsCreated) nativeEdgeCorners.Dispose();
        }

        public Color GetColor(int x, int y, int z) {
            if (x < 0 || x >= TotalVoxelsX || y < 0 || y >= TotalVoxelsY || z < 0 || z >= TotalVoxelsZ)
                return new Color(0, 1, 0, 0);
            return worldColors[x, y, z];
        }

        public void SetColor(int x, int y, int z, Color c) {
            if (x >= 0 && x < TotalVoxelsX && y >= 0 && y < TotalVoxelsY && z >= 0 && z < TotalVoxelsZ)
                worldColors[x, y, z] = c;
        }

        public void Paint(Vector3 worldPoint, float brushRadius, float brushStrength, Color targetColor) {
            Vector3 localPos = transform.InverseTransformPoint(worldPoint) / voxelScale;
            int minX = Mathf.Max(0, Mathf.FloorToInt(localPos.x - brushRadius));
            int maxX = Mathf.Min(TotalVoxelsX - 1, Mathf.CeilToInt(localPos.x + brushRadius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(localPos.y - brushRadius));
            int maxY = Mathf.Min(TotalVoxelsY - 1, Mathf.CeilToInt(localPos.y + brushRadius));
            int minZ = Mathf.Max(0, Mathf.FloorToInt(localPos.z - brushRadius));
            int maxZ = Mathf.Min(TotalVoxelsZ - 1, Mathf.CeilToInt(localPos.z + brushRadius));

            HashSet<Vector3Int> dirtyChunks = new HashSet<Vector3Int>();

            for (int x = minX; x <= maxX; x++) {
                for (int y = minY; y <= maxY; y++) {
                    for (int z = minZ; z <= maxZ; z++) {
                        float dist = Vector3.Distance(new Vector3(x, y, z), localPos);
                        if (dist <= brushRadius) {
                            float falloff = Mathf.Cos((dist / brushRadius) * Mathf.PI * 0.5f);
                            Color current = GetColor(x, y, z);
                            Color blended = Color.Lerp(current, targetColor, brushStrength * falloff);
                            SetColor(x, y, z, blended);

                            int cx = Mathf.FloorToInt((float)x / ChunkSize.x);
                            int cy = Mathf.FloorToInt((float)y / ChunkSize.y);
                            int cz = Mathf.FloorToInt((float)z / ChunkSize.z);

                            dirtyChunks.Add(new Vector3Int(cx, cy, cz));
                            if (x % ChunkSize.x == 0 && cx > 0) dirtyChunks.Add(new Vector3Int(cx - 1, cy, cz));
                            if (y % ChunkSize.y == 0 && cy > 0) dirtyChunks.Add(new Vector3Int(cx, cy - 1, cz));
                            if (z % ChunkSize.z == 0 && cz > 0) dirtyChunks.Add(new Vector3Int(cx, cy, cz - 1));
                        }
                    }
                }
            }

            foreach (var cCoord in dirtyChunks) {
                if (chunks.ContainsKey(cCoord))
                    RebuildChunk(cCoord);
            }
        }

        public void InitializeWorld(bool force = false) {
            if (!force) {
                LoadFromDisk();
            }

            if (worldDensity == null || worldColors == null || force) {
                worldDensity = new float[TotalVoxelsX, TotalVoxelsY, TotalVoxelsZ];
                worldColors = new Color[TotalVoxelsX, TotalVoxelsY, TotalVoxelsZ];

                Color defaultColor = new Color(1f, 0f, 0f, 0f);

                for (int x = 0; x < TotalVoxelsX; x++) {
                    for (int z = 0; z < TotalVoxelsZ; z++) {
                        for (int y = 0; y < TotalVoxelsY; y++) {
                            worldDensity[x, y, z] = (y == 0) ? 1.0f : 0.0f;
                            worldColors[x, y, z] = defaultColor;
                        }
                    }
                }
            }

            CreateChunkObjects();
            UpdateChunkMaterials();
            RebuildAllChunks();
        }

        private void CreateChunkObjects() {
            chunks.Clear();

            for (int i = transform.childCount - 1; i >= 0; i--) {
                Transform child = transform.GetChild(i);
                VoxelChunk existingChunk = child.GetComponent<VoxelChunk>();
                if (existingChunk != null) {
                    string[] parts = child.name.Split('_');
                    if (parts.Length == 4 &&
                        int.TryParse(parts[1], out int cx) &&
                        int.TryParse(parts[2], out int cy) &&
                        int.TryParse(parts[3], out int cz)) {
                        Vector3Int coord = new Vector3Int(cx, cy, cz);
                        if (cx < chunksX && cy < chunksY && cz < chunksZ && !chunks.ContainsKey(coord)) {
                            chunks.Add(coord, existingChunk);
                            continue;
                        }
                    }

                    if (Application.isPlaying) Destroy(child.gameObject);
                    else DestroyImmediate(child.gameObject);
                }
            }

            Material mat = GetValidMaterial();
            for (int cx = 0; cx < chunksX; cx++) {
                for (int cy = 0; cy < chunksY; cy++) {
                    for (int cz = 0; cz < chunksZ; cz++) {
                        Vector3Int coord = new Vector3Int(cx, cy, cz);
                        if (!chunks.ContainsKey(coord)) {
                            GameObject go = new GameObject($"Chunk_{cx}_{cy}_{cz}");
                            go.transform.parent = transform;
                            go.transform.localPosition = new Vector3(cx * ChunkSize.x, cy * ChunkSize.y, cz * ChunkSize.z) * voxelScale;

                            VoxelChunk chunk = go.AddComponent<VoxelChunk>();
                            chunk.GetComponent<MeshRenderer>().sharedMaterial = mat;

#if UNITY_EDITOR
                            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
#endif
                            chunks.Add(coord, chunk);
                        }
                    }
                }
            }
        }

        private bool HasExistingMeshes() {
            VoxelChunk[] existingChunks = GetComponentsInChildren<VoxelChunk>(true);
            if (existingChunks == null || existingChunks.Length == 0)
                return false;

            int chunksWithMeshes = 0;
            foreach (var chunk in existingChunks) {
                MeshFilter mf = chunk.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0) {
                    chunksWithMeshes++;
                }
            }
            return chunksWithMeshes > 0;
        }

        public float GetDensity(int x, int y, int z) {
            if (x < 0 || x >= TotalVoxelsX || y < 0 || y >= TotalVoxelsY || z < 0 || z >= TotalVoxelsZ)
                return 0f;
            return worldDensity[x, y, z];
        }

        public void SetDensity(int x, int y, int z, float val) {
            if (x >= 0 && x < TotalVoxelsX && y >= 0 && y < TotalVoxelsY && z >= 0 && z < TotalVoxelsZ)
                worldDensity[x, y, z] = Mathf.Clamp01(val);
        }

        public void Sculpt(Vector3 worldPoint, float brushRadius, float brushStrength) {
            Vector3 localPos = transform.InverseTransformPoint(worldPoint) / voxelScale;
            int minX = Mathf.Max(0, Mathf.FloorToInt(localPos.x - brushRadius));
            int maxX = Mathf.Min(TotalVoxelsX - 1, Mathf.CeilToInt(localPos.x + brushRadius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(localPos.y - brushRadius));
            int maxY = Mathf.Min(TotalVoxelsY - 1, Mathf.CeilToInt(localPos.y + brushRadius));
            int minZ = Mathf.Max(0, Mathf.FloorToInt(localPos.z - brushRadius));
            int maxZ = Mathf.Min(TotalVoxelsZ - 1, Mathf.CeilToInt(localPos.z + brushRadius));

            HashSet<Vector3Int> dirtyChunks = new HashSet<Vector3Int>();

            for (int x = minX; x <= maxX; x++) {
                for (int y = minY; y <= maxY; y++) {
                    for (int z = minZ; z <= maxZ; z++) {
                        float dist = Vector3.Distance(new Vector3(x, y, z), localPos);
                        if (dist <= brushRadius) {
                            float falloff = Mathf.Cos((dist / brushRadius) * Mathf.PI * 0.5f);
                            float current = GetDensity(x, y, z);
                            SetDensity(x, y, z, current + brushStrength * falloff);

                            if (brushStrength > 0) {
                                Color c = GetColor(x, y, z);
                                if (c.r + c.g + c.b + c.a < 0.1f) {
                                    SetColor(x, y, z, new Color(0f, 1f, 0f, 0f));
                                }
                            }

                            int cx = Mathf.FloorToInt((float)x / ChunkSize.x);
                            int cy = Mathf.FloorToInt((float)y / ChunkSize.y);
                            int cz = Mathf.FloorToInt((float)z / ChunkSize.z);

                            dirtyChunks.Add(new Vector3Int(cx, cy, cz));
                            if (x % ChunkSize.x == 0 && cx > 0) dirtyChunks.Add(new Vector3Int(cx - 1, cy, cz));
                            if (y % ChunkSize.y == 0 && cy > 0) dirtyChunks.Add(new Vector3Int(cx, cy - 1, cz));
                            if (z % ChunkSize.z == 0 && cz > 0) dirtyChunks.Add(new Vector3Int(cx, cy, cz - 1));
                        }
                    }
                }
            }

            foreach (var cCoord in dirtyChunks) {
                if (chunks.ContainsKey(cCoord))
                    RebuildChunk(cCoord);
            }
        }

        public void Smooth(Vector3 worldPoint, float brushRadius, float brushStrength) {
            Vector3 localPos = transform.InverseTransformPoint(worldPoint) / voxelScale;

            int minX = Mathf.Max(1, Mathf.FloorToInt(localPos.x - brushRadius));
            int maxX = Mathf.Min(TotalVoxelsX - 2, Mathf.CeilToInt(localPos.x + brushRadius));
            int minY = Mathf.Max(1, Mathf.FloorToInt(localPos.y - brushRadius));
            int maxY = Mathf.Min(TotalVoxelsY - 2, Mathf.CeilToInt(localPos.y + brushRadius));
            int minZ = Mathf.Max(1, Mathf.FloorToInt(localPos.z - brushRadius));
            int maxZ = Mathf.Min(TotalVoxelsZ - 2, Mathf.CeilToInt(localPos.z + brushRadius));

            int sizeXRange = maxX - minX + 1;
            int sizeYRange = maxY - minY + 1;
            int sizeZRange = maxZ - minZ + 1;

            if (sizeXRange <= 0 || sizeYRange <= 0 || sizeZRange <= 0) return;

            float[,,] temp = new float[sizeXRange, sizeYRange, sizeZRange];
            HashSet<Vector3Int> dirtyChunks = new HashSet<Vector3Int>();

            for (int x = minX; x <= maxX; x++) {
                for (int y = minY; y <= maxY; y++) {
                    for (int z = minZ; z <= maxZ; z++) {
                        float current = GetDensity(x, y, z);
                        float dist = Vector3.Distance(new Vector3(x, y, z), localPos);

                        if (dist <= brushRadius && current > 0.05f && current < 0.95f) {
                            float sum = 0f;
                            sum += GetDensity(x + 1, y, z);
                            sum += GetDensity(x - 1, y, z);
                            sum += GetDensity(x, y + 1, z);
                            sum += GetDensity(x, y - 1, z);
                            sum += GetDensity(x, y, z + 1);
                            sum += GetDensity(x, y, z - 1);
                            float laplacianAvg = sum / 6f;

                            float falloff = Mathf.Cos((dist / brushRadius) * Mathf.PI * 0.5f);
                            float surfaceBand = Mathf.Clamp01(1.0f - Mathf.Abs(current - isoLevel) * 2.0f);

                            float targetDensity = Mathf.Lerp(current, laplacianAvg, brushStrength * falloff * surfaceBand);
                            temp[x - minX, y - minY, z - minZ] = targetDensity;

                            int cx = Mathf.FloorToInt((float)x / ChunkSize.x);
                            int cy = Mathf.FloorToInt((float)y / ChunkSize.y);
                            int cz = Mathf.FloorToInt((float)z / ChunkSize.z);

                            dirtyChunks.Add(new Vector3Int(cx, cy, cz));
                            if (x % ChunkSize.x == 0 && cx > 0) dirtyChunks.Add(new Vector3Int(cx - 1, cy, cz));
                            if (y % ChunkSize.y == 0 && cy > 0) dirtyChunks.Add(new Vector3Int(cx, cy - 1, cz));
                            if (z % ChunkSize.z == 0 && cz > 0) dirtyChunks.Add(new Vector3Int(cx, cy, cz - 1));
                        }
                        else {
                            temp[x - minX, y - minY, z - minZ] = current;
                        }
                    }
                }
            }

            for (int x = minX; x <= maxX; x++) {
                for (int y = minY; y <= maxY; y++) {
                    for (int z = minZ; z <= maxZ; z++) {
                        SetDensity(x, y, z, temp[x - minX, y - minY, z - minZ]);
                    }
                }
            }

            foreach (var cCoord in dirtyChunks) {
                if (chunks.ContainsKey(cCoord))
                    RebuildChunk(cCoord);
            }
        }

        [HideInInspector]
        public bool hasBakedLightmapUVs = false;

        /// <summary>
        /// Rebuilds all child chunks with lightmap UVs generated from the live density field.
        /// </summary>
        public void PrepareForLightmapBake() {
            hasBakedLightmapUVs = true;

            // Ensure child chunk dictionary is populated
            CreateChunkObjects();

            foreach (var pair in chunks) {
                RebuildChunk(pair.Key, generateLightmapUVs: true);
            }

            Debug.Log("<color=green>[VoxelWorld]</color> Generated Lightmap UVs for live terrain.");
        }

        /// <summary>
        /// Generates the mesh of one chunk.
        /// triangleRatio = 1 gives the full resolution (LOD0) mesh. Lower values decimate that mesh, but every vertex on
        /// the border of the chunk (where the surface leaves the chunk) is kept exactly as in LOD0. Because of that a
        /// chunk at any LOD joins a neighbour at any other LOD without cracks.
        /// </summary>
        public Mesh GenerateChunkMesh(Vector3Int coord, float triangleRatio = 1f, bool generateLightmapUVs = false) {
            InitializeNativeTables();

            int paddedSx = ChunkSize.x + 3;
            int paddedSy = ChunkSize.y + 3;
            int paddedSz = ChunkSize.z + 3;

            NativeArray<float> chunkDensities = new NativeArray<float>(paddedSx * paddedSy * paddedSz, Allocator.TempJob);
            NativeArray<float4> chunkColors = new NativeArray<float4>(paddedSx * paddedSy * paddedSz, Allocator.TempJob);

            int startX = coord.x * ChunkSize.x - 1;
            int startY = coord.y * ChunkSize.y - 1;
            int startZ = coord.z * ChunkSize.z - 1;

            for (int x = 0; x < paddedSx; x++)
                for (int y = 0; y < paddedSy; y++)
                    for (int z = 0; z < paddedSz; z++) {
                        int idx = x + y * paddedSx + z * paddedSx * paddedSy;
                        chunkDensities[idx] = GetDensity(startX + x, startY + y, startZ + z);
                        Color c = GetColor(startX + x, startY + y, startZ + z);
                        chunkColors[idx] = new float4(c.r, c.g, c.b, c.a);
                    }

            NativeList<TerrainVertex> verts = new NativeList<TerrainVertex>(Allocator.TempJob);
            NativeList<int> inds = new NativeList<int>(Allocator.TempJob);

            MarchingCubesJob job = new MarchingCubesJob {
                chunkSize = ChunkSize,
                isoLevel = isoLevel,
                densities = chunkDensities,
                colors = chunkColors,
                triTable = nativeTriTable,
                edgeTable = nativeEdgeTable,
                edgeCorners = nativeEdgeCorners,
                outputVertices = verts,
                outputIndices = inds
            };

            job.Schedule().Complete();

            // Vertices are in voxel units here; the border vertices are identical for every LOD
            TerrainVertex[] meshVerts = verts.AsArray().ToArray();
            int[] meshIndices = inds.AsArray().ToArray();

            chunkDensities.Dispose();
            chunkColors.Dispose();
            verts.Dispose();
            inds.Dispose();

            if (triangleRatio < 0.999f && meshIndices.Length > 0) {
                MeshDecimator.Decimate(meshVerts, meshIndices, triangleRatio, out TerrainVertex[] decimatedVerts, out int[] decimatedIndices);
                meshVerts = decimatedVerts;
                meshIndices = decimatedIndices;
            }

            Mesh m = new Mesh();
            if (meshVerts.Length > 0) {
                if (meshVerts.Length > 65000) m.indexFormat = IndexFormat.UInt32;

                Vector3[] pos = new Vector3[meshVerts.Length];
                Vector3[] norms = new Vector3[meshVerts.Length];
                Color[] colors = new Color[meshVerts.Length];

                for (int i = 0; i < meshVerts.Length; i++) {
                    pos[i] = meshVerts[i].position * voxelScale;
                    norms[i] = meshVerts[i].normal;
                    colors[i] = new Color(meshVerts[i].color.x, meshVerts[i].color.y, meshVerts[i].color.z, meshVerts[i].color.w);
                }

                m.vertices = pos;
                m.normals = norms;
                m.colors = colors;
                m.triangles = meshIndices;
                m.RecalculateBounds();

#if UNITY_EDITOR
                if (generateLightmapUVs) {
                    UnwrapParam unwrap;
                    UnwrapParam.SetDefaults(out unwrap);
                    unwrap.packMargin = 0.04f;
                    unwrap.angleError = 8f;
                    unwrap.areaError = 15f;
                    Unwrapping.GenerateSecondaryUVSet(m, unwrap);
                }
#endif
            }

            return m;
        }

        public void RebuildChunk(Vector3Int coord, bool generateLightmapUVs = false) {
            Mesh m = GenerateChunkMesh(coord, 1f, generateLightmapUVs);

            TerrainVertex[] verts = new TerrainVertex[m.vertexCount];
            Vector3[] pos = m.vertices;
            Vector3[] norms = m.normals;
            Color[] cols = m.colors;

            for (int i = 0; i < m.vertexCount; i++) {
                verts[i] = new TerrainVertex {
                    position = pos[i],
                    normal = norms[i],
                    color = new float4(cols[i].r, cols[i].g, cols[i].b, cols[i].a)
                };
            }

            chunks[coord].ApplyMeshData(verts, m.triangles, generateLightmapUVs);
        }



        public void RebuildAllChunks() {
            foreach (var pair in chunks) {
                RebuildChunk(pair.Key, generateLightmapUVs: hasBakedLightmapUVs);
            }
        }
    }
}