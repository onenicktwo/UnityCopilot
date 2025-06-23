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

    public static string EnsureUnityUsings(string src)
    {
        if (!src.Contains("using System.Collections;"))
            src = "using System.Collections;\n" + src;
        if (!src.Contains("using System.Collections.Generic;"))
            src = "using System.Collections.Generic;\n" + src;
        if (!src.Contains("using UnityEngine;"))
            src = "using UnityEngine;\n" + src;
        return src;
    }
}

