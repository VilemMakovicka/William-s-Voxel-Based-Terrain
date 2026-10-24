using UnityEditor;
using UnityEngine;

namespace vilemmakovicka.wvbterrain {
    public static class VoxelWorldCreationMenu {
        /// <summary>
        /// - Defines the option of creating a voxel terrain inside the GameObject dropdown
        /// </summary>
        [MenuItem("GameObject/3D Object/Voxel Terrain", false, 15)]
        public static void CreateVoxelWorld(MenuCommand menuCommand) {
            GameObject go = new GameObject("Voxel Terrain");
            VoxelWorld voxelWorld = go.AddComponent<VoxelWorld>();

	    Material defaultMat = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.vilemmakovicka.wvbterrain/Runtime/Defaults/wvbt_default.mat");
            if (defaultMat != null) {
                voxelWorld.terrainMaterial = defaultMat;
            }

            GameObject parent = menuCommand.context as GameObject;
            GameObjectUtility.SetParentAndAlign(go, parent);

            //register an undo action
            Undo.RegisterCreatedObjectUndo(go, "Create voxel terrain");

            Selection.activeObject = go;
        }
    } 
}