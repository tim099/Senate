# -*- coding: utf-8 -*-
"""
kb_sidecar.py — 知識庫的常駐嵌入程序（TASK-0378，Senate 管理）。

區塊職責：把 BGE-M3 載入**一次**，之後用 localhost HTTP 回 dense＋sparse 向量。
物理意義：舊的 knowledge_base.py 每呼叫一次就重新載入模型（~4.3 秒）—— 常駐之後只付第一次。
數值影響：只讀模型、只回向量；不碰任何檔案（索引讀寫在 Senate 的 C# 那側）。

協定（全部 POST/GET JSON，帶 header `X-Kb-Token`；token 不對一律 403）：
  GET  /health                 → {ok, model, device, fp16, loaded_ms, served}
  POST /embed {texts:[...], sparse:bool, max_length:int}
                               → {dense_b64:"<float32 little-endian, n*dim>", dim, sparse:[{token_id:weight}...]}
  POST /rerank {query:str, passages:[...], max_length:int}
                               → {ok, scores:[0..1 ...]}   （bge-reranker-v2-m3，第一次呼叫才載入；TASK-0382）
  POST /shutdown               → {ok}
啟動：python kb_sidecar.py --port 0 --token <t> --info <寫回 port/pid 的 json 路徑> [--model BAAI/bge-m3] [--idle 1800]
        [--hf-home <模型快取根>] [--log <stderr 落點>]
⚠ Senate 用「脫離行程樹」的方式拉起它（WMI）—— 那條路**不帶呼叫端的環境變數**，所以 HF_HOME 與 log 走參數。
⚠ port 0 ＝ 讓 OS 挑一個空的；實際 port 寫進 --info 檔（寫完才算「起來了」—— Senate 以那個檔為準）。
⚠ 閒置 --idle 秒沒有任何請求就自己退出（不然它會一直佔著 GPU 記憶體，而沒有人記得它還在）。
"""
import argparse, base64, json, os, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ap = argparse.ArgumentParser()
ap.add_argument("--port", type=int, default=0)
ap.add_argument("--token", required=True)
ap.add_argument("--info", required=True)
ap.add_argument("--model", default="BAAI/bge-m3")
ap.add_argument("--rerank-model", default="BAAI/bge-reranker-v2-m3")
ap.add_argument("--idle", type=int, default=1800)
ap.add_argument("--hf-home", default="")
ap.add_argument("--log", default="")
args = ap.parse_args()
if args.log:
    sys.stderr = open(args.log, "a", encoding="utf-8", buffering=1)
    sys.stdout = sys.stderr
if args.hf_home:
    os.environ["HF_HOME"] = args.hf_home          # 一定要在 import FlagEmbedding／huggingface_hub 之前
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
os.environ.setdefault("PYTHONIOENCODING", "utf-8")

t0 = time.time()
from FlagEmbedding import BGEM3FlagModel  # noqa: E402  缺套件就讓它炸：Senate 端會讀 stderr 回「缺相依」
import numpy as np  # noqa: E402
MODEL = BGEM3FlagModel(args.model, use_fp16=True)
LOADED_MS = round((time.time() - t0) * 1000)
try:
    import torch
    DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
except Exception:
    DEVICE = "?"

lock = threading.Lock()          # 模型不保證可重入 ⇒ 一次只算一批
state = {"last": time.time(), "served": 0}
FEATURES = ["embed", "rerank"]   # /health 回報：舊版 sidecar 沒有這一格 ⇒ Senate 端據此判斷要不要重啟（TASK-0382）
RERANKER = {"m": None}           # 重排模型第一次用才載入（多佔 ~1GB 顯存，不用的話不付）


def get_reranker():
    # 不用 FlagEmbedding.FlagReranker：它呼叫 tokenizer.prepare_for_model，新版 transformers 已經沒有（實測 2026-10-03：
    # `XLMRobertaTokenizer has no attribute prepare_for_model`）。bge-reranker-v2-m3 本質是一個序列分類模型 —— 直接載：
    # 成對（query, passage）進去、取單一 logit、sigmoid 成 0..1。
    if RERANKER["m"] is None:
        import torch
        from transformers import AutoModelForSequenceClassification, AutoTokenizer
        tok = AutoTokenizer.from_pretrained(args.rerank_model)
        mdl = AutoModelForSequenceClassification.from_pretrained(args.rerank_model)
        dev = "cuda" if torch.cuda.is_available() else "cpu"
        if dev == "cuda":
            mdl = mdl.half()
        mdl = mdl.to(dev).eval()
        RERANKER["m"] = (tok, mdl, dev)
    return RERANKER["m"]


