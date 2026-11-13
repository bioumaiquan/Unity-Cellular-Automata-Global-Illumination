using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

[CustomEditor(typeof(LrtTrunkScene))]
internal sealed class LrtSceneBaking : Editor
{
    private const string Folder = "Assets/LocalRadianceTransfer";
    private const string DemoSource = "Assets/Z ! Voxel BlocksAssets/Demo/Demo_D.unity";
    private const string DemoScene = "Assets/Scenes/Demo_D_LRT.unity";

    private sealed class BakeGroup
    {
        public string Key;
        public string PrefabPath;
        public string AssetName;
        public readonly List<LrtColorSdfBaker.Source> Sources = new List<LrtColorSdfBaker.Source>();
        public LrtColorSdf Data;
    }

    public override bool RequiresConstantRepaint() => true;

    public override void OnInspectorGUI()
    {
        if (DrawDefaultInspector())
            EditorApplication.QueuePlayerLoopUpdate();
        var trunks = (LrtTrunkScene)target;
        EditorGUILayout.LabelField("Probe Limit", LocalRadianceTransferDemo.MaxProbeCount.ToString("N0"));
        var controller = trunks.GetComponent<LocalRadianceTransferDemo>();
        if (controller != null)
        {
            Vector3Int requested = controller.RequestedGridDimensions;
            if (requested != Vector3Int.zero)
            {
                long cells = (long)requested.x * requested.y * requested.z;
                EditorGUILayout.LabelField(controller.UsesMixedProbeDensity ? "Geometry Grid" : "Requested Grid",
                    $"{requested.x} × {requested.y} × {requested.z}");
                if (controller.UsesMixedProbeDensity)
                {
                    EditorGUILayout.LabelField("Fine Probes", controller.ActiveFineProbeCount.ToString("N0"));
                    EditorGUILayout.LabelField("Coarse Probes", controller.ActiveCoarseProbeCount.ToString("N0"));
                }
                else
                    EditorGUILayout.LabelField("Requested Probes", cells.ToString("N0"));
            }
            if (controller.GridLimitWarning != null)
                EditorGUILayout.HelpBox(controller.GridLimitWarning, MessageType.Warning);
        }
        if (trunks.TrunkCount > 0)
        {
            Vector3Int size = trunks.TrunkDimensions;
            EditorGUILayout.LabelField("Active Scene Trunks", $"{size.x} × {size.y} × {size.z} ({trunks.TrunkCount})");
        }
        EditorGUILayout.LabelField("Baked instances", trunks.BakedInstanceCount.ToString());
        using (new EditorGUI.DisabledScope(Application.isPlaying))
            if (GUILayout.Button("Bake Color SDF"))
                Bake(trunks.gameObject.scene, trunks);
    }

    [MenuItem("Tools/Local Radiance Transfer/Bake Active Scene Color SDF")]
    private static void BakeActiveScene()
    {
        Bake(SceneManager.GetActiveScene(), null);
    }

    [MenuItem("Tools/Local Radiance Transfer/Bake Active Scene Color SDF", true)]
    private static bool CanBakeActiveScene() => !Application.isPlaying && !string.IsNullOrEmpty(SceneManager.GetActiveScene().path);

