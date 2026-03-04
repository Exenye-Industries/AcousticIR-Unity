using AcousticIR.Materials;
using AcousticIR.Probes;
using AcousticIR.Zones;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor
{
    /// <summary>
    /// Menu items for quick AcousticIR setup in the Unity Editor.
    /// </summary>
    public static class MenuItems
    {
        [MenuItem("GameObject/AcousticIR/Acoustic Probe", false, 10)]
        static void CreateAcousticProbe()
        {
            var go = new GameObject("AcousticProbe");
            go.AddComponent<AcousticProbe>();

            if (Selection.activeTransform != null)
                go.transform.SetParent(Selection.activeTransform);

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Acoustic Probe");
        }

        [MenuItem("GameObject/AcousticIR/Acoustic Listener", false, 12)]
        static void CreateAcousticListener()
        {
            var go = new GameObject("AcousticListener");
            go.AddComponent<AcousticListener>();

            if (Selection.activeTransform != null)
                go.transform.SetParent(Selection.activeTransform);

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Acoustic Listener");
        }

        [MenuItem("GameObject/AcousticIR/Acoustic Zone (Box)", false, 13)]
        static void CreateAcousticZoneBox()
        {
            var go = new GameObject("AcousticZone");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(10f, 5f, 10f);
            go.AddComponent<AcousticZone>();

            if (Selection.activeTransform != null)
                go.transform.SetParent(Selection.activeTransform);

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Acoustic Zone");
        }

        [MenuItem("GameObject/AcousticIR/Acoustic Zone Manager", false, 14)]
        static void CreateAcousticZoneManager()
        {
            var go = new GameObject("AcousticZoneManager");
            go.AddComponent<AcousticZoneManager>();
            go.AddComponent<AcousticZoneBlender>();

            if (Selection.activeTransform != null)
                go.transform.SetParent(Selection.activeTransform);

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Acoustic Zone Manager");
        }

        [MenuItem("GameObject/AcousticIR/Acoustic Source", false, 15)]
        static void CreateAcousticSource()
        {
            if (Selection.activeGameObject != null)
            {
                // Add to selected object if it has an AudioSource
                var go = Selection.activeGameObject;
                if (go.GetComponent<AudioSource>() != null)
                {
                    if (go.GetComponent<AcousticSource>() == null)
                    {
                        Undo.AddComponent<AcousticSource>(go);
                        return;
                    }
                }
            }

            // Create new object with AudioSource + AcousticSource
            var newGo = new GameObject("AcousticSource");
            newGo.AddComponent<AudioSource>();
            newGo.AddComponent<AcousticSource>();

            if (Selection.activeTransform != null)
                newGo.transform.SetParent(Selection.activeTransform);

            Selection.activeGameObject = newGo;
            Undo.RegisterCreatedObjectUndo(newGo, "Create Acoustic Source");
        }

        [MenuItem("GameObject/AcousticIR/Acoustic Surface", false, 16)]
        static void AddAcousticSurface()
        {
            if (Selection.activeGameObject == null)
            {
                EditorUtility.DisplayDialog("AcousticIR",
                    "Select a GameObject with a Collider first.", "OK");
                return;
            }

            var go = Selection.activeGameObject;
            if (go.GetComponent<Collider>() == null)
            {
                EditorUtility.DisplayDialog("AcousticIR",
                    "Selected GameObject needs a Collider component.", "OK");
                return;
            }

            if (go.GetComponent<AcousticSurface>() != null)
            {
                EditorUtility.DisplayDialog("AcousticIR",
                    "AcousticSurface already exists on this GameObject.", "OK");
                return;
            }

            Undo.AddComponent<AcousticSurface>(go);
        }

        [MenuItem("Assets/Create/AcousticIR/Material Preset Set", false, 100)]
        static void CreateAllMaterialPresets()
        {
            string folder = GetSelectedFolder();

            CreatePresetAsset(folder, "Concrete", AcousticMaterialPresets.Concrete);
            CreatePresetAsset(folder, "Brick", AcousticMaterialPresets.Brick);
            CreatePresetAsset(folder, "WoodPanel", AcousticMaterialPresets.WoodPanel);
            CreatePresetAsset(folder, "WoodFloor", AcousticMaterialPresets.WoodFloor);
            CreatePresetAsset(folder, "Glass", AcousticMaterialPresets.Glass);
            CreatePresetAsset(folder, "Carpet", AcousticMaterialPresets.Carpet);
            CreatePresetAsset(folder, "HeavyCurtain", AcousticMaterialPresets.HeavyCurtain);
            CreatePresetAsset(folder, "Metal", AcousticMaterialPresets.Metal);
            CreatePresetAsset(folder, "Plaster", AcousticMaterialPresets.Plaster);
            CreatePresetAsset(folder, "AcousticTile", AcousticMaterialPresets.AcousticTile);
            CreatePresetAsset(folder, "Soil", AcousticMaterialPresets.Soil);
            CreatePresetAsset(folder, "Foliage", AcousticMaterialPresets.Foliage);
            CreatePresetAsset(folder, "OpenAir", AcousticMaterialPresets.OpenAir);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AcousticIR] Created 13 material presets in {folder}");
        }

        static void CreatePresetAsset(string folder, string name, float[] preset)
        {
            var mat = ScriptableObject.CreateInstance<AcousticMaterial>();
            AcousticMaterialPresets.ApplyPreset(mat, preset);
            AssetDatabase.CreateAsset(mat, $"{folder}/AM_{name}.asset");
        }

        static string GetSelectedFolder()
        {
            string path = "Assets";
            foreach (var obj in Selection.GetFiltered<Object>(SelectionMode.Assets))
            {
                path = AssetDatabase.GetAssetPath(obj);
                if (!System.IO.Directory.Exists(path))
                    path = System.IO.Path.GetDirectoryName(path);
                break;
            }
            return path;
        }
    }
}