def rerank_scores(query, passages, max_length=512, batch=16):
    import torch
    tok, mdl, dev = get_reranker()
    out = []
    with torch.no_grad():
        for i in range(0, len(passages), batch):
            chunk = passages[i:i + batch]
            enc = tok([query] * len(chunk), chunk, padding=True, truncation=True, max_length=max_length, return_tensors="pt").to(dev)
            logits = mdl(**enc).logits.view(-1).float()
            out.extend(torch.sigmoid(logits).cpu().tolist())
    return out


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):   # 安靜：stdout 留給 Senate 讀
        pass

    def _send(self, code, obj):
        b = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def _auth(self):
        if self.headers.get("X-Kb-Token") != args.token:
            self._send(403, {"ok": False, "error": "token 不對"})
            return False
        state["last"] = time.time()
        return True

    def do_GET(self):
        if not self._auth():
            return
        if self.path == "/health":
            self._send(200, {"ok": True, "model": args.model, "device": DEVICE, "fp16": True,
                             "loaded_ms": LOADED_MS, "served": state["served"], "pid": os.getpid(),
                             "features": FEATURES, "rerank_loaded": RERANKER["m"] is not None})
        else:
            self._send(404, {"ok": False, "error": "沒有這個路徑"})

    def do_POST(self):
        if not self._auth():
            return
        n = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(n) or b"{}")
        if self.path == "/shutdown":
            self._send(200, {"ok": True})
            threading.Thread(target=srv.shutdown, daemon=True).start()
            return
        if self.path == "/rerank":
            try:
                with lock:
                    t = time.time()
                    q = body.get("query") or ""
                    passages = body.get("passages") or []
                    scores = rerank_scores(q, passages, int(body.get("max_length", 512))) if passages else []
                    state["served"] += len(passages)
                    ms = round((time.time() - t) * 1000)
                self._send(200, {"ok": True, "n": len(passages), "scores": [float(s) for s in scores], "ms": ms})
            except Exception as e:
                self._send(500, {"ok": False, "error": f"{type(e).__name__}: {e}"})
            return
        if self.path != "/embed":
            self._send(404, {"ok": False, "error": "沒有這個路徑"})
            return
        texts = body.get("texts") or []
        want_sparse = bool(body.get("sparse", True))
        try:
            with lock:
                t = time.time()
                out = MODEL.encode(texts, batch_size=int(body.get("batch_size", 16)),
                                   max_length=int(body.get("max_length", 1024)),
                                   return_dense=True, return_sparse=want_sparse, return_colbert_vecs=False)
                dense = np.asarray(out["dense_vecs"], dtype=np.float32)
                if dense.ndim == 1:
                    dense = dense.reshape(1, -1)
                sparse = []
                if want_sparse:
                    for d in out["lexical_weights"]:
                        sparse.append({str(k): float(v) for k, v in d.items()})
                state["served"] += len(texts)
                ms = round((time.time() - t) * 1000)
            self._send(200, {"ok": True, "dim": int(dense.shape[1]) if dense.size else 0, "n": len(texts),
                             "dense_b64": base64.b64encode(dense.astype("<f4").tobytes()).decode("ascii"),
                             "sparse": sparse, "ms": ms})
        except Exception as e:
            self._send(500, {"ok": False, "error": f"{type(e).__name__}: {e}"})


srv = ThreadingHTTPServer(("127.0.0.1", args.port), H)
port = srv.server_address[1]
tmp = args.info + ".tmp"
with open(tmp, "w", encoding="utf-8") as f:
    json.dump({"pid": os.getpid(), "port": port, "model": args.model, "device": DEVICE,
               "loaded_ms": LOADED_MS, "started_at": time.strftime("%Y-%m-%dT%H:%M:%S%z")}, f)
os.replace(tmp, args.info)


def idle_watch():
    while True:
        time.sleep(15)
        if time.time() - state["last"] > args.idle:
            srv.shutdown()
            return


threading.Thread(target=idle_watch, daemon=True).start()
try:
    srv.serve_forever()
finally:
    try:
        os.remove(args.info)        # 退出就撤掉 info 檔：檔在＝還活著（Senate 仍會用 /health 複驗，不只看檔）
    except OSError:
        pass
