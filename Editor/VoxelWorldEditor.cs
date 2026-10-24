using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace vilemmakovicka.wvbterrain {
    [CustomEditor(typeof(VoxelWorld))]
    public class VoxelWorldEditor : Editor {
        private VoxelWorld world;
        private bool isDirty = false;

        public enum ToolMode { Sculpt, Paint }
        private ToolMode activeTool = ToolMode.Sculpt;

        private int selectedLayerIndex = 0; // 0: Rock (R), 1: Grass (G), 2: Dirt (B), 3: Cobble (A)
        private readonly string[] layerDescriptions = { "(R)", "(G)", "(B)", "(A)" };

        private static float brushRadius = 4f;
        private static float brushStrength = 0.03f;

        // Material system fields
        private SerializedProperty materialProp;
        private int selectedMaterialIndex = 0;
        private string[] compatibleMaterialNames = new string[0];
        private Material[] compatibleMaterials = new Material[0];

        private SerializedObject transformSerializedObject;
        private SerializedProperty localPositionProp;

        private SerializedProperty chunksXProp;
        private SerializedProperty chunksYProp;
        private SerializedProperty chunksZProp;
        private SerializedProperty voxelScaleProp;
        private SerializedProperty isoLevelProp;
        private SerializedProperty terrainMaterialProp;

        private void OnEnable() {
            world = (VoxelWorld)target;
            isDirty = false;
            //if(world.isEditingMode) world.InitializeWorld();

            // Attempts to find target material serialized property on VoxelWorld
            materialProp = serializedObject.FindProperty("terrainMaterial")
                        ?? serializedObject.FindProperty("material")
                        ?? serializedObject.FindProperty("targetMaterial");

            RefreshCompatibleMaterials();

            if (world != null && world.transform != null) {
                transformSerializedObject = new SerializedObject(world.transform);
                localPositionProp = transformSerializedObject.FindProperty("m_LocalPosition");
            }

            chunksXProp = serializedObject.FindProperty("chunksX");
            chunksYProp = serializedObject.FindProperty("chunksY");
            chunksZProp = serializedObject.FindProperty("chunksZ");
            voxelScaleProp = serializedObject.FindProperty("voxelScale");
            isoLevelProp = serializedObject.FindProperty("isoLevel");
            terrainMaterialProp = serializedObject.FindProperty("terrainMaterial");
        }

        private void OnDisable() {
            // Triggers automatically whenever the user clicks off or selects another object
            if (isDirty && world != null) {
                world.SaveToDisk();
                isDirty = false;
            }
        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            // Position
            if (transformSerializedObject != null && localPositionProp != null) {
                transformSerializedObject.Update();

                // Draws a clean Vector3 field mapped directly to transform.localPosition
                EditorGUILayout.PropertyField(localPositionProp, new GUIContent("Position"));

                // Applies undo/redo records and marks the scene dirty if modified
                transformSerializedObject.ApplyModifiedProperties();
            }

            // Size
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Size");
            EditorGUILayout.PropertyField(chunksXProp, GUIContent.none);
            EditorGUILayout.PropertyField(chunksYProp, GUIContent.none);
            EditorGUILayout.PropertyField(chunksZProp, GUIContent.none);
            EditorGUILayout.EndHorizontal();

            // Enforce positive chunk counts
            chunksXProp.intValue = Mathf.Max(1, chunksXProp.intValue);
            chunksYProp.intValue = Mathf.Max(1, chunksYProp.intValue);
            chunksZProp.intValue = Mathf.Max(1, chunksZProp.intValue);

            // Voxel size
            EditorGUILayout.PropertyField(voxelScaleProp, new GUIContent("Voxel Scale (m)"));
            voxelScaleProp.floatValue = Mathf.Max(0.01f, voxelScaleProp.floatValue);

            // Locked mode
            if (world.IsBaked || !world.isEditingMode) {
                Material curMat = GetTargetMaterial();
                Material assignedMat = (Material)EditorGUILayout.ObjectField("Active Material", curMat, typeof(Material), false);
                if (EditorGUI.EndChangeCheck()) { SetTargetMaterial(assignedMat); }

                EditorGUILayout.HelpBox(
                    world.IsBaked
                        ? "Terrain is currently BAKED (Static .asset Meshes).\nProcedural generation is completely disabled."
                        : "Terrain is currently LOCKED.\nClick Unlock to sculpt or paint.",
                    MessageType.Info
                );

                GUI.backgroundColor = new Color(0.4f, 1f, 0.4f);
                if (GUILayout.Button("Unlock / Enter Edit Mode", GUILayout.Height(35))) {
                    world.UnlockForEditing();
                    EditorUtility.SetDirty(world);
                }
                GUI.backgroundColor = Color.white;
                return; // Stops here: tools and Scene GUI remain locked
            }

            EditorGUILayout.Space(10);
            DrawMaterialSection();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Terrain Editing Mode", EditorStyles.boldLabel);
            activeTool = (ToolMode)GUILayout.Toolbar((int)activeTool, new string[] { "Sculpt / Shape", "Texture Paint" });

            EditorGUILayout.Space(5);
            brushRadius = EditorGUILayout.Slider("Brush Radius", brushRadius, 1f, 25f);
            brushStrength = EditorGUILayout.Slider("Brush Strength", brushStrength, 0.01f, 1f);

            Material currentMat = GetTargetMaterial();

            if (activeTool == ToolMode.Paint) {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Visual Paint Layer Palette", EditorStyles.boldLabel);

                // Visual Texture Grid
                DrawVisualPalette(currentMat);

                EditorGUILayout.HelpBox(
                    "Paint Mode:\n" +
                    "• Left Click + Drag: Paint selected texture\n" +
                    "Uses Vertex Colors (R = Rock, G = Grass, B = Dirt, A = Path).",
                    MessageType.Info
                );
            }
            else {
                EditorGUILayout.HelpBox(
                    "Sculpt Mode Hotkeys:\n" +
                    "• Left Click: Raise / Extrude\n" +
                    "• Shift + Left Click: Carve / Dig\n" +
                    "• Ctrl + Left Click: Smooth",
                    MessageType.Info
                );
            }

            // Storage
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Persistence & Storage", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save Terrain to Disk")) {
                world.SaveToDisk();
            }
            if (GUILayout.Button("Reload from Disk")) {
                world.InitializeWorld(force: false);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
            if (GUILayout.Button("Reset & Reinitialize Entire World (Wipes Disk)")) {
                if (EditorUtility.DisplayDialog("Reset Terrain?", "This will wipe current terrain density back to flat baseline. Continue?", "Yes", "No")) {
                    world.InitializeWorld(force: true);
                    world.SaveToDisk();
                }
            }

            /*EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Lighting & GI", EditorStyles.boldLabel);
            if (GUILayout.Button("Generate Lightmap UVs (Prepare for Bake)")) {
                world.PrepareForLightmapBake();
            }*/

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            /*
            GUI.backgroundColor = new Color(0.4f, 1f, 0.4f);
            if (GUILayout.Button("Unlock / Resume Sculpting", GUILayout.Height(28))) {
                world.UnlockForEditing();
            }
            */
            GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
            if (GUILayout.Button("Bake Meshes For Build", GUILayout.Height(28))) {
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
            EditorGUILayout.LabelField("Terrain Material & Shader", EditorStyles.boldLabel);

            Material currentMat = GetTargetMaterial();

            EditorGUI.BeginChangeCheck();
            Material assignedMat = (Material)EditorGUILayout.ObjectField("Active Material", currentMat, typeof(Material), false);
            if (EditorGUI.EndChangeCheck()) {
                SetTargetMaterial(assignedMat);
                RefreshCompatibleMaterials();
                currentMat = assignedMat;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Create New Material", GUILayout.Height(22))) {
                CreateNewMaterial(currentMat);
                currentMat = GetTargetMaterial();
            }
            if (GUILayout.Button("Refresh List", GUILayout.Width(90), GUILayout.Height(22))) {
                RefreshCompatibleMaterials();
            }
            EditorGUILayout.EndHorizontal();

            if (currentMat != null && currentMat.shader != null) {
                if (compatibleMaterials.Length > 0) {
                    int newIndex = EditorGUILayout.Popup("Same Shader Mats", selectedMaterialIndex, compatibleMaterialNames);
                    if (newIndex != selectedMaterialIndex && newIndex >= 0 && newIndex < compatibleMaterials.Length) {
                        selectedMaterialIndex = newIndex;
                        SetTargetMaterial(compatibleMaterials[newIndex]);
                        GUI.FocusControl(null);
                    }
                }
                else {
                    EditorGUILayout.HelpBox("No other materials in the project share this shader.", MessageType.None);
                }

                EditorGUILayout.Space(6);
                DrawInlineTextureSlots(currentMat);
            }
            else {
                EditorGUILayout.HelpBox("Assign or create a material to view texture slots and shader options.", MessageType.Warning);
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
                : Shader.Find("Custom/URP_VoxelTriplanarSplat") ?? Shader.Find("Standard");

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
                    names.Add($"{m.name} ({Path.GetFileName(path)})");
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
            switch (layerIndex) {
                case 0: return new Color(1f, 0f, 0f, 0f); // Rock (R)
                case 1: return new Color(0f, 1f, 0f, 0f); // Grass (G)
                case 2: return new Color(0f, 0f, 1f, 0f); // Dirt (B)
                case 3: return new Color(0f, 0f, 0f, 1f); // Cobble (A)
                default: return new Color(1f, 0f, 0f, 0f);
            }
        }

        #endregion

        #region Scene Painting & Sculpting

        private void OnSceneGUI() {
            Event e = Event.current;

            SceneView sceneView = SceneView.currentDrawingSceneView;
            if (sceneView != null) {
                sceneView.wantsMouseMove = true;
                if (e.type == EventType.MouseMove)
                    sceneView.Repaint();
            }

            if (!world.isEditingMode) { return; }

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit)) {
                if (hit.collider.transform.IsChildOf(world.transform)) {
                    if (activeTool == ToolMode.Paint)
                        Handles.color = Color.magenta;
                    else if (e.control)
                        Handles.color = Color.cyan;
                    else if (e.shift)
                        Handles.color = Color.red;
                    else
                        Handles.color = Color.green;

                    Handles.DrawWireDisc(hit.point, hit.normal, brushRadius * world.voxelScale);
                    Handles.ArrowHandleCap(
                        0,
                        hit.point,
                        Quaternion.LookRotation(hit.normal),
                        1f,
                        EventType.Repaint
                    );

                    HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

                    if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) {
                        if (e.button == 0) {
                            if (activeTool == ToolMode.Paint) {
                                world.Paint(hit.point, brushRadius, brushStrength, GetTargetColor(selectedLayerIndex));
                            }
                            else {
                                if (e.control)
                                    world.Smooth(hit.point, brushRadius, brushStrength);
                                else
                                    world.Sculpt(hit.point, brushRadius, brushStrength * (e.shift ? -1f : 1f));
                            }
                            isDirty = true;
                            e.Use();
                        }
                    }
                }
            }
        }

        #endregion

        public class VoxelTerrainAutoSaver : UnityEditor.AssetModificationProcessor {
            private static string[] OnWillSaveAssets(string[] paths) {
                // When scene is saved, auto-write all voxel worlds to their .bytes files
                VoxelWorld[] worlds = Object.FindObjectsOfType<VoxelWorld>();
                foreach (var w in worlds) {
                    if (w != null) {
                        w.SaveToDisk();
                    }
                }
                return paths;
            }
        }
    } 
}