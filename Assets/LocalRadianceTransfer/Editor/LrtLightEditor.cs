using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

[CustomEditor(typeof(LrtLight))]
[CanEditMultipleObjects]
internal sealed class LrtLightEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("useUnityLight"));
        bool syncChanged = serializedObject.ApplyModifiedProperties();

        bool allSynced = true;
        foreach (LrtLight light in targets)
            allSynced &= light.TryGetUnityLight(out _);

        if (allSynced)
        {
            EditorGUILayout.HelpBox("Shape, color, intensity, range, and enabled state follow the Unity Light on this GameObject.", MessageType.Info);
        }
        else
        {
            SerializedProperty shape = serializedObject.FindProperty("shape");
            EditorGUILayout.PropertyField(shape);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("color"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("intensity"));
            if (shape.hasMultipleDifferentValues || shape.intValue != (int)LrtLight.LightShape.Directional)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("range"));
            if (shape.hasMultipleDifferentValues || shape.intValue == (int)LrtLight.LightShape.Area)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("size"));
            if (shape.hasMultipleDifferentValues || shape.intValue == (int)LrtLight.LightShape.Spot)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("spotAngle"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("innerSpotAngle"));
            }
        }
        EditorGUILayout.PropertyField(serializedObject.FindProperty("indirectMultiplier"));
        if (serializedObject.ApplyModifiedProperties() || syncChanged)
            EditorApplication.QueuePlayerLoopUpdate();
    }
}

internal sealed class LrtLightBuildProcessor : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (report == null)
            return;

        // Unity exposes Light.areaSize only in the editor. Store the size in the build's scene copy.
        foreach (GameObject root in scene.GetRootGameObjects())
        foreach (LrtLight light in root.GetComponentsInChildren<LrtLight>(true))
        {
            if (!light.TryGetUnityLight(out Light source) || source.type != LightType.Rectangle)
                continue;

            var settings = new SerializedObject(light);
            settings.FindProperty("size").vector2Value = Vector2.Max(source.areaSize, Vector2.one * 0.01f);
            settings.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
