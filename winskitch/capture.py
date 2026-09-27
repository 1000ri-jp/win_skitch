"""Full-screen crosshair overlay for picking a snap region.

Drag to select a region, or click to snap the window under the cursor.
Esc / right click cancels.
"""
import tkinter as tk

from PIL import Image, ImageTk

ACCENT = "#2F8CF2"


class SnapOverlay:
    def __init__(self, root, shot, origin, windows, on_done):
        self.shot = shot
        self.on_done = on_done
        vx, vy = origin
        self.W, self.H = shot.size
        self.wins = [(l - vx, t - vy, r - vx, b - vy) for l, t, r, b in windows]
        self.start = None
        self.dragging = False

        top = self.top = tk.Toplevel(root)
        top.overrideredirect(True)
        top.geometry(f"{self.W}x{self.H}+{vx}+{vy}")
        top.attributes("-topmost", True)
        c = self.c = tk.Canvas(top, width=self.W, height=self.H, highlightthickness=0, bd=0,
                               cursor="crosshair", bg="black")
        c.pack()

        rgb = shot.convert("RGB")
        self.bright = ImageTk.PhotoImage(rgb)
        self.dim = ImageTk.PhotoImage(Image.blend(rgb, Image.new("RGB", rgb.size), 0.45))
        c.create_image(0, 0, anchor="nw", image=self.dim)
        self.sel_photo = tk.PhotoImage(master=top)
        self.sel = c.create_image(0, 0, anchor="nw", image=self.sel_photo, state="hidden")
        self.rect = c.create_rectangle(0, 0, 0, 0, outline=ACCENT, width=2, state="hidden")
        self.hline = c.create_line(0, 0, self.W, 0, fill="white", dash=(4, 4))
        self.vline = c.create_line(0, 0, 0, self.H, fill="white", dash=(4, 4))
        self.label_bg = c.create_rectangle(0, 0, 0, 0, fill="#202020", outline="")
        self.label = c.create_text(0, 0, anchor="nw", fill="white", font=("Segoe UI", 10))

        c.bind("<Motion>", self.on_motion)
        c.bind("<ButtonPress-1>", self.on_press)
        c.bind("<B1-Motion>", self.on_drag)
        c.bind("<ButtonRelease-1>", self.on_release)
        c.bind("<Button-3>", lambda e: self.finish(None))
        top.bind("<Escape>", lambda e: self.finish(None))

        top.update_idletasks()
        top.lift()
        top.focus_force()
        c.focus_set()
        x, y = top.winfo_pointerx() - vx, top.winfo_pointery() - vy
        self.update_cursor(x, y)
        self.show_window_at(x, y)

    def window_at(self, x, y):
        for l, t, r, b in self.wins:
            if l <= x < r and t <= y < b:
                return max(0, l), max(0, t), min(self.W, r), min(self.H, b)
        return None

    def show_region(self, box):
        if box is None:
            self.c.itemconfig(self.sel, state="hidden")
            self.c.itemconfig(self.rect, state="hidden")
            return
        x0, y0, x1, y1 = box
        x0, x1 = sorted((max(0, min(self.W, int(x0))), max(0, min(self.W, int(x1)))))
        y0, y1 = sorted((max(0, min(self.H, int(y0))), max(0, min(self.H, int(y1)))))
        if x1 - x0 < 1 or y1 - y0 < 1:
            return self.show_region(None)
        self.sel_photo.blank()
        self.sel_photo.configure(width=x1 - x0, height=y1 - y0)
        self.c.tk.call(str(self.sel_photo), "copy", str(self.bright), "-from", x0, y0, x1, y1, "-to", 0, 0)
        self.c.coords(self.sel, x0, y0)
        self.c.coords(self.rect, x0, y0, x1, y1)
        self.c.itemconfig(self.sel, state="normal")
        self.c.itemconfig(self.rect, state="normal")

    def show_window_at(self, x, y):
        self.show_region(self.window_at(x, y))

    def update_cursor(self, x, y, text=None):
        c = self.c
        c.coords(self.hline, 0, y, self.W, y)
        c.coords(self.vline, x, 0, x, self.H)
        c.itemconfig(self.label, text=text or f"{x}, {y}")
        lx, ly = x + 16, y + 16
        bb = c.bbox(self.label)
        w, h = bb[2] - bb[0], bb[3] - bb[1]
        if lx + w + 10 > self.W:
            lx = x - w - 20
        if ly + h + 8 > self.H:
            ly = y - h - 20
        c.coords(self.label, lx + 5, ly + 3)
        c.coords(self.label_bg, lx, ly, lx + w + 10, ly + h + 6)
        for item in (self.hline, self.vline, self.label_bg, self.label):
            c.tag_raise(item)

    def on_motion(self, e):
        self.update_cursor(e.x, e.y)
        self.show_window_at(e.x, e.y)

    def on_press(self, e):
        self.start = (e.x, e.y)
        self.dragging = False

    def on_drag(self, e):
        if not self.start:
            return
        sx, sy = self.start
        if not self.dragging and abs(e.x - sx) + abs(e.y - sy) < 4:
            return
        self.dragging = True
        self.show_region((sx, sy, e.x, e.y))
        self.update_cursor(e.x, e.y, f"{abs(e.x - sx)} × {abs(e.y - sy)}")

    def on_release(self, e):
        if self.dragging:
            sx, sy = self.start
            x0, x1 = sorted((max(0, min(sx, e.x)), min(self.W, max(sx, e.x))))
            y0, y1 = sorted((max(0, min(sy, e.y)), min(self.H, max(sy, e.y))))
            if x1 - x0 >= 3 and y1 - y0 >= 3:
                return self.finish((x0, y0, x1, y1))
            self.start, self.dragging = None, False
            return
        box = self.window_at(e.x, e.y)
        if box:
            self.finish(box)

    def finish(self, box):
        self.top.destroy()
        self.on_done(box)
