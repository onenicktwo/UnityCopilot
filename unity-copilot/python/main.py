from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import requests, os
from unity_docs import retrieve

# Model / Ollama settings
MODEL      = os.getenv("OLLAMA_MODEL", "codellama:13b-instruct-q4_K_M")
OLLAMA_URL = os.getenv("OLLAMA_URL",  "http://localhost:11434/api/chat")

# Prompt pieces
SYSTEM_HEADER = """\
You are UnityCopilot, a Unity-specific coding assistant.
RULES:
• Reply with ONE valid JSON object, no markdown.
• Property names exactly: "files", "actions", "explanation".
SCHEMA:
{
  "files":[{"path":"<string>","content":"<string>"}],
  "actions":[{"type":"create_gameobject","name":"<string>","components":[ ... ]}],
  "explanation":"<string>"
}
EXAMPLES:
"""

EXAMPLES = [
    # 1) spinning cube
    {"role":"user","content":"create a spinning cube"},
    {"role":"assistant","content":"""{
        "files":[
          {"path":"Assets/Scripts/Spin.cs",
           "content":"using UnityEngine; public class Spin : MonoBehaviour{void Update(){transform.Rotate(0,90*Time.deltaTime,0);}}"}
        ],
        "actions":[
          {"type":"create_gameobject","name":"SpinningCube","components":["Spin","MeshRenderer","BoxCollider"]}
        ],
        "explanation":"Cube rotates 90°/sec around Y and has collider."
    }"""},
    # 2) health bar
    {"role":"user","content":"make me a health bar UI"},
    {"role":"assistant","content":"""{
        "files":[
          {"path":"Assets/Scripts/HealthBar.cs",
           "content":"using UnityEngine.UI; public class HealthBar : MonoBehaviour{public Image fill;public void Set(float v){fill.fillAmount=v;}}"}
        ],
        "actions":[
          {"type":"create_gameobject","name":"Canvas","components":["Canvas","CanvasScaler","GraphicRaycaster","HealthBar"]}
        ],
        "explanation":"Creates world-space canvas with HealthBar script."
    }"""},
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
    messages = [{"role":"system","content": system_with_docs}] + EXAMPLES + req.messages

    payload = {
        "model": MODEL,
        "messages": messages,
        "stream": False,
        "options": {"format":"json","temperature":0.1,"num_predict": req.max_tokens or 512}
    }

    try:
        r = requests.post(OLLAMA_URL, json=payload, timeout=120)
        r.raise_for_status()
        return {"content": r.json()["message"]["content"]}
    except requests.RequestException as e:
        raise HTTPException(status_code=503, detail=f"Ollama error: {e}")