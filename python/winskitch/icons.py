"""Toolbar and app icons, drawn with the same renderer as annotations."""
from PIL import Image, ImageDraw, ImageFilter

from . import annotations as A

FG = "#E6E6E6"
S = 64  # drawing canvas size


def _plain(ann):
    ann.shadow = ann.outline = False
    return ann


def tool_icon(tool, variant, px, has_menu=False):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    anns = []
    if tool == "arrow":
        anns = [A.Arrow(FG, 6, 8, 56, 54, 10)]
    elif tool == "text":
        anns = [A.Text(FG, 50, 14, 2, "A")]
    elif tool == "shape":
        anns = [{"rect": A.Rect(FG, 6, 10, 14, 54, 50),
                 "rrect": A.Rect(FG, 6, 10, 14, 54, 50, rounded=True),
                 "oval": A.Oval(FG, 6, 8, 14, 56, 50),
                 "line": A.Line(FG, 6, 10, 54, 54, 10)}[variant]]
    elif tool == "pen":
        pts = [(8, 44), (18, 24), (28, 40), (38, 20), (48, 36), (56, 18)]
        if variant == "highlighter":
            anns = [A.Freehand("#FDE23A", 12, [(8, 40), (56, 24)])]
        else:
            anns = [A.Freehand(FG, 5, pts)]
    elif tool == "mosaic":
        d = ImageDraw.Draw(img)
        shades = ["#8a8a8a", "#d0d0d0", "#b0b0b0", "#f0f0f0"]
        for i in range(3):
            for j in range(3):
                d.rectangle((8 + i * 16, 8 + j * 16, 23 + i * 16, 23 + j * 16), fill=shades[(i + j * 2) % 4])
        if variant == "blur":
            img = img.filter(ImageFilter.GaussianBlur(5))
    elif tool == "stamp":
        anns = [A.Stamp("#8a8a8a", 24, 32, 32, variant)]
    elif tool == "crop":
        anns = [A.Freehand(FG, 6, [(18, 4), (18, 46), (60, 46)]),
                A.Freehand(FG, 6, [(4, 18), (46, 18), (46, 60)])]
    for a in anns:
        _plain(a).render_onto(img)
    if has_menu:
        ImageDraw.Draw(img).polygon([(52, 62), (63, 62), (63, 51)], fill=FG)
    return img.resize((px, px), Image.LANCZOS)


def app_icon(px=64):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(img).rounded_rectangle((2, 2, 62, 62), radius=14, fill=(45, 45, 48, 255))
    A.Arrow("#EA3323", 6, 12, 52, 50, 14).render_onto(img)
    return img.resize((px, px), Image.LANCZOS)
