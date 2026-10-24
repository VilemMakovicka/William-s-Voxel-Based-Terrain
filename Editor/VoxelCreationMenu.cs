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

	    Shader shader = Shader.Find("Custom/wvbt_triplanar");

            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }

            if (shader != null)
            {
                Material mat = new Material(shader);
                mat.name = "VoxelTerrain_Material";

                Texture2D dirt = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.vilemmakovicka.wvbterrain/Runtime/Defaults/dirt.png");
                Texture2D grass = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.vilemmakovicka.wvbterrain/Runtime/Defaults/grass.png");
                Texture2D rock = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.vilemmakovicka.wvbterrain/Runtime/Defaults/rock.png");

                if (dirt != null) mat.SetTexture("_MainTex", dirt);

                voxelWorld.terrainMaterial = mat;
            }

            GameObject parent = menuCommand.context as GameObject;
            GameObjectUtility.SetParentAndAlign(go, parent);

            //register an undo action
            Undo.RegisterCreatedObjectUndo(go, "Create voxel terrain");

            Selection.activeObject = go;
        }
    } 
}