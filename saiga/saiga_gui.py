#!/usr/bin/env python3
"""
Панель управления сайгой — GUI (Tkinter, без зависимостей).

Кнопка на каждый MCP-тул (тянутся динамически из tools/list), поля под параметры
(target/text/dx/dy/filter/radius…), лог ответов, и строка «сказать боргу» (пишет в
инбокс ~/saiga-mcp/inbox — его читает автономный listen_agent.py).

Требует ЗАПУЩЕННЫЙ клиент (в нём живёт MCP :1213). Автономный раннер listen_agent.py
нужен только для «боргу→инбокс» и LLM-реакций; кнопки-тулзы работают и без него.

Запуск:  python3 ~/saiga-mcp/saiga_gui.py
"""
import json, os, threading, queue, urllib.request
import tkinter as tk
from tkinter import ttk, scrolledtext

MCP_URL   = "http://127.0.0.1:1213/mcp"
MCP_TOKEN = "devsecret"
INBOX     = os.path.expanduser("~/saiga-mcp/inbox")


def _rpc(method, params=None):
    body = {"jsonrpc": "2.0", "id": 1, "method": method}
    if params is not None:
        body["params"] = params
    req = urllib.request.Request(MCP_URL, data=json.dumps(body).encode(),
                                 headers={"Authorization": f"Bearer {MCP_TOKEN}",
                                          "Content-Type": "application/json"})
    r = json.load(urllib.request.urlopen(req, timeout=30))
    if "error" in r:
        raise RuntimeError(r["error"])
    return r.get("result", {})


def tool_call(name, args):
    res = _rpc("tools/call", {"name": name, "arguments": args})
    txt = "".join(b.get("text", "") for b in res.get("content", []) if b.get("type") == "text")
    return txt, bool(res.get("isError"))


