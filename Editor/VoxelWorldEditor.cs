using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.Linq;

namespace vilemmakovicka.wvbterrain {
    [CustomEditor(typeof(VoxelWorld))]
    public class VoxelWorldEditor : Editor {
        private VoxelWorld world;
        private bool isDirty = false;

        public enum ToolMode { Sculpt, Paint }
        private ToolMode activeTool = ToolMode.Sculpt;

        private int selectedLayerIndex = 0;
        private readonly string[] layerDescriptions = { "(R)", "(G)", "(B)", "(A)" };

        private static float brushRadius = 4f;
        private static float brushStrength = 0.03f;

        // Material system fields
        private SerializedProperty materialProp;
        private int selectedMaterialIndex = 0;
        private string[] compatibleMaterialNames = new string[0];
        private Material[] compatibleMaterials = new Material[0];

        private SerializedObject transformSerializedObject;
        private SerializedProperty localPositionProperty;

        private SerializedProperty chunksXProperty;
        private SerializedProperty chunksYProperty;
        private SerializedProperty chunksZProperty;
        private SerializedProperty voxelScaleProperty;

        private bool visualizeChunks = false;

        /*material preview*/
        private PreviewRenderUtility _previewUtility;
        private float _spinAngle;
        private double _lastTime;
        private Mesh _fallbackSphereMesh;
        private Mesh previewMesh;

        private void OnEnable() {
            world = (VoxelWorld)target;
            isDirty = false;

            /**/
            string assetPath = "Packages/com.vilemmakovicka.wvbterrain/Runtime/Defaults/wvbt_terrainPreview.fbx";
            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            previewMesh = subAssets.OfType<Mesh>().FirstOrDefault() ?? _fallbackSphereMesh;

            _previewUtility = new PreviewRenderUtility();
            _previewUtility.cameraFieldOfView = 30f;

            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _fallbackSphereMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            DestroyImmediate(primitive);

            _lastTime = EditorApplication.timeSinceStartup;
            /**/

            materialProp = serializedObject.FindProperty("terrainMaterial")
                        ?? serializedObject.FindProperty("material")
                        ?? serializedObject.FindProperty("targetMaterial");

            RefreshCompatibleMaterials();

            if (world != null && world.transform != null) {
                transformSerializedObject = new SerializedObject(world.transform);
                localPositionProperty = transformSerializedObject.FindProperty("m_LocalPosition");
            }

            chunksXProperty = serializedObject.FindProperty("chunksX");
            chunksYProperty = serializedObject.FindProperty("chunksY");
            chunksZProperty = serializedObject.FindProperty("chunksZ");
            voxelScaleProperty = serializedObject.FindProperty("voxelScale");
        }

