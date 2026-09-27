"""The Skitch-like editor window: toolbar, canvas and all mouse/keyboard handling."""
import math
import os
import tempfile
import time
import tkinter as tk
from tkinter import filedialog, font as tkfont

from PIL import Image, ImageGrab, ImageTk
from tkinterdnd2 import COPY, DND_FILES

from . import annotations as A
from . import icons, win32

COLORS = ["#EA3323", "#F03C96", "#F7A12B", "#FDE23A", "#58C43C", "#2F8CF2",
          "#8A3FD1", "#000000", "#FFFFFF"]
SIZES = [3, 6, 10]
TEXT_SIZES = [20, 32, 52]
STAMP_R = [18, 28, 42]

TOOLS = [("arrow", "矢印 (A)"), ("text", "テキスト (T)"), ("shape", "図形 (R)"),
         ("pen", "ペン (M / 蛍光ペン H)"), ("mosaic", "モザイク (P)"),
         ("stamp", "スタンプ (S)"), ("crop", "切り抜き (C)")]
VARIANTS = {
    "shape": [("rect", "四角形"), ("rrect", "角丸四角形"), ("oval", "楕円"), ("line", "直線")],
    "pen": [("marker", "マーカー"), ("highlighter", "蛍光ペン")],
    "mosaic": [("pixelate", "モザイク"), ("blur", "ぼかし")],
    "stamp": [("check", "✔ OK"), ("cross", "✖ NG"), ("question", "? 質問"),
              ("exclaim", "! 注意"), ("star", "★ スター"), ("heart", "♥ ハート")],
}
KEY_TOOLS = {"a": ("arrow", None), "t": ("text", None), "r": ("shape", None),
             "m": ("pen", "marker"), "h": ("pen", "highlighter"), "p": ("mosaic", None),
             "s": ("stamp", None), "c": ("crop", None)}

BG = "#262626"
BAR = "#303030"
HOVER = "#444444"
ACTIVE = "#555555"
CANVAS_BG = "#3c3c3c"
FG = "#e6e6e6"
DIM_FG = "#a0a0a0"
ACCENT = "#2F8CF2"
IMAGE_TYPES = [("画像", "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"), ("すべて", "*.*")]


def pictures_dir():
    p = os.path.join(os.path.expanduser("~"), "Pictures")
    return p if os.path.isdir(p) else os.path.expanduser("~")


