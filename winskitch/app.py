"""Application wiring: main window, global hotkeys, tray icon and snapping."""
import os
import queue
import tempfile
import tkinter as tk
import traceback
from tkinter import messagebox

import pystray
from PIL import ImageGrab, ImageTk
from tkinterdnd2 import TkinterDnD

from . import icons, win32
from .capture import SnapOverlay
from .editor import Editor

HOTKEYS = {
    1: (win32.MOD_CONTROL | win32.MOD_SHIFT, ord("5"), "region"),
    2: (win32.MOD_CONTROL | win32.MOD_SHIFT, ord("6"), "full"),
}
LOG = os.path.join(tempfile.gettempdir(), "winskitch.log")


class App:
    def __init__(self):
        self.root = root = TkinterDnD.Tk()
        root.title("WinSkitch")
        root.geometry(f"{round(1000 * root.winfo_fpixels('1i') / 96)}x{round(700 * root.winfo_fpixels('1i') / 96)}")
        root.minsize(480, 360)
        self._icon = ImageTk.PhotoImage(icons.app_icon(64))
        root.iconphoto(True, self._icon)
        root.report_callback_exception = self._on_error

        self.q = queue.Queue()
        self.snapping = False
        self.last_rect = None
        self.hide_hint_shown = False
        self.editor = Editor(root, on_snap=self.snap)

        root.protocol("WM_DELETE_WINDOW", self.hide)
        root.bind("<<HideWindow>>", lambda e: self.hide())
        root.bind("<<QuitApp>>", lambda e: self.quit())
        root.bind("<<ShowWindow>>", lambda e: self.show())

        failed = win32.start_hotkeys({hid: (m, vk) for hid, (m, vk, _) in HOTKEYS.items()},
                                     lambda hid: self.q.put(("hotkey", hid)))
        if failed:
            self.editor.show_status("⚠ ホットキーを登録できませんでした（他のアプリが使用中の可能性）", 0)
        self._start_tray()
        root.after(50, self._poll)

    def _start_tray(self):
        put = self.q.put
        menu = pystray.Menu(
            pystray.MenuItem("範囲スナップ (Ctrl+Shift+5)", lambda: put(("snap", "region")), default=True),
            pystray.MenuItem("全画面スナップ (Ctrl+Shift+6)", lambda: put(("snap", "full"))),
            pystray.MenuItem("タイマースナップ (5秒後)", lambda: put(("snap", "timer"))),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("ウィンドウを表示", lambda: put(("show", None))),
            pystray.MenuItem("終了", lambda: put(("quit", None))),
        )
        self.tray = pystray.Icon("WinSkitch", icons.app_icon(64), "WinSkitch", menu)
        self.tray.run_detached()

    def _poll(self):
        try:
            while True:
                kind, arg = self.q.get_nowait()
                if kind == "hotkey":
                    self.snap(HOTKEYS[arg][2])
                elif kind == "snap":
                    self.snap(arg)
                elif kind == "show":
                    self.show()
                elif kind == "quit":
                    return self.quit()
        except queue.Empty:
            pass
        self.root.after(50, self._poll)

    def _on_error(self, *exc):
        with open(LOG, "a", encoding="utf-8") as f:
            f.write("".join(traceback.format_exception(*exc)) + "\n")
        self.editor.show_status(f"エラー: {exc[1]}  (詳細: {LOG})", 8000)

    # ------------------------------------------------------------ window
    def show(self):
        r = self.root
        r.deiconify()
        r.lift()
        r.attributes("-topmost", True)
        r.after(200, lambda: r.attributes("-topmost", False))
        r.focus_force()

    def hide(self):
        self.editor.commit_text()
        self.root.withdraw()
        if not self.hide_hint_shown:
            self.hide_hint_shown = True
            try:
                self.tray.notify("タスクトレイで動作中です。Ctrl+Shift+5 でスナップできます。", "WinSkitch")
            except Exception:
                pass

    def quit(self):
        try:
            self.tray.stop()
        except Exception:
            pass
        self.root.destroy()

    # -------------------------------------------------------------- snap
    def snap(self, mode):
        if self.snapping:
            return
        self.editor.commit_text()
        self.snapping = True
        self.was_visible = self.root.state() != "withdrawn"
        self.root.withdraw()
        if mode == "timer":
            self._countdown(5)
        else:
            self.root.after(250, lambda: self._grab(mode))

    def _countdown(self, n):
        top = tk.Toplevel(self.root)
        top.overrideredirect(True)
        top.attributes("-topmost", True, "-alpha", 0.85)
        label = tk.Label(top, font=("Segoe UI", 36, "bold"), fg="white", bg="#202020", padx=30, pady=10)
        label.pack()
        top.update_idletasks()
        top.geometry(f"+{(top.winfo_screenwidth() - top.winfo_reqwidth()) // 2}+60")

        def tick(i):
            if i == 0:
                top.destroy()
                self.root.after(200, lambda: self._grab("region"))
                return
            label.configure(text=str(i))
            top.after(1000, tick, i - 1)

        tick(n)

    def _grab(self, mode):
        vx, vy, _, _ = win32.virtual_screen()
        shot = ImageGrab.grab(all_screens=True)
        if mode == "full":
            l, t, r, b = win32.monitor_rect_at_cursor()
            return self._finish(shot.crop((l - vx, t - vy, r - vx, b - vy)))
        if mode == "previous" and self.last_rect:
            return self._finish(shot.crop(self.last_rect))
        SnapOverlay(self.root, shot, (vx, vy), win32.visible_window_rects(),
                    lambda box: self._overlay_done(shot, box))

    def _overlay_done(self, shot, box):
        if box is None:
            self.snapping = False
            if self.was_visible:
                self.show()
            return
        self.last_rect = box
        self._finish(shot.crop(box))

    def _finish(self, img):
        self.snapping = False
        self.editor.load_image(img)
        self.show()


def main():
    win32.set_dpi_aware()
    if win32.already_running():
        r = tk.Tk()
        r.withdraw()
        messagebox.showinfo("WinSkitch", "WinSkitch はすでに起動しています（タスクトレイ）。\nCtrl+Shift+5 でスナップできます。")
        r.destroy()
        return
    App().root.mainloop()
