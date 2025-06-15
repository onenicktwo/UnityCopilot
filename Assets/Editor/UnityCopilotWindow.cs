// Unity 2021/2022 compatible – requires the Newtonsoft Json package
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

public class UnityCopilotWindow : EditorWindow
{
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

        llmJson = ExtractJsonBlock(llmJson);
        llmJson = QuotePropertyNames(llmJson);

        LlmReply reply;
        try { reply = JsonConvert.DeserializeObject<LlmReply>(llmJson); }
        catch (Exception ex)
        { _explanation = "JSON parse error: " + ex.Message; Repaint(); return; }

        WriteFiles(reply.files);

        _explanation = reply.explanation;
        Repaint();

        void AfterCompile(object _) { ExecuteActions(reply.actions); CompilationPipeline.compilationFinished -= AfterCompile; }
        CompilationPipeline.compilationFinished += AfterCompile;

        AssetDatabase.Refresh();
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
                continue;                    // ignore junk

            Directory.CreateDirectory(Path.GetDirectoryName(f.path) ?? "Assets");
            File.WriteAllText(f.path, RoslynFormatter.FormatCSharp(f.content));
            Debug.Log("Wrote " + f.path);
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

        // 4. Finalise
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
        return Undo.AddComponent(go, t);        // ensures proper undo
    }

    // salvage helpers
    private static string ExtractJsonBlock(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : text;
    }
    private static string QuotePropertyNames(string json)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            json, @"(?<=[,{]\s*)([A-Za-z_][A-Za-z0-9_]*)(?=\s*:)", "\"$1\"");
    }
}