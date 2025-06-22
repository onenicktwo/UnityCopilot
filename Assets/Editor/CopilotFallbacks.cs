#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

static class CopilotFallbacks
{
    private static Material _defaultMat;

    public static void EnsureDefaultMaterial(Renderer r)
    {
        if (r.sharedMaterial != null) return;

#if UNITY_EDITOR   // Load only in the Editor; runtime doesn’t need this
        if (_defaultMat == null)
            _defaultMat = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
        r.sharedMaterial = _defaultMat;
#else
        r.sharedMaterial = new Material(Shader.Find("Standard"));
#endif
    }
}

