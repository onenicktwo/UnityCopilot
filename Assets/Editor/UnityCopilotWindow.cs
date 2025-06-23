// Unity 2021/2022 compatible – requires the Newtonsoft Json package
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using CAssembly = UnityEditor.Compilation.Assembly;

public class UnityCopilotWindow : EditorWindow
{
    private const string KEY = "CopilotPendingActions";

    private string _prompt = "Ask Copilot…";
    private string _explanation = "";
    private string _raw = "";
    private Vector2 _scroll;

    [MenuItem("Window/Unity Copilot")]
    public static void Open() => GetWindow<UnityCopilotWindow>("Copilot");

    public static string FormatCSharp(string code)
    {
        try
        {
            return code;
        }
        catch
        {
            return code;
        }
    }

    private static void RunActions(IEnumerable<ActionRequest> actions)
    {
        ExecuteActions(actions);
    }

    private void OnGUI()
    {
        GUILayout.Label("Unity Copilot", EditorStyles.boldLabel);
        _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.Height(60));

        if (GUILayout.Button("Generate") && !string.IsNullOrWhiteSpace(_prompt))
            _ = HandlePrompt(_prompt);

        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(260));
        EditorGUILayout.SelectableLabel(_raw, EditorStyles.wordWrappedLabel, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        if (GUILayout.Button("Copy raw"))
            EditorGUIUtility.systemCopyBuffer = _raw;

        GUILayout.Space(6);
        EditorGUILayout.HelpBox(_explanation, MessageType.Info);
    }

    // data contracts
    private class GenFile { public string path; public string content; }
    private class ActionRequest { public string type; public string name; public JToken components; }
    private class LlmReply
    {
        public List<GenFile> files = new List<GenFile>();
        public List<ActionRequest> actions = new List<ActionRequest>();
        public string explanation = "";
    }

    // main flow
    private async Task HandlePrompt(string userPrompt)
    {
        _explanation = "Waiting for model…";
        Repaint();

        string llmJson = await CallLlm(userPrompt);
        _raw = llmJson;

        llmJson = FirstJsonObject(llmJson);
        llmJson = QuotePropertyNames(llmJson);

        LlmReply reply;
        try { reply = JsonConvert.DeserializeObject<LlmReply>(llmJson); }
        catch (Exception ex)
        { _explanation = "JSON parse error: " + ex.Message; Repaint(); return; }

        WriteFiles(reply.files);

        _explanation = reply.explanation;
        Repaint();

        CompilationPipeline.compilationFinished += OnCompileFinished;
        SessionState.SetString(KEY, JsonConvert.SerializeObject(reply.actions));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        void OnCompileFinished(object _)
        {
            CompilationPipeline.compilationFinished -= OnCompileFinished;
            AssemblyReloadEvents.afterAssemblyReload += AfterReload;
        }

        void AfterReload()
        {
            AssemblyReloadEvents.afterAssemblyReload -= AfterReload;
            ExecuteActions(reply.actions);
        }
    }

    // HTTP
    private static async Task<string> CallLlm(string userPrompt)
    {
        using var http = new HttpClient();
        var body = JsonConvert.SerializeObject(new
        {
            messages = new object[]
            {
                new { role = "system", content = "You are UnityCopilot. Reply with JSON (files, actions, explanation)." },
                new { role = "user",   content = userPrompt }
            }
        });
        var resp = await http.PostAsync(
            "http://127.0.0.1:8000/chat",
            new StringContent(body, Encoding.UTF8, "application/json"));

        resp.EnsureSuccessStatusCode();
        var wrapper = JsonConvert.DeserializeObject<JObject>(await resp.Content.ReadAsStringAsync());
        return wrapper?["content"]?.ToString() ?? "";
    }

    // file I/O
    private static void WriteFiles(IEnumerable<GenFile> files)
    {
        foreach (var f in files ?? Enumerable.Empty<GenFile>())
        {
            if (string.IsNullOrWhiteSpace(f?.path) ||
                string.IsNullOrWhiteSpace(f?.content))
                continue;

            var fullPath = f.path.StartsWith("Assets/")
                         ? f.path
                         : Path.Combine("Assets", f.path);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath,
                RoslynFormatter.FormatCSharp(
                    CopilotFallbacks.EnsureUnityUsings(f.content)));
            Debug.Log("Wrote " + fullPath);
        }
    }

    // scene actions
    private static void ExecuteActions(IEnumerable<ActionRequest> actions)
    {
        foreach (var a in actions ?? Enumerable.Empty<ActionRequest>())
        {
            if (a == null || a.type != "create_gameobject") continue;
            MakeGameObject(a);
        }
    }

    private static void MakeGameObject(ActionRequest a)
    {
        // 1. Decide how to create the GO
        GameObject go = null;

        // Caller may specify { "primitive":"Cube" } in the *action* object
        string primitive = (a?.components ??
                            Enumerable.Empty<JToken>())
                           .OfType<JObject>()
                           .FirstOrDefault(o => o["primitive"] != null)?
                           .Value<string>("primitive");

        if (!string.IsNullOrEmpty(primitive) &&
            Enum.TryParse<PrimitiveType>(primitive, true, out var primType))
        {
            go = GameObject.CreatePrimitive(primType);
        }
        else
        {
            go = new GameObject(string.IsNullOrEmpty(a?.name) ? "NewGameObject" : a.name);
        }

        // Keep the requested name (primitive gives default names)
        if (!string.IsNullOrEmpty(a?.name)) go.name = a.name;

        // 2. Add / configure components
        foreach (var token in a?.components ?? Enumerable.Empty<JToken>())
        {
            if (token.Type == JTokenType.String)
            {
                AddComponentByName(go, token.ToString());
            }
            else if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;

                // Skip the helper { "primitive":"Cube" } object
                if (obj.ContainsKey("primitive")) continue;

                foreach (var prop in obj)
                {
                    Component c = AddComponentByName(go, prop.Key);

                    // Example: { "Renderer": { "materialColor":"#ff0000" } }
                    if (c is Renderer r &&
                        prop.Value?["materialColor"] != null &&
                        ColorUtility.TryParseHtmlString(
                            prop.Value["materialColor"].ToString(), out Color col))
                    {
                        var mat = new Material(Shader.Find("Standard")) { color = col };

                        // write material to disk
                        const string folder = "Assets/Materials";
                        if (!AssetDatabase.IsValidFolder(folder))
                            AssetDatabase.CreateFolder("Assets", "Materials");

                        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{go.name}_Mat.mat");
                        AssetDatabase.CreateAsset(mat, path);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                        r.sharedMaterial = mat;
                    }
                }
            }
        }

        // 3. Ensure visible mesh when user asks for renderer only
        bool hasRenderer = go.GetComponent<MeshRenderer>();
        bool hasFilter = go.GetComponent<MeshFilter>();

        if (hasRenderer && !hasFilter)
        {
            var mf = Undo.AddComponent<MeshFilter>(go);
            mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        }
        else if (hasFilter && !hasRenderer)
        {
            Undo.AddComponent<MeshRenderer>(go);
        }

        // 4.  Give a material if still none
        if (go.TryGetComponent<Renderer>(out var rend))
            CopilotFallbacks.EnsureDefaultMaterial(rend);

        // 5. Finalise
        Undo.RegisterCreatedObjectUndo(go, "Copilot create GameObject");
        Selection.activeObject = go;
    }

    private static Component AddComponentByName(GameObject go, string typeName)
    {
        // 1. Try “short” name first (works for built-ins)
        Type t = AppDomain.CurrentDomain.GetAssemblies()
                      .SelectMany(a => a.GetTypes())
                      .FirstOrDefault(x => x.Name == typeName);

        // 2. Try fully-qualified name (if user already wrote it)
        t ??= Type.GetType(typeName, false, true);

        // 3. Unity's "GetBuiltinType" helper
#if UNITY_EDITOR
        t ??= UnityEditor.TypeCache.GetTypesDerivedFrom<Component>()
                      .FirstOrDefault(x => x.Name == typeName || x.FullName == typeName);
#endif

        if (t == null) { Debug.LogWarning($"Component '{typeName}' not found"); return null; }
        var existing = go.GetComponent(t);
        if (existing) return existing;

        return Undo.AddComponent(go, t);
    }

    private static string FirstJsonObject(string text)
    {
        int depth = 0, start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start != -1)
                    return text.Substring(start, i - start + 1);
            }
        }
        // fallback: whole string (will still throw, but the error is clearer)
        return text;
    }
    private static string QuotePropertyNames(string json)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            json, @"(?<=[,{]\s*)([A-Za-z_][A-Za-z0-9_]*)(?=\s*:)", "\"$1\"");
    }

    [InitializeOnLoadMethod]
    static void AfterScriptsReloaded()
    {
        var json = SessionState.GetString(KEY, null);
        if (string.IsNullOrEmpty(json)) return;
        SessionState.EraseString(KEY);

        // delay one tick so TypeCache is warm
        EditorApplication.delayCall += () =>
        {
            if (HasCompileErrors())
            {
                Debug.LogError("Copilot: compile errors detected – actions skipped");
                return;
            }

            try
            {
                var actions = JsonConvert.DeserializeObject<List<ActionRequest>>(json);
                ExecuteActions(actions);
            }
            catch (Exception ex)
            {
                Debug.LogError("Copilot: failed to replay actions\n" + ex);
            }
        };
    }

    static bool HasCompileErrors()
    {
        // 1. Try the modern property (2022.2+)
        foreach (var asm in UnityEditor.Compilation.CompilationPipeline.GetAssemblies())
        {
            var prop = asm.GetType().GetProperty("compilerMessages");
            if (prop == null) continue;

            var msgs = prop.GetValue(asm) as UnityEditor.Compilation.CompilerMessage[];
            if (msgs != null && msgs.Any(m =>
                     m.type == UnityEditor.Compilation.CompilerMessageType.Error))
                return true;
        }

        // 2. Legacy fallback (2021 / 2022.1) – scan Console log
        System.Type logEntries = System.Type.GetType("UnityEditor.LogEntries, UnityEditor.dll");
        if (logEntries == null) return false;

        int count = (int)logEntries.GetMethod("GetCount").Invoke(null, null);
        var entry = System.Activator.CreateInstance(
                       System.Type.GetType("UnityEditor.LogEntry, UnityEditor.dll"));

        var start = logEntries.GetMethod("StartGettingEntries");
        var end = logEntries.GetMethod("EndGettingEntries");
        var get = logEntries.GetMethod("GetEntryInternal");

        start.Invoke(null, null);
        bool hasError = false;
        for (int i = 0; i < count && !hasError; i++)
        {
            get.Invoke(null, new object[] { i, entry });
            // LogEntry.mode bit-flag: 2 == error
            int mode = (int)entry.GetType().GetField("mode").GetValue(entry);
            hasError = (mode & 2) != 0;
        }
        end.Invoke(null, null);
        return hasError;
    }
}
