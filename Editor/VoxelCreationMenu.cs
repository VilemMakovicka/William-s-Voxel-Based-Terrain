using UnityEditor;
using UnityEngine;

namespace vilemmakovicka.wvbterrain {
    /// <summary>
    /// Adds the button for creating a voxel terrain inside the GameObject dropdown
    /// </summary>
    public static class VoxelWorldCreationMenu {
        /// <summary>
        /// Adds the button for creating a voxel terrain inside the GameObject dropdown
        /// </summary>
        [MenuItem("GameObject/3D Object/Voxel Terrain", false, 15)]
        public static void CreateVoxelWorld(MenuCommand menuCommand) {
            // Create a terrain object
            GameObject go = new GameObject("Voxel Terrain");
            VoxelWorld voxelWorld = go.AddComponent<VoxelWorld>();

            // Assign ithe object's material
	        Material defaultMat = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.vilemmakovicka.wvbterrain/Runtime/Defaults/wvbt_default.mat");
            if (defaultMat != null) { voxelWorld.terrainMaterial = defaultMat; }

            // Set parent
            GameObject parent = menuCommand.context as GameObject;
            GameObjectUtility.SetParentAndAlign(go, parent);

            // Register an undo action
            Undo.RegisterCreatedObjectUndo(go, "Create voxel terrain");

            // Select terrain
            Selection.activeObject = go;
        }
    } 
}