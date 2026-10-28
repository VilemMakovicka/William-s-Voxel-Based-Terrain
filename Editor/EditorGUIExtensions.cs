using UnityEngine;
using UnityEditor;

public class EditorGUIExtensions : MonoBehaviour {
    /// <summary>
    /// Slider that increases faster as it moves right.
    /// </summary>
    public static float LogSliderWithField(string label, float value, float min, float max, float power = 3f) {
        min = Mathf.Max(0.0001f, min);
        value = Mathf.Clamp(value, min, max);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel(label);

        // 1. Compute non-linear position on the track (0..1)
        float normalized = Mathf.Clamp01((value - min) / (max - min));
        float t = Mathf.Pow(normalized, 1f / power);

        // 2. Draw track
        EditorGUI.BeginChangeCheck();
        t = GUILayout.HorizontalSlider(t, 0f, 1f);
        if (EditorGUI.EndChangeCheck()) {
            value = min + Mathf.Pow(t, power) * (max - min);
        }

        // 3. Draw readable numeric field showing the actual value
        value = EditorGUILayout.FloatField(GUIContent.none, value, GUILayout.Width(60));
        value = Mathf.Clamp(value, min, max);

        EditorGUILayout.EndHorizontal();
        return value;
    }
}
