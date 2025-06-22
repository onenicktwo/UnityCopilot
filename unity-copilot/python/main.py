from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import requests, os
from python.unity_docs import retrieve
import json, re

# Model / Ollama settings
MODEL      = os.getenv("OLLAMA_MODEL", "codellama:13b-instruct-q4_K_M")
OLLAMA_URL = os.getenv("OLLAMA_URL",  "http://localhost:11434/api/chat")
CODE_FENCE_RE = re.compile(r"^```(?:json)?\s*([\s\S]*?)```$", re.MULTILINE)

# Prompt pieces
SYSTEM_HEADER = """\
You are UnityCopilot, a Unity-specific coding assistant.
RULES:
You MUST return exactly one valid JSON object and NOTHING else. 
No markdown, no comments, no trailing characters.
Property names exactly: "files", "actions", "explanation".
SCHEMA:
{
  "files":[{"path":"<string>","content":"<string>"}],
  "actions":[{"type":"create_gameobject","name":"<string>","components":[ ... ]}],
  "explanation":"<string>"
}

Example of a Default Unity Script:
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Test : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
Example of user input and what your output should be:
"""

FEW_SHOTS = [
  { "role":"user",
    "content":"make a blue spinning cube" },
  { "role":"assistant",
    "content": r'''{
  "files":[
    {"path":"Assets/Scripts/SpinningCube.cs",
     "content":"using UnityEngine;\nusing System.Collections;\n\npublic class SpinningCube : MonoBehaviour {\n  private float speed = 30f;\n  private IEnumerator Start() {\n    float t = 0f;\n    while (true) {\n      bool reverse = t >= 5f;\n      transform.Rotate(Vector3.up, (reverse?-speed:speed) * Time.deltaTime);\n      t += Time.deltaTime;\n      yield return null;\n    }\n  }\n}"}],
  "actions":[
    {"type":"create_gameobject",
     "name":"SpinningCube",
     "components":[
       {"primitive":"Cube"},
       {"Renderer":{"materialColor":"#0066ff"}},
       "SpinningCube"
     ]}],
  "explanation":"Creates a blue cube and rotates it 30°/s, reversing after 5 s."
}''' }
]

# FastAPI boilerplate
app = FastAPI(title="Unity Copilot LLM Shim", version="0.3.0")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"], allow_methods=["POST","GET"], allow_headers=["*"]
)

class ChatRequest(BaseModel):
    messages: list[dict]   # [{"role":"user","content":"…"}]
    max_tokens: int | None = 512

@app.get("/health")
def health(): return {"ok": True}

@app.post("/chat")
def chat(req: ChatRequest):
    # 1. retrieve docs for last user message
    docs = "\n---\n".join(retrieve(req.messages[-1]["content"]))
    system_with_docs = SYSTEM_HEADER + "\nRELEVANT_UNITY_DOCS:\n" + docs[:1500]

    # 2. assemble final messages list
    messages = [{"role":"system","content": system_with_docs}] + FEW_SHOTS + req.messages

    payload = {
        "model": MODEL,
        "messages": messages,
        "stream": False,
        "options": {
            "format":"json",
            "temperature":0.1,
            "num_predict": req.max_tokens or 512}
    }

    try:
        r = requests.post(OLLAMA_URL, json=payload, timeout=120)
        r.raise_for_status()
        raw = r.json()["message"]["content"]
        try:
            cleaned = validated_json(raw)
        except ValueError as e:
            cleaned = raw
            print("⚠️  JSON fix-up failed:", e)
        return {"content": cleaned}
    except requests.RequestException as e:
        raise HTTPException(status_code=503, detail=f"Ollama error: {e}")
    
def strip_code_fence(text: str) -> str:
    m = CODE_FENCE_RE.search(text.strip())
    return m.group(1) if m else text

def comma_patch(text: str) -> str:
    text = re.sub(r"}\s*\"(?=(files|actions|explanation)\")", r"},\"", text)
    text = re.sub(r",\s*([\]}])", r"\1", text)
    return text

def validated_json(text: str) -> str:
    """Return a *valid* JSON string or raise ValueError."""
    text = strip_code_fence(text)
    text = comma_patch(text)

    first, last = text.find("{"), text.rfind("}")
    if first == -1 or last == -1:
        raise ValueError("no braces found")
    text = text[first:last+1]

    json.loads(text)
    return text