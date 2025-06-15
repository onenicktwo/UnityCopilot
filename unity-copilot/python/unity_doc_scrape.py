import re, json, requests, pathlib, tqdm, numpy as np, faiss
from bs4 import BeautifulSoup
from sentence_transformers import SentenceTransformer

BASE   = "https://docs.unity3d.com/ScriptReference/"
TYPES  = ["Rigidbody","BoxCollider","MonoBehaviour","AudioSource",
          "Transform","MeshRenderer","Animator","Collider","Vector3"]

OUT_DIR    = pathlib.Path(__file__).parent
IDX_PATH   = OUT_DIR / "unity_docs.index"
JSON_PATH  = OUT_DIR / "unity_docs.json"
HEADERS    = {"User-Agent":"Mozilla/5.0 (unity-copilot)"}
embed      = SentenceTransformer("all-MiniLM-L6-v2")

chunks, metas, vecs = [], [], []

def fetch(url: str) -> BeautifulSoup:
    html = requests.get(url, headers=HEADERS, timeout=30).text
    return BeautifulSoup(html, "html.parser")

def member_links(cls: str) -> list[str]:
    soup  = fetch(f"{BASE}{cls}.html")
    nav   = soup.select_one("#nav") or soup
    return [BASE + a["href"] for a in nav.select(f'a[href^="{cls}."]')]

for t in tqdm.tqdm(TYPES):
    pages = [f"{BASE}{t}.html"] + member_links(t)
    for url in pages:
        soup  = fetch(url)
        body  = soup.select_one("#content-area") or soup
        paras = [p.get_text(" ", strip=True) for p in body.find_all("p")]
        for para in paras:
            if len(para.split()) < 15:
                continue
            para = para[:1000]
            chunks.append(para)
            metas.append({"type": t, "url": url})
            vecs.append(embed.encode(para))

print(f"📝 {len(chunks)} text chunks scraped")

index = faiss.IndexFlatIP(384)
index.add(np.asarray(vecs, dtype="float32"))
faiss.write_index(index, str(IDX_PATH))
json.dump({"chunks":chunks,"metas":metas}, open(JSON_PATH,"w"))
print("✅ done")