    // Also callable with -executeMethod LrtSceneBaking.BuildDemo for a fresh checkout.
    [MenuItem("Tools/Local Radiance Transfer/Open or Build Demo_D LRT")]
    public static void BuildDemo()
    {
        if (Application.isPlaying || (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()))
            return;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoScene) != null)
        {
            EditorSceneManager.OpenScene(DemoScene);
            return;
        }

        Scene source = EditorSceneManager.OpenScene(DemoSource);
        if (!EditorSceneManager.SaveScene(source, DemoScene, true))
            throw new IOException("Could not create the Demo_D LRT scene.");
        Scene demo = EditorSceneManager.OpenScene(DemoScene);
        if (!Bake(demo, null))
            throw new InvalidOperationException("Demo_D Color SDF bake did not finish.");
        EditorSceneManager.SaveScene(demo);
    }

    private static bool Bake(Scene scene, LrtTrunkScene requested)
    {
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path) || EditorSceneManager.IsPreviewScene(scene))
        {
            Debug.LogWarning("Save the scene before baking its Color SDF.");
            return false;
        }
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
        {
            Debug.LogError("Color SDF baking needs a graphics device to read material textures.");
            return false;
        }

        LocalRadianceTransferDemo controller = FindController(scene, requested);
        if (requested == null && controller != null)
            requested = controller.GetComponent<LrtTrunkScene>();

        var groups = Collect(scene);
        var unique = new Dictionary<string, BakeGroup>();
        var keys = new Dictionary<Object, string>();
        var baker = new LrtColorSdfBaker();
        int resolution = requested != null ? requested.BakeResolution : 16;
        try
        {
            int done = 0;
            foreach (var entry in groups)
            {
                BakeGroup group = entry.Value;
                group.Key = GetKey(group, resolution, keys);
                if (unique.ContainsKey(group.Key))
                    continue;
                if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar("Bake Color SDF",
                        entry.Key.name, (float)done / Mathf.Max(1, groups.Count)))
                    return false;
                done++;
                group.Data = baker.Bake(group.Sources, resolution);
                unique.Add(group.Key, group);
            }

            string output = EnsureBakeFolder(scene);
            foreach (BakeGroup group in unique.Values)
            {
                if (group.Data == null)
                    continue;
                string path = output + "/" + group.AssetName + "_" + group.Key + ".asset";
                var existing = AssetDatabase.LoadAssetAtPath<LrtColorSdf>(path);
                if (existing == null)
                    AssetDatabase.CreateAsset(group.Data, path);
                else
                {
                    existing.ReplaceBake(group.Data);
                    EditorUtility.SetDirty(existing);
                    Object.DestroyImmediate(group.Data);
                    group.Data = existing;
                }
            }

            var instances = new List<LrtTrunkScene.Instance>();
            Bounds bounds = default;
            foreach (var entry in groups)
            {
                LrtColorSdf data = unique[entry.Value.Key].Data;
                if (data == null)
                    continue;
                Transform root = entry.Key;
                Bounds world = LrtTrunkScene.WorldBounds(root.localToWorldMatrix, data.Bounds);
                if (instances.Count == 0)
                    bounds = world;
                else
                    bounds.Encapsulate(world);
                instances.Add(new LrtTrunkScene.Instance { Root = root, Data = data });
            }

            if (instances.Count == 0)
            {
                Debug.LogWarning("No active opaque triangle meshes were found in the scene.");
                return false;
            }

            if (controller == null && requested == null)
            {
                var go = new GameObject("Local Radiance Transfer");
                SceneManager.MoveGameObjectToScene(go, scene);
                Undo.RegisterCreatedObjectUndo(go, "Add Local Radiance Transfer");
                requested = Undo.AddComponent<LrtTrunkScene>(go);
            }
            if (requested == null)
                requested = controller.GetComponent<LrtTrunkScene>() ?? Undo.AddComponent<LrtTrunkScene>(controller.gameObject);
            if (controller == null)
                controller = Undo.AddComponent<LocalRadianceTransferDemo>(requested.gameObject);

            bool hadBake = requested.BakedInstanceCount > 0;
            Undo.RecordObjects(new Object[] { requested, controller }, "Bake Color SDF");
            requested.SetBake(instances.ToArray());
            ConfigureController(controller, requested, bounds, hadBake, scene.path == DemoScene, instances);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorApplication.QueuePlayerLoopUpdate();
            Debug.Log($"LRT baked {instances.Count} instances from {unique.Count} distinct inputs in {scene.name}. Bounds {bounds}.", requested);
            return true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            foreach (BakeGroup group in unique.Values)
                if (group.Data != null && !EditorUtility.IsPersistent(group.Data))
                    Object.DestroyImmediate(group.Data);
        }
    }

    private static Dictionary<Transform, BakeGroup> Collect(Scene scene)
    {
        var groups = new Dictionary<Transform, BakeGroup>();
        var lowerLods = new HashSet<Renderer>();
        foreach (GameObject root in scene.GetRootGameObjects())
        foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>())
        {
            LOD[] lods = group.GetLODs();
            for (int i = 1; i < lods.Length; i++)
                lowerLods.UnionWith(lods[i].renderers);
            if (lods.Length > 0)
                lowerLods.ExceptWith(lods[0].renderers);
        }

        foreach (GameObject sceneRoot in scene.GetRootGameObjects())
        foreach (MeshRenderer renderer in sceneRoot.GetComponentsInChildren<MeshRenderer>())
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || lowerLods.Contains(renderer) ||
                !renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
                continue;
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length > 0 && !Array.Exists(materials, LrtColorSdfBaker.IsOpaque))
                continue;
            GameObject prefab = PrefabUtility.GetOutermostPrefabInstanceRoot(renderer.gameObject);
            Transform root = prefab != null ? prefab.transform : renderer.transform;
            if (!groups.TryGetValue(root, out BakeGroup group))
            {
                string prefabPath = prefab != null ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefab) : null;
                string name = !string.IsNullOrEmpty(prefabPath) ? Path.GetFileNameWithoutExtension(prefabPath) : filter.sharedMesh.name;
                foreach (char invalid in Path.GetInvalidFileNameChars())
                    name = name.Replace(invalid, '_');
                groups.Add(root, group = new BakeGroup { PrefabPath = prefabPath, AssetName = name });
            }

            Matrix4x4 localToRoot = Matrix4x4.identity;
            for (Transform child = renderer.transform; child != root; child = child.parent)
                localToRoot = Matrix4x4.TRS(child.localPosition, child.localRotation, child.localScale) * localToRoot;
            group.Sources.Add(new LrtColorSdfBaker.Source(filter.sharedMesh, localToRoot, materials));
        }
        return groups;
    }

    private static string GetKey(BakeGroup group, int resolution, Dictionary<Object, string> ids)
    {
        var text = new StringBuilder();
        text.Append("color-sdf-v1:").Append(resolution).Append('|').Append(AssetDatabase.AssetPathToGUID(group.PrefabPath ?? ""));
        foreach (LrtColorSdfBaker.Source source in group.Sources)
        {
            text.Append('|').Append(Id(source.Mesh)).Append(':');
            for (int i = 0; i < 16; i++)
                text.Append(source.LocalToRoot[i].ToString("R", CultureInfo.InvariantCulture)).Append(',');
            foreach (Material material in source.Materials)
                text.Append(Id(material)).Append(',');
        }

        return Hash128.Compute(text.ToString()).ToString();

        string Id(Object asset)
        {
            if (asset == null)
                return "-";
            if (!ids.TryGetValue(asset, out string id))
            {
                id = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long local)
                    ? guid + ":" + local
                    : GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString() + ":" + asset.GetInstanceID();
                ids.Add(asset, id);
            }
            return id;
        }
    }

    private static string EnsureBakeFolder(Scene scene)
    {
        string parent = Folder + "/BakedColorSdf";
        if (!AssetDatabase.IsValidFolder(parent))
            AssetDatabase.CreateFolder(Folder, "BakedColorSdf");
        string name = scene.name + "_" + AssetDatabase.AssetPathToGUID(scene.path).Substring(0, 8);
        string output = parent + "/" + name;
        if (!AssetDatabase.IsValidFolder(output))
            AssetDatabase.CreateFolder(parent, name);
        return output;
    }

    private static LocalRadianceTransferDemo FindController(Scene scene, LrtTrunkScene requested)
    {
        if (requested != null)
            return requested.GetComponent<LocalRadianceTransferDemo>();

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var controller = root.GetComponentInChildren<LocalRadianceTransferDemo>(true);
            if (controller != null)
                return controller;
        }
        return null;
    }

    private static void ConfigureController(LocalRadianceTransferDemo controller, LrtTrunkScene trunks,
        Bounds bounds, bool hadBake, bool demo, List<LrtTrunkScene.Instance> instances)
    {
        bool wasEnabled = controller.enabled;
        controller.enabled = false;
        var settings = new SerializedObject(controller);
        settings.FindProperty("trunkScene").objectReferenceValue = trunks;
        settings.FindProperty("propagation").objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<ComputeShader>(Folder + "/Shaders/LrtPropagation.compute");
        if (!hadBake)
        {
            settings.FindProperty("stepsPerFrame").intValue = 1;
            settings.FindProperty("propagationFrameInterval").intValue = 2;
            settings.FindProperty("propagationIterations").intValue = 64;
        }

        if (demo)
            ConfigureDemoControls(controller, bounds, instances, settings);
        settings.ApplyModifiedPropertiesWithoutUndo();
        controller.enabled = wasEnabled;
    }

    private static void ConfigureDemoControls(LocalRadianceTransferDemo controller, Bounds bounds,
        List<LrtTrunkScene.Instance> instances, SerializedObject settings)
    {
        Transform prop = null;
        float best = float.PositiveInfinity;
        foreach (var instance in instances)
        {
            if (instance.Root.parent == null || instance.Root.parent.name != "Build" ||
                instance.Data.Bounds.size.magnitude < 2f)
                continue;
            float score = (instance.Root.position - bounds.center).sqrMagnitude;
            if (score < best)
            {
                prop = instance.Root;
                best = score;
            }
        }
        settings.FindProperty("demoProp").objectReferenceValue = prop;

        // Strip the obsolete built-in post effects from the copied URP demo camera.
        foreach (GameObject root in controller.gameObject.scene.GetRootGameObjects())
        foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(camera.gameObject);

        Transform existingLamp = controller.transform.Find("LRT Courtyard Lamp");
        Light lamp = existingLamp != null ? existingLamp.GetComponent<Light>() : null;
        if (lamp == null)
        {
            var lightObject = new GameObject("LRT Courtyard Lamp");
            lightObject.transform.SetParent(controller.transform);
            lightObject.transform.position = new Vector3(4.997f, 5.25f, 43.542f);
            lamp = lightObject.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.lightmapBakeType = LightmapBakeType.Realtime;
            lamp.color = new Color(1f, 0.73f, 0.48f);
            lamp.intensity = 6f;
            lamp.range = 5.5f;
            lamp.bounceIntensity = 1.5f;
            Undo.RegisterCreatedObjectUndo(lightObject, "Add LRT courtyard lamp");
        }
        if (!lamp.TryGetComponent<LrtLight>(out _))
            Undo.AddComponent<LrtLight>(lamp.gameObject);
        settings.FindProperty("demoLight").objectReferenceValue = lamp;
    }
}


[CustomEditor(typeof(LocalRadianceTransferDemo))]
internal sealed class LrtControllerInspector : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawPropertiesExcluding(serializedObject, "m_Script");
        if (serializedObject.ApplyModifiedProperties())
            EditorApplication.QueuePlayerLoopUpdate();
    }
}
