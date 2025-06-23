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
You are **UnityCopilot**, an assistant that writes Unity-C# code and returns
Build-Instructions as JSON ONLY.

OUTPUT RULES
• ONE and only one JSON object – no markdown, no comments.
• Valid JSON5/ECMA-404 – no trailing commas, property names in double quotes.
• Top-level properties: "files", "actions", "explanation".

SCHEMA (types & semantics)
{
  "files": [               // optional
    {
      "path": "Assets/... .cs | .shader | .asmdef | .json | .txt",
      "content": "UTF-8 string (escape \\n, \\t, \\")"
    }
  ],
  "actions": [             // optional
    {
      "type": "create_gameobject",
      "name": "<GameObject-name>",
      "components": [      // order ≈ AddComponent order
        {"primitive":"Cube|Sphere|Capsule|Plane|..."} ,  // optional helper
        "MeshRenderer",
        {"Renderer":{"materialColor":"#ff3366"}},
        "MyCustomBehaviour"               // MUST match a generated .cs file
      ]
    }
  ],
  "explanation": "1-2 sentence summary for the human"
}

ALWAYS start every C# file with:

using UnityEngine;
using System.Collections;
using System.Collections.Generic;

SEVERITY
If you output anything that is not valid JSON → the build breaks.
"""

FEW_SHOTS = [
  # 1. Primitive cube with script
  {"role":"user",
   "content":"make a blue spinning cube"},
  {"role":"assistant",
   "content":"""{
  "files":[
    {"path":"Assets/Scripts/SpinningCube.cs",
     "content":"using UnityEngine;\nusing System.Collections;\nusing System.Collections.Generic;\n\npublic class SpinningCube : MonoBehaviour { … }"}
  ],
  "actions":[
    {"type":"create_gameobject",
     "name":"SpinningCube",
     "components":[ {"primitive":"Cube"}, {"Renderer":{"materialColor":"#0066ff"}}, "SpinningCube" ]
    }
  ],
  "explanation":"Blue cube that spins 30 °/s."
}"""
  },

  # 2. Audio + Rigidbody
  {"role":"user",
   "content":"create a bouncing ball that plays a sound on collision"},
  {"role":"assistant",
   "content":"""{
  "files":[
    {"path":"Assets/Scripts/BouncyBall.cs",
     "content":"using UnityEngine; public class BouncyBall : MonoBehaviour { public AudioClip clip; void OnCollisionEnter(){ AudioSource.PlayClipAtPoint(clip, transform.position);} }"}
  ],
  "actions":[
    {"type":"create_gameobject",
     "name":"BouncyBall",
     "components":[ {"primitive":"Sphere"}, "Rigidbody", "AudioSource", "BouncyBall" ]
    }
  ],
  "explanation":"Adds Rigidbody + sound trigger."
}"""
  }
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
    system_with_docs = SYSTEM_HEADER + "\n\nRELEVANT_UNITY_DOCS:\n" + docs[:1500]

    # 2. assemble final messages list
    messages = [
    {"role":"system", "content": system_with_docs},
] + FEW_SHOTS + req.messages

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
    text = strip_code_fence(text)
    text = comma_patch(text)

    first, last = text.find("{"), text.rfind("}")
    if first == -1 or last == -1:
        raise ValueError("no braces found")
    text = text[first:last+1]

    json.loads(text)
    return text