        private void OnDisable() {
            // Triggers automatically whenever the user clicks off or selects another object
            if (isDirty && world != null) {
                world.SaveToDisk();
                isDirty = false;
            }

            if (_previewUtility != null) {
                _previewUtility.Cleanup();
                _previewUtility = null;
            }
        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            // Advance spin angle based on actual time elapsed
            double currentTime = EditorApplication.timeSinceStartup;
            float deltaTime = (float)(currentTime - _lastTime);
            _lastTime = currentTime;
            _spinAngle = (_spinAngle + 45f * deltaTime) % 360f;

            // Position
            if (transformSerializedObject != null && localPositionProperty != null) {
                transformSerializedObject.Update();

                EditorGUILayout.PropertyField(localPositionProperty, new GUIContent("Position"));

                transformSerializedObject.ApplyModifiedProperties();
            }

            // Locked mode
            if (world.IsBaked || !world.isEditingMode) {
                EditorGUILayout.HelpBox(
                    world.IsBaked
                        ? "Terrain is currently BAKED."
                        : "Terrain is currently LOCKED.",
                    MessageType.Info
                );

                GUI.backgroundColor = new Color(0.4f, 1f, 0.4f);
                if (GUILayout.Button("Enter Edit Mode", GUILayout.Height(35))) {
                    world.UnlockForEditing();
                    EditorUtility.SetDirty(world);
                }
                GUI.backgroundColor = Color.white;
                return; // Stops here: tools and Scene GUI remain locked
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Terrain generation", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            // Size in chunks

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Size in chunks");
            EditorGUILayout.LabelField("x", GUILayout.Width(10.0f));
            EditorGUILayout.PropertyField(chunksXProperty, GUIContent.none);
            EditorGUILayout.LabelField("y", GUILayout.Width(10.0f));
            EditorGUILayout.PropertyField(chunksYProperty, GUIContent.none);
            EditorGUILayout.LabelField("z", GUILayout.Width(10.0f));
            EditorGUILayout.PropertyField(chunksZProperty, GUIContent.none);
            EditorGUILayout.EndHorizontal();

            // Enforce positive chunk counts
            chunksXProperty.intValue = Mathf.Max(1, chunksXProperty.intValue);
            chunksYProperty.intValue = Mathf.Max(1, chunksYProperty.intValue);
            chunksZProperty.intValue = Mathf.Max(1, chunksZProperty.intValue);

            // Voxel size
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.PropertyField(voxelScaleProperty, new GUIContent("Voxel Scale"));
            voxelScaleProperty.floatValue = Mathf.Max(0.01f, voxelScaleProperty.floatValue);
            visualizeChunks = GUILayout.Toggle(visualizeChunks, "Visualize chunks");
            if (EditorGUI.EndChangeCheck()) {
                SceneView.RepaintAll();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("LOD (used when baking)", EditorStyles.miniBoldLabel);
            EditorGUILayout.Slider(serializedObject.FindProperty("lod1TriangleRatio"), 0.05f, 1f, "LOD1 triangle ratio");
            EditorGUILayout.Slider(serializedObject.FindProperty("lod2TriangleRatio"), 0.02f, 1f, "LOD2 triangle ratio");

            EditorGUILayout.Space(10);
            if (GUILayout.Button("Generate terrain")) {
                if (EditorUtility.DisplayDialog("Generate Terrain?", "This will delete the current terrain. Continue?", "Yes", "No")) {
                    world.InitializeWorld(force: true);
                    world.SaveToDisk();
                }
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);
            DrawMaterialSection();

            EditorGUILayout.Space(10);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Terrain Editing Mode", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            activeTool = (ToolMode)GUILayout.Toolbar((int)activeTool, new string[] { "Sculpt", "Texture Paint" });

            EditorGUILayout.Space(5);
            brushRadius = EditorGUILayout.Slider("Brush Radius", brushRadius, 1f, 25f);
            // brushStrength = EditorGUILayout.Slider("Brush Strength", brushStrength, 0.01f, 1f);
            brushStrength = EditorGUIExtensions.LogSliderWithField("Brush Strength", brushStrength, 0.01f, 1f, power: 3f);

            Material currentMat = GetTargetMaterial();

            if (activeTool == ToolMode.Paint) {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Texture selection", EditorStyles.boldLabel);

                // Visual Texture Grid
                DrawVisualPalette(currentMat);

                EditorGUILayout.HelpBox(
                    "Paint Mode:\n" +
                    "• Left Click + Drag: Paint selected texture\n" +
                    "(Uses Vertex Colors)",
                    MessageType.Info
                );
            }
            else {
                EditorGUILayout.HelpBox(
                    "Sculpt Mode Hotkeys:\n" +
                    " • Left Click: Raise / Extrude\n" +
                    " • Shift + Left Click: Carve / Dig\n" +
                    " • Ctrl + Left Click: Smooth",
                    MessageType.Info
                );
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();

            GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
            if (GUILayout.Button(new GUIContent("Bake terrain", "Converts terrain into static meshes, generates LODs and prepares lightmap UVs"), GUILayout.Height(28))) {
                world.LockAndBake();
                EditorUtility.SetDirty(world);
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(5);

            serializedObject.ApplyModifiedProperties();
        }

        #region Material Management & Texture Slots

        private Material GetTargetMaterial() {
            if (materialProp != null && materialProp.objectReferenceValue != null)
                return materialProp.objectReferenceValue as Material;

            // Fallback: Check MeshRenderer or world component
            Renderer rend = world.GetComponent<Renderer>();
            return rend != null ? rend.sharedMaterial : null;
        }

        private void SetTargetMaterial(Material newMat) {
            if (materialProp != null) {
                materialProp.objectReferenceValue = newMat;
                serializedObject.ApplyModifiedProperties();
            }

            Renderer rend = world.GetComponent<Renderer>();
            if (rend != null) {
                Undo.RecordObject(rend, "Set Terrain Material");
                rend.sharedMaterial = newMat;
                EditorUtility.SetDirty(rend);
            }
        }

        private void DrawMaterialSection() {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Terrain Material", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            Material currentMat = GetTargetMaterial();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+", GUILayout.Height(18), GUILayout.Width(18))) {
                CreateNewMaterial(currentMat);
                currentMat = GetTargetMaterial();
            }
            if (currentMat != null && currentMat.shader != null) {
                if (compatibleMaterials.Length > 0) {
                    int newIndex = EditorGUILayout.Popup("", selectedMaterialIndex, compatibleMaterialNames);
                    if (newIndex != selectedMaterialIndex && newIndex >= 0 && newIndex < compatibleMaterials.Length) {
                        selectedMaterialIndex = newIndex;
                        SetTargetMaterial(compatibleMaterials[newIndex]);
                        GUI.FocusControl(null);
                    }
                }
                else {
                    EditorGUILayout.HelpBox("No other materials in the project share this shader.", MessageType.None);
                }
            }
            else {
                EditorGUILayout.HelpBox("Assign or create a material to view texture slots and shader options.", MessageType.Warning);
            }

            /*if (GUILayout.Button("Refresh List", GUILayout.Width(120), GUILayout.Height(22))) {
                RefreshCompatibleMaterials();
            }*/
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginHorizontal();
            // Texture Fields
            Texture currentTexR = currentMat.GetTexture("_TexRock");
            Texture newTexR = (Texture)EditorGUILayout.ObjectField("", currentTexR, typeof(Texture), false, GUILayout.Width(60));
            Texture currentTexG = currentMat.GetTexture("_TexGrass");
            Texture newTexG = (Texture)EditorGUILayout.ObjectField("", currentTexG, typeof(Texture), false, GUILayout.Width(60));
            Texture currentTexB = currentMat.GetTexture("_TexDirt");
            Texture newTexB = (Texture)EditorGUILayout.ObjectField("", currentTexB, typeof(Texture), false, GUILayout.Width(60));
            Texture currentTexA = currentMat.GetTexture("_TexPath");
            Texture newTexA = (Texture)EditorGUILayout.ObjectField("", currentTexA, typeof(Texture), false, GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            Rect viewportRect = GUILayoutUtility.GetRect(200, 200, GUILayout.Width(200), GUILayout.Height(200));
            GUI.Box(viewportRect, GUIContent.none, EditorStyles.textArea);
            if (Event.current.type == EventType.Repaint) {
                DrawRotatingPreview(viewportRect, previewMesh, currentMat);
            }
            EditorGUILayout.EndHorizontal();

            // Apply changes
            if (EditorGUI.EndChangeCheck()) {
                Undo.RecordObject(currentMat, "Modify Material Properties");

                if (newTexR != currentTexR) currentMat.SetTexture("_TexRock", newTexR);
                if (newTexR != currentTexG) currentMat.SetTexture("_TexGrass", newTexG);
                if (newTexR != currentTexB) currentMat.SetTexture("_TexDirt", newTexB);
                if (newTexR != currentTexA) currentMat.SetTexture("_TexPath", newTexA);

                EditorUtility.SetDirty(currentMat);
                SceneView.RepaintAll(); // Updates scene immediately
            }

            EditorGUILayout.EndVertical();
        }

        private List<string> GetShaderTextureProperties(Material mat) {
            List<string> texProps = new List<string>();
            if (mat == null || mat.shader == null) return texProps;

            Shader shader = mat.shader;
            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++) {
                if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture) {
                    texProps.Add(shader.GetPropertyName(i));
                }
            }
            return texProps;
        }

        private void DrawInlineTextureSlots(Material mat) {
            EditorGUILayout.LabelField("Exposed Shader Textures (Editable)", EditorStyles.miniBoldLabel);

            List<string> texProps = GetShaderTextureProperties(mat);
            int slotsToShow = Mathf.Min(texProps.Count, 4);

            if (slotsToShow == 0) {
                EditorGUILayout.HelpBox("Current shader has no exposed texture properties.", MessageType.Info);
                return;
            }

            for (int i = 0; i < slotsToShow; i++) {
                string propName = texProps[i];
                Texture currentTex = mat.GetTexture(propName);
                string labelName = i < layerDescriptions.Length ? $"{layerDescriptions[i]} [{propName}]" : propName;

                EditorGUI.BeginChangeCheck();
                Texture newTex = (Texture)EditorGUILayout.ObjectField(labelName, currentTex, typeof(Texture), false);
                if (EditorGUI.EndChangeCheck()) {
                    Undo.RecordObject(mat, $"Change {propName}");
                    mat.SetTexture(propName, newTex);
                    EditorUtility.SetDirty(mat);
                }
            }
        }

        private void CreateNewMaterial(Material referenceMat) {
            Shader targetShader = referenceMat != null && referenceMat.shader != null
                ? referenceMat.shader
                : Shader.Find("Custom/wvbt_triplanar") ?? Shader.Find("Standard");

            string defaultName = referenceMat != null ? $"{referenceMat.name}_Variant.mat" : "Terrain_Material.mat";
            string path = EditorUtility.SaveFilePanelInProject("Save Terrain Material", defaultName, "mat", "Select a save location.");

            if (string.IsNullOrEmpty(path)) return;

            Material newMat = new Material(targetShader);
            AssetDatabase.CreateAsset(newMat, path);
            AssetDatabase.SaveAssets();

            SetTargetMaterial(newMat);
            RefreshCompatibleMaterials();
        }

        private void RefreshCompatibleMaterials() {
            Material currentMat = GetTargetMaterial();
            if (currentMat == null || currentMat.shader == null) {
                compatibleMaterials = new Material[0];
                compatibleMaterialNames = new string[0];
                return;
            }

            string targetShaderName = currentMat.shader.name;
            string[] guids = AssetDatabase.FindAssets("t:Material");

            List<Material> matched = new List<Material>();
            List<string> names = new List<string>();
            selectedMaterialIndex = 0;

            for (int i = 0; i < guids.Length; i++) {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                Material m = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (m != null && m.shader != null && m.shader.name == targetShaderName) {
                    if (m == currentMat)
                        selectedMaterialIndex = matched.Count;

                    matched.Add(m);
                    names.Add($"{m.name}.mat");
                }
            }

            compatibleMaterials = matched.ToArray();
            compatibleMaterialNames = names.ToArray();
        }

        #endregion

        #region Visual Palette Selector

        private void DrawVisualPalette(Material currentMat) {
            List<string> texProps = GetShaderTextureProperties(currentMat);
            int totalLayers = 4; // R, G, B, A
            float previewSize = 64f;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            for (int i = 0; i < totalLayers; i++) {
                Texture tex = (currentMat != null && i < texProps.Count) ? currentMat.GetTexture(texProps[i]) : null;
                bool isSelected = (selectedLayerIndex == i);

                EditorGUILayout.BeginVertical(GUILayout.Width(previewSize + 10));

                // Highlight border if selected
                Rect buttonRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.Width(previewSize), GUILayout.Height(previewSize));

                if (isSelected) {
                    Handles.DrawSolidRectangleWithOutline(
                        new Rect(buttonRect.x - 2, buttonRect.y - 2, buttonRect.width + 4, buttonRect.height + 4),
                        new Color(0f, 0f, 0f, 0f),
                        new Color(0.24f, 0.49f, 0.9f, 1f)
                    );
                }

                // Clickable thumbnail preview
                if (tex != null) {
                    GUI.DrawTexture(buttonRect, tex, ScaleMode.ScaleToFit);
                    if (GUI.Button(buttonRect, GUIContent.none, GUIStyle.none)) {
                        selectedLayerIndex = i;
                        GUI.FocusControl(null);
                    }
                }
                else {
                    // Fallback button if no texture is loaded in this channel
                    string emptyLabel = i < layerDescriptions.Length ? layerDescriptions[i] : $"Slot {i}";
                    if (GUI.Button(buttonRect, $"No Tex\n({emptyLabel})")) {
                        selectedLayerIndex = i;
                    }
                }

                // Channel label below thumbnail
                EditorGUILayout.LabelField(layerDescriptions[i], isSelected ? EditorStyles.boldLabel : EditorStyles.miniLabel, GUILayout.Width(previewSize + 10));
                EditorGUILayout.EndVertical();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(5);
        }

        private Color GetTargetColor(int layerIndex) {
            return layerIndex switch {
                0 => new Color(1f, 0f, 0f, 0f), // (R)
                1 => new Color(0f, 1f, 0f, 0f), // (G)
                2 => new Color(0f, 0f, 1f, 0f), // (B)
                3 => new Color(0f, 0f, 0f, 1f), // (A)
                _ => new Color(1f, 0f, 0f, 0f),
            };
        }

        #endregion

        #region Scene Painting & Sculpting
        /// <summary>
        /// Terrain tool gizmos & tool usage
        /// </summary>
        private void OnSceneGUI() {
            Event evnt = Event.current;

            SceneView sceneView = SceneView.currentDrawingSceneView;
            if (sceneView != null) {
                sceneView.wantsMouseMove = true;
                if (evnt.type == EventType.MouseMove) {
                    sceneView.Repaint();
                }
            }

            if (!world.isEditingMode) { return; }

            // Chunk visualization
            if (visualizeChunks) {
                for (int x = 0; x < world.chunksX; x++)
                    for (int y = 0; y < world.chunksY; y++)
                        for (int z = 0; z < world.chunksZ; z++) {
                            Vector3 chunkSize = VoxelWorld.ChunkSizeV3;
                            Vector3 centerOffset = chunkSize / 2;
                            Vector3 cubePosition = new Vector3(x * chunkSize.x, y * chunkSize.y, z * chunkSize.z);
                            Handles.DrawWireCube(world.transform.position + cubePosition + centerOffset, chunkSize);
                        }
            }

            // Mouse raycast
            Ray ray = HandleUtility.GUIPointToWorldRay(evnt.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit)) {
                var hitTransform = hit.collider.transform;
                if (hitTransform.IsChildOf(world.transform)) {
                    // Set handles color based on the tool used
                    Handles.color = activeTool switch {
                        ToolMode.Paint => Color.magenta, // Texture Paint mode
                        ToolMode.Sculpt when evnt.control => Color.cyan,    // Smooth mode
                        ToolMode.Sculpt when evnt.shift => Color.red,     // Dig mode
                        _ => Color.green,   // Add mode
                    };

                    // Draw brush visualization
                    Handles.DrawWireDisc(hit.point, hit.normal, brushRadius * world.voxelScale);
                    Handles.ArrowHandleCap(
                        controlID: 0,
                        hit.point,
                        Quaternion.LookRotation(hit.normal),
                        size: brushRadius * world.voxelScale * brushStrength,
                        EventType.Repaint
                    );


                    HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

                    bool usingLMB = evnt.button == 0;
                    bool isClickingOrDragging = evnt.type == EventType.MouseDown || evnt.type == EventType.MouseDrag;

                    // Use selected tools
                    if (usingLMB && isClickingOrDragging) {
                        if (activeTool == ToolMode.Paint) {
                            world.Paint(hit.point, brushRadius, brushStrength, GetTargetColor(selectedLayerIndex));
                        }
                        else if (activeTool == ToolMode.Sculpt) {
                            if (evnt.control) world.Smooth(hit.point, brushRadius, brushStrength); // Smooth
                            else if (evnt.shift) world.Sculpt(hit.point, brushRadius, -brushStrength); // Dig
                            else world.Sculpt(hit.point, brushRadius, brushStrength); // Add
                        }

                        isDirty = true;
                        evnt.Use();
                    }
                }
            }
        }
        #endregion

