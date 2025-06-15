import faiss, json, numpy as np
from sentence_transformers import SentenceTransformer
from pathlib import Path

ROOT      = Path(__file__).parent
IDX_PATH  = ROOT / "unity_docs.index"
JSON_PATH = ROOT / "unity_docs.json"

embed_model = SentenceTransformer("all-MiniLM-L6-v2")
faiss_index = faiss.read_index(str(IDX_PATH))
store       = json.load(open(JSON_PATH))

def retrieve(query: str, k: int = 4) -> list[str]:
    """Return up to k relevant doc chunks (plain strings)."""
    qv = embed_model.encode(query).astype("float32")[None, :]
    _, I = faiss_index.search(qv, k)
    return [store["chunks"][idx] for idx in I[0]]