class App:
    def __init__(self, root):
        self.root = root
        root.title("Сайга — панель управления")
        root.geometry("960x720")
        self.q = queue.Queue()
        self.tools = []
        self._build()
        self._poll_queue()
        self.refresh_tools()

    def _build(self):
        top = ttk.Frame(self.root, padding=6)
        top.pack(fill="x")
        self.status = ttk.Label(top, text="Подключаюсь к MCP…")
        self.status.pack(side="left")
        ttk.Button(top, text="⟳ Обновить тулзы", command=self.refresh_tools).pack(side="right")

        opf = ttk.Frame(self.root, padding=(6, 0))
        opf.pack(fill="x")
        ttk.Label(opf, text="Боргу (инбокс):").pack(side="left")
        self.op = ttk.Entry(opf)
        self.op.pack(side="left", fill="x", expand=True, padx=4)
        self.op.bind("<Return>", lambda e: self.send_op())
        ttk.Button(opf, text="Отправить", command=self.send_op).pack(side="left")

        ff = ttk.Frame(self.root, padding=(6, 4))
        ff.pack(fill="x")
        ttk.Label(ff, text="Фильтр:").pack(side="left")
        self.flt = ttk.Entry(ff)
        self.flt.pack(side="left", fill="x", expand=True, padx=4)
        self.flt.bind("<KeyRelease>", lambda e: self._render_tools())

        # Тулзы (сверху) и «Ответы» (снизу) — в вертикальном PanedWindow: разделитель тянется, обе растут с окном.
        paned = ttk.PanedWindow(self.root, orient="vertical")
        paned.pack(fill="both", expand=True, padx=6, pady=(0, 6))

        mid = ttk.Frame(paned)
        self.canvas = tk.Canvas(mid, highlightthickness=0)
        sb = ttk.Scrollbar(mid, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas)
        self.inner.bind("<Configure>", lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=sb.set)
        self.canvas.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")
        for seq, d in (("<MouseWheel>", None), ("<Button-4>", -1), ("<Button-5>", 1)):
            self.canvas.bind_all(seq, self._on_wheel)

        logframe = ttk.Frame(paned)
        ttk.Label(logframe, text="Ответы:").pack(anchor="w")
        self.log = scrolledtext.ScrolledText(logframe, height=6, wrap="word")
        self.log.pack(fill="both", expand=True)

        paned.add(mid, weight=3)
        paned.add(logframe, weight=2)

    def _on_wheel(self, e):
        if getattr(e, "num", None) == 4:
            self.canvas.yview_scroll(-1, "units")
        elif getattr(e, "num", None) == 5:
            self.canvas.yview_scroll(1, "units")
        else:
            self.canvas.yview_scroll(int(-e.delta / 120), "units")

    def _run_bg(self, fn):
        threading.Thread(target=fn, daemon=True).start()

    def refresh_tools(self):
        def work():
            try:
                t = _rpc("tools/list").get("tools", [])
                self.q.put(("tools", t))
            except Exception as e:
                self.q.put(("status", f"MCP недоступен: {e} — запущен ли клиент (SAIGA_MCP_CLIENT=1) и ты в игре?"))
        self._run_bg(work)

    def _render_tools(self):
        for w in self.inner.winfo_children():
            w.destroy()
        flt = self.flt.get().strip().lower()
        for t in self.tools:
            if flt and flt not in t["name"].lower():
                continue
            self._tool_row(t)

    def _tool_row(self, t):
        row = ttk.Frame(self.inner, padding=(0, 2))
        row.pack(fill="x", anchor="w")
        entries = {}
        ttk.Button(row, text=t["name"], width=16,
                   command=lambda: self._call_tool(t, entries)).pack(side="left")
        schema = t.get("inputSchema") or {}
        props = schema.get("properties", {})
        req = set(schema.get("required", []))
        for pname, pinfo in props.items():
            ttk.Label(row, text=pname + ("*" if pname in req else "") + ":").pack(side="left", padx=(6, 1))
            e = ttk.Entry(row, width=10 if pinfo.get("type") == "integer" else 18)
            e.pack(side="left")
            entries[pname] = (e, pinfo.get("type"))
        desc = t.get("description", "")
        ttk.Label(row, text=" — " + (desc[:70] + "…" if len(desc) > 70 else desc),
                  foreground="#888").pack(side="left", padx=6)

    def _call_tool(self, t, entries):
        args = {}
        try:
            for pname, (e, ptype) in entries.items():
                v = e.get().strip()
                if v == "":
                    continue
                args[pname] = int(v) if ptype == "integer" else v
        except ValueError:
            self._log(f"[!] {t['name']}: числовой параметр — введи число")
            return
        self._log(f"→ {t['name']}({args})")

        def work():
            try:
                txt, err = tool_call(t["name"], args)
                self.q.put(("log", f"{'ERR ' if err else ''}{txt}\n"))
            except Exception as ex:
                self.q.put(("log", f"[ошибка] {ex}\n"))
        self._run_bg(work)

    def send_op(self):
        s = self.op.get().strip()
        if not s:
            return
        try:
            with open(INBOX, "a", encoding="utf-8") as f:
                f.write(s + "\n")
            self._log(f"[боргу] {s}")
            self.op.delete(0, "end")
        except Exception as e:
            self._log(f"[!] инбокс: {e}")

    def _log(self, s):
        self.log.insert("end", s if s.endswith("\n") else s + "\n")
        self.log.see("end")

    def _poll_queue(self):
        try:
            while True:
                kind, data = self.q.get_nowait()
                if kind == "tools":
                    self.tools = data
                    self.status.config(text=f"MCP OK — {len(data)} тулзов")
                    self._render_tools()
                elif kind == "status":
                    self.status.config(text=data)
                elif kind == "log":
                    self._log(data)
        except queue.Empty:
            pass
        self.root.after(120, self._poll_queue)


if __name__ == "__main__":
    root = tk.Tk()
    App(root)
    root.mainloop()