class Editor:
    def __init__(self, root, on_snap):
        self.root = root
        self.on_snap = on_snap
        self.ui = root.winfo_fpixels("1i") / 96
        self.base = None
        self.anns = []
        self.selected = None
        self.undo_stack, self.redo_stack = [], []
        self.tool = "arrow"
        self.variant = {"shape": "rect", "pen": "marker", "mosaic": "pixelate", "stamp": "check"}
        self.color = COLORS[0]
        self.size = 1
        self.path = None
        self.doc_name = "Skitch.png"
        self.drag = None
        self.crop = None
        self.text_edit = None
        self.scale, self.off = 1.0, (0, 0)
        self._pushed = False
        self._render_pending = False
        self._status_job = None
        self._icons = {}
        self._build()
        self.set_tool("arrow")

    def px(self, v):
        return max(1, round(v * self.ui))

    # ================================================================ UI
    def _build(self):
        root = self.root
        root.configure(bg=BG)
        self._build_menu()

        top = tk.Frame(root, bg=BAR, height=self.px(44))
        top.pack(side="top", fill="x")
        snap = self._button(top, "📷  スナップ", lambda: self.on_snap("region"))
        snap.configure(bg="#3d6fb6", activebackground="#4a80cc")
        snap.pack(side="left", padx=(self.px(8), 0), pady=self.px(6))
        more = self._button(top, "▾", None)
        more.configure(bg="#3d6fb6", activebackground="#4a80cc")
        more.pack(side="left", padx=(1, self.px(10)), pady=self.px(6))
        more.configure(command=lambda: self._popup(self.snap_menu, more))
        for text, cmd in [("開く", self.open_file), ("保存", self.save), ("コピー", self.copy)]:
            self._button(top, text, cmd).pack(side="left", padx=self.px(2), pady=self.px(6))
        self.info = tk.Label(top, text="", bg=BAR, fg=DIM_FG, font=("Yu Gothic UI", 9))
        self.info.pack(side="right", padx=self.px(12))

        bottom = tk.Frame(root, bg=BAR)
        bottom.pack(side="bottom", fill="x")
        self.handle = tk.Label(bottom, text="⠿  ここをドラッグして書き出し", bg="#3a3a3a", fg=FG,
                               font=("Yu Gothic UI", 9), padx=self.px(10), pady=self.px(4), cursor="hand2")
        self.handle.pack(side="left", padx=self.px(8), pady=self.px(5))
        self.handle.drag_source_register(1, DND_FILES)
        self.handle.dnd_bind("<<DragInitCmd>>", self._drag_out)
        self.status = tk.Label(bottom, text="", bg=BAR, fg=DIM_FG, font=("Yu Gothic UI", 9))
        self.status.pack(side="left", padx=self.px(8))

        side = tk.Frame(root, bg=BG)
        side.pack(side="left", fill="y")
        self.tool_btns = {}
        for tool, tip in TOOLS:
            b = tk.Label(side, bg=BG, cursor="hand2", padx=self.px(8), pady=self.px(6))
            b.pack(side="top", padx=self.px(6), pady=(self.px(4), 0))
            b.bind("<Button-1>", lambda e, t=tool: self._tool_click(t))
            b.bind("<Button-3>", lambda e, t=tool: self._variant_menu(t))
            b.bind("<Enter>", lambda e, t=tool, tip=tip: (self._hover_tool(t, True), self.show_status(tip, 0)))
            b.bind("<Leave>", lambda e, t=tool: (self._hover_tool(t, False), self.show_status("")))
            self.tool_btns[tool] = b
            self._refresh_icon(tool)

        tk.Frame(side, bg="#3a3a3a", height=1).pack(fill="x", padx=self.px(8), pady=self.px(10))
        grid = tk.Frame(side, bg=BG)
        grid.pack(side="top")
        sw = self.px(16)
        self.swatches = {}
        for i, color in enumerate(COLORS):
            c = tk.Canvas(grid, width=sw, height=sw, bg=color, highlightthickness=self.px(2),
                          highlightbackground=BG, cursor="hand2")
            c.grid(row=i // 3, column=i % 3, padx=1, pady=1)
            c.bind("<Button-1>", lambda e, col=color: self.set_color(col))
            self.swatches[color] = c

        tk.Frame(side, bg="#3a3a3a", height=1).pack(fill="x", padx=self.px(8), pady=self.px(10))
        sizes = tk.Frame(side, bg=BG)
        sizes.pack(side="top")
        self.size_btns = []
        d = self.px(20)
        for i, r in enumerate((2, 4, 7)):
            c = tk.Canvas(sizes, width=d, height=d, bg=BG, highlightthickness=0, cursor="hand2")
            c.create_oval(d / 2 - self.px(r), d / 2 - self.px(r), d / 2 + self.px(r), d / 2 + self.px(r),
                          fill=FG, outline="")
            c.grid(row=0, column=i, padx=1)
            c.bind("<Button-1>", lambda e, i=i: self.set_size(i))
            self.size_btns.append(c)

        self.canvas = tk.Canvas(root, bg=CANVAS_BG, highlightthickness=0)
        self.canvas.pack(side="left", fill="both", expand=True)
        c = self.canvas
        c.bind("<Configure>", lambda e: self.request_render())
        c.bind("<ButtonPress-1>", self.on_press)
        c.bind("<B1-Motion>", self.on_drag)
        c.bind("<ButtonRelease-1>", self.on_release)
        c.bind("<Double-Button-1>", self.on_double)
        c.bind("<Motion>", self.on_motion)
        c.drop_target_register(DND_FILES)
        c.dnd_bind("<<Drop>>", self._drop)

        keys = {
            "<Control-z>": self.undo, "<Control-y>": self.redo, "<Control-Z>": self.redo,
            "<Control-c>": self.copy, "<Control-s>": self.save, "<Control-S>": self.save_as,
            "<Control-o>": self.open_file, "<Control-v>": self.paste,
            "<Delete>": self.delete_selected, "<BackSpace>": self.delete_selected,
            "<Escape>": self.escape, "<Return>": self.enter,
        }
        for seq, fn in keys.items():
            root.bind(seq, lambda e, fn=fn: None if self._typing() else (fn(), "break")[1])
        root.bind("<Key>", self.on_key)
        self._update_palette()

    def _build_menu(self):
        m = tk.Menu(self.root)
        f = tk.Menu(m, tearoff=0)
        f.add_command(label="範囲スナップ", accelerator="Ctrl+Shift+5", command=lambda: self.on_snap("region"))
        f.add_command(label="全画面スナップ", accelerator="Ctrl+Shift+6", command=lambda: self.on_snap("full"))
        f.add_separator()
        f.add_command(label="開く...", accelerator="Ctrl+O", command=self.open_file)
        f.add_command(label="保存", accelerator="Ctrl+S", command=self.save)
        f.add_command(label="名前を付けて保存...", accelerator="Ctrl+Shift+S", command=self.save_as)
        f.add_separator()
        f.add_command(label="閉じる (トレイに格納)", command=lambda: self.root.event_generate("<<HideWindow>>"))
        f.add_command(label="終了", command=lambda: self.root.event_generate("<<QuitApp>>"))
        m.add_cascade(label="ファイル", menu=f)
        e = tk.Menu(m, tearoff=0)
        e.add_command(label="元に戻す", accelerator="Ctrl+Z", command=self.undo)
        e.add_command(label="やり直し", accelerator="Ctrl+Y", command=self.redo)
        e.add_separator()
        e.add_command(label="画像をコピー", accelerator="Ctrl+C", command=self.copy)
        e.add_command(label="貼り付け", accelerator="Ctrl+V", command=self.paste)
        e.add_command(label="選択を削除", accelerator="Delete", command=self.delete_selected)
        m.add_cascade(label="編集", menu=e)
        s = self.snap_menu = tk.Menu(m, tearoff=0)
        s.add_command(label="範囲スナップ", accelerator="Ctrl+Shift+5", command=lambda: self.on_snap("region"))
        s.add_command(label="全画面スナップ", accelerator="Ctrl+Shift+6", command=lambda: self.on_snap("full"))
        s.add_command(label="タイマースナップ (5秒後)", command=lambda: self.on_snap("timer"))
        s.add_command(label="前回の範囲でスナップ", command=lambda: self.on_snap("previous"))
        m.add_cascade(label="スナップ", menu=s)
        self.root.config(menu=m)

    def _button(self, parent, text, cmd):
        return tk.Button(parent, text=text, command=cmd, bg=BAR, fg=FG, activebackground=HOVER,
                         activeforeground=FG, relief="flat", bd=0, padx=self.px(10), pady=self.px(3),
                         font=("Yu Gothic UI", 10), cursor="hand2")

    def _popup(self, menu, widget):
        menu.tk_popup(widget.winfo_rootx(), widget.winfo_rooty() + widget.winfo_height())

    def _refresh_icon(self, tool):
        img = icons.tool_icon(tool, self.variant.get(tool), self.px(26), has_menu=tool in VARIANTS)
        self._icons[tool] = ImageTk.PhotoImage(img)
        self.tool_btns[tool].configure(image=self._icons[tool])

    def _hover_tool(self, tool, on):
        if tool != self.tool:
            self.tool_btns[tool].configure(bg=HOVER if on else BG)

    def _tool_click(self, tool):
        if tool == self.tool and tool in VARIANTS:
            self._variant_menu(tool)
        else:
            self.set_tool(tool)

    def _variant_menu(self, tool):
        if tool not in VARIANTS:
            return self.set_tool(tool)
        menu = tk.Menu(self.root, tearoff=0)
        self._menu_var = tk.StringVar(value=self.variant[tool])
        for key, label in VARIANTS[tool]:
            menu.add_radiobutton(label=label, variable=self._menu_var, value=key,
                                 command=lambda k=key: self.set_variant(tool, k))
        w = self.tool_btns[tool]
        menu.tk_popup(w.winfo_rootx() + w.winfo_width(), w.winfo_rooty())

    def set_variant(self, tool, key):
        self.variant[tool] = key
        self._refresh_icon(tool)
        self.set_tool(tool)

    def set_tool(self, tool):
        self.commit_text()
        self.tool = tool
        self.crop = None
        if tool == "crop" and self.base:
            self.crop = A.Box(None, 0, 0, 0, *self.base.size)
            self.selected = None
            self.show_status("ハンドルで範囲を調整 → Enter で確定 / Esc でキャンセル（外側に広げると余白を追加）", 0)
        for t, b in self.tool_btns.items():
            b.configure(bg=ACTIVE if t == tool else BG)
        self.canvas.configure(cursor="xterm" if tool == "text" else "crosshair")
        self.request_render()

    def set_color(self, color):
        self.color = color
        if self.text_edit:
            self.text_edit["color"] = color
            self.text_edit["widget"].configure(fg=color, insertbackground=color)
        elif self.selected and not isinstance(self.selected, A.Mosaic):
            self.begin_change()
            self.selected.color = color
            self.selected.changed()
            self.request_render()
        self._pushed = False
        self._update_palette()

    def set_size(self, i):
        self.size = i
        if self.selected:
            self.begin_change()
            table = {A.Text: TEXT_SIZES, A.Stamp: STAMP_R}.get(type(self.selected), SIZES)
            self.selected.width = table[i]
            self.selected.changed()
            self.request_render()
        self._pushed = False
        self._update_palette()

    def _update_palette(self):
        for color, c in self.swatches.items():
            c.configure(highlightbackground="white" if color == self.color else BG)
        for i, c in enumerate(self.size_btns):
            c.configure(bg=ACTIVE if i == self.size else BG)

    def show_status(self, text, ms=3000):
        self.status.configure(text=text)
        if self._status_job:
            self.root.after_cancel(self._status_job)
            self._status_job = None
        if ms:
            self._status_job = self.root.after(ms, lambda: self.status.configure(text=""))

    def _typing(self):
        return isinstance(self.root.focus_get(), (tk.Text, tk.Entry))

    # ========================================================= document
    def load_image(self, img, name=None):
        self.commit_text()
        self.base = img.convert("RGBA")
        self.anns = []
        self.selected = None
        self.undo_stack.clear()
        self.redo_stack.clear()
        self.path = None
        self.doc_name = name or time.strftime("Skitch_%Y-%m-%d_%H%M%S.png")
        self.set_tool("arrow")

    def composite(self):
        img = self.base.copy()
        for a in self.anns:
            a.render_onto(img)
        return img

    def flatten(self):
        self.commit_text()
        return self.composite().convert("RGB")

    def snapshot(self):
        return self.base, [a.clone() for a in self.anns]

    def begin_change(self):
        """Record an undo point once per user gesture."""
        if not self._pushed:
            self.undo_stack.append(self.snapshot())
            del self.undo_stack[:-100]
            self.redo_stack.clear()
            self._pushed = True

    def _restore(self, src, dst):
        if not src:
            return
        self.commit_text()
        dst.append(self.snapshot())
        self.base, self.anns = src.pop()
        self.selected = None
        if self.tool == "crop":
            self.set_tool("crop")
        self.request_render()

    def undo(self):
        self._restore(self.undo_stack, self.redo_stack)

    def redo(self):
        self._restore(self.redo_stack, self.undo_stack)

    def delete_selected(self):
        if self.selected in self.anns:
            self._pushed = False
            self.begin_change()
            self.anns.remove(self.selected)
            self.selected = None
            self.request_render()

    def escape(self):
        if self.tool == "crop":
            self.set_tool("arrow")
        else:
            self.selected = None
            self.request_render()

    def enter(self):
        if self.tool == "crop":
            self.apply_crop()

    def apply_crop(self):
        x0, y0, x1, y1 = (round(v) for v in self.crop.bbox())
        if x1 - x0 < 2 or y1 - y0 < 2:
            return
        self._pushed = False
        self.begin_change()
        new = Image.new("RGBA", (x1 - x0, y1 - y0), (255, 255, 255, 255))
        new.alpha_composite(self.base.crop((x0, y0, x1, y1)))
        self.base = new
        for a in self.anns:
            a.move(-x0, -y0)
        self.set_tool("arrow")
        self.show_status(f"切り抜きました ({x1 - x0} × {y1 - y0})")

    # ============================================================ files
    def open_file(self):
        p = filedialog.askopenfilename(parent=self.root, filetypes=IMAGE_TYPES, initialdir=pictures_dir())
        if p:
            self.open_path(p)

    def open_path(self, p):
        try:
            img = Image.open(p)
            img.load()
        except Exception as ex:
            self.show_status(f"開けませんでした: {ex}")
            return
        self.load_image(img, os.path.splitext(os.path.basename(p))[0] + "_skitch.png")
        self.root.event_generate("<<ShowWindow>>")

    def save(self):
        if self.path:
            self._write(self.path)
        else:
            self.save_as()

    def save_as(self):
        if not self.base:
            return
        p = filedialog.asksaveasfilename(parent=self.root, defaultextension=".png",
                                         initialdir=os.path.dirname(self.path) if self.path else pictures_dir(),
                                         initialfile=os.path.basename(self.path or self.doc_name),
                                         filetypes=[("PNG", "*.png"), ("JPEG", "*.jpg;*.jpeg")])
        if p:
            self._write(p)

    def _write(self, p):
        if not self.base:
            return
        jpeg = os.path.splitext(p)[1].lower() in (".jpg", ".jpeg")
        self.flatten().save(p, "JPEG" if jpeg else "PNG", quality=92)
        self.path = p
        self.show_status(f"保存しました: {p}")

    def copy(self):
        if self.base and win32.copy_image(self.flatten()):
            self.show_status("クリップボードにコピーしました")

    def paste(self):
        data = ImageGrab.grabclipboard()
        if isinstance(data, Image.Image):
            self.load_image(data)
        elif isinstance(data, list) and data:
            self.open_path(data[0])

    def _drop(self, event):
        files = self.root.tk.splitlist(event.data)
        if files:
            self.open_path(files[0])
        return COPY

    def _drag_out(self, event):
        if not self.base:
            return None
        folder = os.path.join(tempfile.gettempdir(), "WinSkitch")
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, self.doc_name)
        self.flatten().save(path, "PNG")
        return (COPY,), (DND_FILES,), (path,)

    # ========================================================= rendering
    def request_render(self):
        if not self._render_pending:
            self._render_pending = True
            self.root.after_idle(self._render)

    def _render(self):
        self._render_pending = False
        c = self.canvas
        c.delete("all")
        cw, ch = max(1, c.winfo_width()), max(1, c.winfo_height())
        if self.base is None:
            self.info.configure(text="")
            c.create_text(cw / 2, ch / 2, fill=DIM_FG, justify="center", font=("Yu Gothic UI", 12),
                          text="Ctrl + Shift + 5 で画面をスナップ\n\n"
                               "画像をドロップ / Ctrl+V で貼り付け / Ctrl+O で開く")
            return
        iw, ih = self.base.size
        m = self.px(24)
        s = max(0.05, min(1.0, (cw - 2 * m) / iw, (ch - 2 * m) / ih))
        dw, dh = max(1, round(iw * s)), max(1, round(ih * s))
        ox, oy = (cw - dw) // 2, (ch - dh) // 2
        self.scale, self.off = s, (ox, oy)
        img = self.composite().convert("RGB")  # RGB resizes much faster than RGBA
        if s < 1:
            img = img.resize((dw, dh), Image.BILINEAR, reducing_gap=2.0)
        self.photo = ImageTk.PhotoImage(img)
        c.create_rectangle(ox + 2, oy + 3, ox + dw + 3, oy + dh + 4, fill="#2a2a2a", outline="")
        c.create_image(ox, oy, anchor="nw", image=self.photo)
        self._draw_overlay()
        self.info.configure(text=f"{iw} × {ih}  ·  {round(s * 100)}%")

    def to_img(self, x, y):
        return (x - self.off[0]) / self.scale, (y - self.off[1]) / self.scale

    def to_disp(self, x, y):
        return x * self.scale + self.off[0], y * self.scale + self.off[1]

    def _handle_dot(self, x, y):
        r = self.px(5)
        dx, dy = self.to_disp(x, y)
        self.canvas.create_oval(dx - r, dy - r, dx + r, dy + r, fill="white", outline=ACCENT, width=2)

    def _draw_overlay(self):
        c = self.canvas
        a = self.selected
        if a and a in self.anns and not a.hidden:
            hs = a.handles()
            if not hs or isinstance(a, (A.Mosaic, A.Stamp)):
                x0, y0, x1, y1 = a.bbox()
                d0, d1 = self.to_disp(x0, y0), self.to_disp(x1, y1)
                g = self.px(4)
                c.create_rectangle(d0[0] - g, d0[1] - g, d1[0] + g, d1[1] + g, outline=ACCENT, dash=(4, 3))
            for _, x, y in hs:
                self._handle_dot(x, y)
        if self.crop:
            x0, y0, x1, y1 = self.crop.bbox()
            (d0x, d0y), (d1x, d1y) = self.to_disp(x0, y0), self.to_disp(x1, y1)
            W, H = c.winfo_width(), c.winfo_height()
            for box in ((0, 0, W, d0y), (0, d1y, W, H), (0, d0y, d0x, d1y), (d1x, d0y, W, d1y)):
                c.create_rectangle(*box, fill="black", stipple="gray50", outline="")
            c.create_rectangle(d0x, d0y, d1x, d1y, outline="white", dash=(6, 4))
            for _, x, y in self.crop.handles():
                self._handle_dot(x, y)
            c.create_text((d0x + d1x) / 2, d1y + self.px(14), fill="white", font=("Yu Gothic UI", 9),
                          text=f"{round(abs(x1 - x0))} × {round(abs(y1 - y0))}   Enter で確定")

    # ============================================================ mouse
    def _tol(self):
        return 6 / self.scale

    def _handle_at(self, obj, x, y):
        r = 8 / self.scale
        for name, hx, hy in obj.handles():
            if abs(x - hx) <= r and abs(y - hy) <= r:
                return name
        return None

    def _ann_at(self, x, y):
        for a in reversed(self.anns):
            if not a.hidden and a.hit(x, y, self._tol()):
                return a
        return None

    def on_motion(self, e):
        if self.base is None or self.drag:
            return
        x, y = self.to_img(e.x, e.y)
        target = self.crop if self.tool == "crop" else self.selected
        if target and self._handle_at(target, x, y):
            cur = "sizing"
        elif self.tool != "crop" and self._ann_at(x, y):
            cur = "fleur"
        elif self.tool == "crop" and self.crop and self.crop.hit(x, y, 0):
            cur = "fleur"
        else:
            cur = "xterm" if self.tool == "text" else "crosshair"
        if self.canvas.cget("cursor") != cur:
            self.canvas.configure(cursor=cur)

    def on_press(self, e):
        self.canvas.focus_set()
        if self.base is None:
            return
        if self.text_edit:
            self.commit_text()
            return
        x, y = self.to_img(e.x, e.y)
        self._pushed = False
        if self.tool == "crop":
            h = self._handle_at(self.crop, x, y)
            if h:
                self.drag = {"mode": "crop_handle", "name": h}
            elif self.crop.hit(x, y, 0):
                self.drag = {"mode": "crop_move", "last": (x, y)}
            else:
                self.crop = A.Box(None, 0, x, y, x, y)
                self.drag = {"mode": "crop_handle", "name": "x1y1"}
            return
        if self.selected:
            h = self._handle_at(self.selected, x, y)
            if h:
                self.drag = {"mode": "handle", "name": h}
                return
        hit = self._ann_at(x, y)
        if hit and self.tool == "text" and isinstance(hit, A.Text):
            self.start_text(hit.x, hit.y, hit)
            return
        if hit:
            self.selected = hit
            self.drag = {"mode": "move", "last": (x, y)}
            self.request_render()
            return
        self.selected = None
        ann = self._new_annotation(x, y)
        if ann:
            self.begin_change()
            self.anns.append(ann)
            self.drag = {"mode": "create", "ann": ann, "start": (x, y)}
        self.request_render()

    def _new_annotation(self, x, y):
        t, c, w = self.tool, self.color, SIZES[self.size]
        if t == "arrow":
            return A.Arrow(c, w, x, y, x, y)
        if t == "shape":
            v = self.variant["shape"]
            if v == "line":
                return A.Line(c, w, x, y, x, y)
            if v == "oval":
                return A.Oval(c, w, x, y, x, y)
            return A.Rect(c, w, x, y, x, y, rounded=v == "rrect")
        if t == "pen":
            return A.Freehand(c, w, [(x, y)], highlighter=self.variant["pen"] == "highlighter")
        if t == "mosaic":
            return A.Mosaic(w, x, y, x, y, mode=self.variant["mosaic"])
        if t == "stamp":
            return A.Stamp(c, STAMP_R[self.size], x, y, self.variant["stamp"])
        if t == "text":
            self.start_text(x, y)
        return None

    @staticmethod
    def _constrain(sx, sy, x, y, square):
        """Shift-drag: 45° steps for lines, squares for boxes."""
        dx, dy = x - sx, y - sy
        if square:
            m = max(abs(dx), abs(dy))
            return sx + math.copysign(m, dx or 1), sy + math.copysign(m, dy or 1)
        ang = round(math.atan2(dy, dx) / (math.pi / 4)) * (math.pi / 4)
        L = math.hypot(dx, dy)
        return sx + L * math.cos(ang), sy + L * math.sin(ang)

    def on_drag(self, e):
        d = self.drag
        if not d:
            return
        x, y = self.to_img(e.x, e.y)
        shift = e.state & 0x0001
        mode = d["mode"]
        if mode == "create":
            a = d["ann"]
            sx, sy = d["start"]
            if isinstance(a, A.Freehand):
                lx, ly = a.points[-1]
                if math.hypot(x - lx, y - ly) >= 1.5 / self.scale:
                    a.points.append((x, y))
                    a.changed()
            elif isinstance(a, A.Stamp):
                a.width = max(STAMP_R[self.size], math.hypot(x - sx, y - sy))
                a.changed()
            else:
                if shift:
                    x, y = self._constrain(sx, sy, x, y, isinstance(a, A.Box))
                a.drag_handle(a.handles()[-1][0], x, y)
        elif mode == "handle":
            self.begin_change()
            self.selected.drag_handle(d["name"], x, y)
        elif mode == "move":
            self.begin_change()
            lx, ly = d["last"]
            self.selected.move(x - lx, y - ly)
            d["last"] = (x, y)
        elif mode == "crop_handle":
            self.crop.drag_handle(d["name"], x, y)
        elif mode == "crop_move":
            lx, ly = d["last"]
            self.crop.move(x - lx, y - ly)
            d["last"] = (x, y)
        self.request_render()

    def on_release(self, e):
        d, self.drag = self.drag, None
        if not d:
            return
        if d["mode"] == "create":
            a = d["ann"]
            x0, y0, x1, y1 = a.bbox()
            tiny = ((isinstance(a, A.TwoPoint) and a.length() < 4) or
                    (isinstance(a, A.Box) and (x1 - x0 < 4 or y1 - y0 < 4)))
            if tiny:
                self.anns.remove(a)
                self.undo_stack.pop()
            else:
                self.selected = a
        elif d["mode"].startswith("crop"):
            x0, y0, x1, y1 = self.crop.bbox()
            self.crop = A.Box(None, 0, x0, y0, x1, y1)
        self.request_render()

    def on_double(self, e):
        if self.base is None:
            return
        hit = self._ann_at(*self.to_img(e.x, e.y))
        if isinstance(hit, A.Text):
            self.drag = None
            self.start_text(hit.x, hit.y, hit)

    def on_key(self, e):
        if self._typing() or e.state & 0x0004 or self.base is None:
            return
        k = e.keysym.lower()
        if k in KEY_TOOLS:
            tool, variant = KEY_TOOLS[k]
            if variant and variant != self.variant[tool]:
                self.set_variant(tool, variant)
            else:
                self.set_tool(tool)
        elif k in ("left", "right", "up", "down") and self.selected:
            step = 10 if e.state & 0x0001 else 1
            dx, dy = {"left": (-step, 0), "right": (step, 0), "up": (0, -step), "down": (0, step)}[k]
            self._pushed = False
            self.begin_change()
            self.selected.move(dx, dy)
            self.request_render()

    # ============================================================= text
    def start_text(self, x, y, ann=None):
        self.commit_text()
        if ann:
            ann.hidden = True
            color, size, text = ann.color, ann.width, ann.text
            self.selected = None
            self.request_render()
        else:
            color, size, text = self.color, TEXT_SIZES[self.size], ""
        f = tkfont.Font(family="Yu Gothic UI", size=-max(8, round(size * self.scale)), weight="bold")
        light = A.is_light(color)
        w = tk.Text(self.canvas, font=f, fg=color, bg="#333333" if light else "#ffffff",
                    insertbackground=color, relief="flat", bd=0, highlightthickness=1,
                    highlightcolor=ACCENT, wrap="none", undo=True, width=2, height=1)
        w.insert("1.0", text)
        dx, dy = self.to_disp(x, y)
        w.place(x=dx, y=dy)
        self.text_edit = {"widget": w, "font": f, "x": x, "y": y, "ann": ann, "color": color, "size": size}
        w.bind("<Return>", lambda e: (self.commit_text(), "break")[1])
        w.bind("<Shift-Return>", lambda e: (w.insert("insert", "\n"), self._fit_text(), "break")[2])
        w.bind("<Escape>", lambda e: (self.commit_text(), "break")[1])
        w.bind("<KeyRelease>", lambda e: self._fit_text())
        self._fit_text()
        w.focus_set()
        self.show_status("Enter で確定 / Shift+Enter で改行", 0)

    def _fit_text(self):
        te = self.text_edit
        if not te:
            return
        w, f = te["widget"], te["font"]
        lines = w.get("1.0", "end-1c").split("\n")
        widest = max(f.measure(line) for line in lines)
        w.configure(width=max(3, math.ceil(widest / max(1, f.measure("0"))) + 2), height=len(lines))

    def commit_text(self):
        te, self.text_edit = self.text_edit, None
        if not te:
            return
        text = te["widget"].get("1.0", "end-1c").rstrip()
        te["widget"].destroy()
        self.show_status("")
        ann = te["ann"]
        self._pushed = False
        if ann:
            ann.hidden = False
            if not text.strip():
                self.begin_change()
                self.anns.remove(ann)
            elif text != ann.text or te["color"] != ann.color:
                self.begin_change()
                ann.text, ann.color = text, te["color"]
                ann.changed()
                self.selected = ann
            else:
                self.selected = ann
        elif text.strip():
            self.begin_change()
            a = A.Text(te["color"], te["size"], te["x"], te["y"], text)
            self.anns.append(a)
            self.selected = a
        self._pushed = False
        self.canvas.focus_set()
        self.request_render()