        /// <summary>
        /// When the sceen is saved, save all voxel terrains too.
        /// </summary>
        public class VoxelTerrainAutoSaver : AssetModificationProcessor {
            /// <summary>
            /// When the sceen is saved, save all voxel terrains too.
            /// </summary>
            private static string[] OnWillSaveAssets(string[] paths) {
                VoxelWorld[] worlds = FindObjectsOfType<VoxelWorld>();
                foreach (var w in worlds) {
                    if (w != null) {
                        w.SaveToDisk();
                    }
                }
                return paths;
            }
        }

        void DrawRotatingPreview(Rect rect, Mesh mesh, Material mat) {
            _previewUtility.BeginPreview(rect, GUIStyle.none);

            // Setup camera
            Quaternion camRot = Quaternion.Euler(20f, _spinAngle, 0f);
            _previewUtility.camera.transform.position = camRot * (Vector3.back * 6f);
            _previewUtility.camera.transform.rotation = camRot;
            _previewUtility.camera.nearClipPlane = 0.01f;
            _previewUtility.camera.farClipPlane = 100f;

            // 2. Setup Key Light (Parented/oriented relative to camera)
            Light keyLight = _previewUtility.lights[0];
            keyLight.enabled = true;
            keyLight.type = LightType.Directional;
            keyLight.color = Color.white;
            keyLight.intensity = 1.5f;
            keyLight.transform.rotation = camRot * Quaternion.Euler(30f, 45f, 0f);

            _previewUtility.ambientColor = new Color(0.7f, 0.75f, 0.85f);

            // Spin model
            Quaternion rotation = Quaternion.Euler(-90, 0, 0f);
            Matrix4x4 trs = Matrix4x4.TRS(Vector3.zero, rotation, Vector3.one * 100);

            _previewUtility.DrawMesh(mesh, trs, mat, 0);
            _previewUtility.camera.Render();

            Texture resultTex = _previewUtility.EndPreview();
            GUI.DrawTexture(rect, resultTex, ScaleMode.StretchToFill, false);
        }
    }
}