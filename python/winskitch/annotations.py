"""Annotation objects: geometry, hit testing and Skitch-style rendering.

Every annotation renders itself into a small supersampled RGBA patch
(white outline + drop shadow) that is cached until it changes.
"""
import copy
import math

from PIL import Image, ImageDraw, ImageFilter, ImageFont

WHITE = (255, 255, 255, 255)
OUTLINE = 2            # white border around strokes (px)
SHADOW_OFFSET = (1, 2)
SHADOW_BLUR = 3
SHADOW_ALPHA = 0.45

TEXT_FONTS = [r"C:\Windows\Fonts\YuGothB.ttc", r"C:\Windows\Fonts\meiryob.ttc",
              r"C:\Windows\Fonts\segoeuib.ttf"]
SYMBOL_FONTS = [r"C:\Windows\Fonts\seguisym.ttf", r"C:\Windows\Fonts\segoeuib.ttf"]

STAMPS = {"check": "✔", "cross": "✖", "question": "?", "exclaim": "!",
          "star": "★", "heart": "♥"}

_fonts = {}


def get_font(size, symbol=False):
    key = (max(1, int(size)), symbol)
    if key not in _fonts:
        for path in (SYMBOL_FONTS if symbol else TEXT_FONTS):
            try:
                _fonts[key] = ImageFont.truetype(path, key[0])
                break
            except OSError:
                continue
        else:
            _fonts[key] = ImageFont.load_default(key[0])
    return _fonts[key]


def rgba(color, alpha=255):
    c = color.lstrip("#")
    return int(c[0:2], 16), int(c[2:4], 16), int(c[4:6], 16), alpha


def is_light(color):
    r, g, b, _ = rgba(color)
    return r * 0.299 + g * 0.587 + b * 0.114 > 186


def seg_dist(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    l2 = dx * dx + dy * dy
    t = 0 if l2 == 0 else max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / l2))
    return math.hypot(px - ax - t * dx, py - ay - t * dy)


def composite_clipped(dst, src, x, y):
    """alpha_composite src onto dst at (x, y), clipped to dst."""
    sx0, sy0 = max(0, -x), max(0, -y)
    sx1, sy1 = min(src.width, dst.width - x), min(src.height, dst.height - y)
    if sx1 > sx0 and sy1 > sy0:
        dst.alpha_composite(src, dest=(x + sx0, y + sy0), source=(sx0, sy0, sx1, sy1))


def _supersample(w, h):
    a = w * h
    return 4 if a < 300_000 else 3 if a < 800_000 else 2 if a < 3_000_000 else 1


def _stroke(d, pts, width, fill):
    """Polyline with round joins and caps. pts/width are in layer pixels."""
    if len(pts) > 1:
        d.line(pts, fill=fill, width=max(1, round(width)), joint="curve")
    r = width / 2
    for x, y in (pts[0], pts[-1]):
        d.ellipse((x - r, y - r, x + r, y + r), fill=fill)


class Annotation:
    shadow = True
    outline = True

    def __init__(self, color, width):
        self.color = color
        self.width = width
        self.hidden = False
        self._cache = None

    def clone(self):
        c = copy.copy(self)
        c.__dict__ = {k: copy.deepcopy(v) for k, v in self.__dict__.items() if k != "_cache"}
        c._cache = None
        return c

    def changed(self):
        self._cache = None

    # --- geometry -----------------------------------------------------
    def bbox(self):
        raise NotImplementedError

    def pad(self):
        return self.width / 2 + OUTLINE + SHADOW_BLUR * 2 + 4

    def move(self, dx, dy):
        raise NotImplementedError

    def handles(self):
        return []

    def drag_handle(self, name, x, y):
        pass

    def hit(self, x, y, tol):
        x0, y0, x1, y1 = self.bbox()
        return x0 - tol <= x <= x1 + tol and y0 - tol <= y <= y1 + tol

    # --- rendering ----------------------------------------------------
    def draw(self, d, s, ox, oy):
        """Draw onto a layer scaled by s whose origin is image point (ox, oy)."""

    def post(self, layer):
        return layer

    def _render(self):
        x0, y0, x1, y1 = self.bbox()
        p = self.pad()
        ox, oy = math.floor(x0 - p), math.floor(y0 - p)
        w, h = math.ceil(x1 + p) - ox, math.ceil(y1 + p) - oy
        s = _supersample(w, h)
        layer = Image.new("RGBA", (w * s, h * s), (0, 0, 0, 0))
        self.draw(ImageDraw.Draw(layer), s, ox, oy)
        if s > 1:
            layer = layer.reduce(s)
        layer = self.post(layer)
        if self.shadow:
            alpha = layer.getchannel("A").filter(ImageFilter.GaussianBlur(SHADOW_BLUR))
            shadow = Image.new("RGBA", layer.size, (0, 0, 0, 0))
            shadow.putalpha(alpha.point(lambda v: int(v * SHADOW_ALPHA)))
            out = Image.new("RGBA", layer.size, (0, 0, 0, 0))
            composite_clipped(out, shadow, *SHADOW_OFFSET)
            out.alpha_composite(layer)
            layer = out
        return layer, ox, oy

    def render_onto(self, img):
        if self.hidden:
            return
        if self._cache is None:
            self._cache = self._render()
        layer, ox, oy = self._cache
        composite_clipped(img, layer, ox, oy)


class TwoPoint(Annotation):
    def __init__(self, color, width, x0, y0, x1, y1):
        super().__init__(color, width)
        self.x0, self.y0, self.x1, self.y1 = x0, y0, x1, y1

    def bbox(self):
        return (min(self.x0, self.x1), min(self.y0, self.y1),
                max(self.x0, self.x1), max(self.y0, self.y1))

    def length(self):
        return math.hypot(self.x1 - self.x0, self.y1 - self.y0)

    def move(self, dx, dy):
        self.x0 += dx; self.x1 += dx; self.y0 += dy; self.y1 += dy
        self.changed()

    def handles(self):
        return [("p0", self.x0, self.y0), ("p1", self.x1, self.y1)]

    def drag_handle(self, name, x, y):
        if name == "p0":
            self.x0, self.y0 = x, y
        else:
            self.x1, self.y1 = x, y
        self.changed()

    def hit(self, x, y, tol):
        return seg_dist(x, y, self.x0, self.y0, self.x1, self.y1) <= self.width / 2 + tol


class Line(TwoPoint):
    def draw(self, d, s, ox, oy):
        pts = [((self.x0 - ox) * s, (self.y0 - oy) * s), ((self.x1 - ox) * s, (self.y1 - oy) * s)]
        if self.outline:
            _stroke(d, pts, (self.width + 2 * OUTLINE) * s, WHITE)
        _stroke(d, pts, self.width * s, rgba(self.color))


class Arrow(TwoPoint):
    """Skitch-style arrow: tapered shaft and a wide swept-back head."""

    def _geometry(self):
        x0, y0, x1, y1, w = self.x0, self.y0, self.x1, self.y1, self.width
        L = self.length() or 0.001
        ux, uy = (x1 - x0) / L, (y1 - y0) / L
        nx, ny = -uy, ux
        head = min(L * 0.6, 12 + w * 3.6)
        hh = head * 0.64          # head half width
        back = head * 0.2         # how far the barbs sweep back
        sh = max(1.2, w * 0.55)   # shaft half width at the head
        th = max(0.8, w * 0.2)    # tail half width
        bx, by = x1 - ux * head, y1 - uy * head
        pts = [(x0 + nx * th, y0 + ny * th), (bx + nx * sh, by + ny * sh),
               (bx - ux * back + nx * hh, by - uy * back + ny * hh), (x1, y1),
               (bx - ux * back - nx * hh, by - uy * back - ny * hh),
               (bx - nx * sh, by - ny * sh), (x0 - nx * th, y0 - ny * th)]
        return pts, th

    def pad(self):
        return (12 + self.width * 3.6) * 0.64 + OUTLINE + SHADOW_BLUR * 2 + 4

    def hit(self, x, y, tol):
        return seg_dist(x, y, self.x0, self.y0, self.x1, self.y1) <= self.width + tol

    def draw(self, d, s, ox, oy):
        pts, th = self._geometry()
        pts = [((x - ox) * s, (y - oy) * s) for x, y in pts]
        tx, ty = (self.x0 - ox) * s, (self.y0 - oy) * s
        color = rgba(self.color)
        if self.outline:
            o = OUTLINE * s
            d.line(pts + [pts[0], pts[1]], fill=WHITE, width=max(1, round(2 * o)), joint="curve")
            r = th * s + o
            d.ellipse((tx - r, ty - r, tx + r, ty + r), fill=WHITE)
        d.polygon(pts, fill=color)
        r = th * s
        d.ellipse((tx - r, ty - r, tx + r, ty + r), fill=color)


class Box(Annotation):
    """Axis-aligned box with four corner handles (also used for cropping)."""

    def __init__(self, color, width, x0, y0, x1, y1):
        super().__init__(color, width)
        self.x0, self.y0, self.x1, self.y1 = x0, y0, x1, y1

    def bbox(self):
        return (min(self.x0, self.x1), min(self.y0, self.y1),
                max(self.x0, self.x1), max(self.y0, self.y1))

    def move(self, dx, dy):
        self.x0 += dx; self.x1 += dx; self.y0 += dy; self.y1 += dy
        self.changed()

    def handles(self):
        return [("x0y0", self.x0, self.y0), ("x1y0", self.x1, self.y0),
                ("x0y1", self.x0, self.y1), ("x1y1", self.x1, self.y1)]

    def drag_handle(self, name, x, y):
        setattr(self, name[:2], x)
        setattr(self, name[2:], y)
        self.changed()

    def _ring_hit(self, x, y, tol):
        x0, y0, x1, y1 = self.bbox()
        m = self.width / 2 + tol
        outer = x0 - m <= x <= x1 + m and y0 - m <= y <= y1 + m
        inner = x0 + m < x < x1 - m and y0 + m < y < y1 - m
        return outer and not inner


class Rect(Box):
    def __init__(self, color, width, x0, y0, x1, y1, rounded=False):
        super().__init__(color, width, x0, y0, x1, y1)
        self.rounded = rounded

    def hit(self, x, y, tol):
        return self._ring_hit(x, y, tol)

    def draw(self, d, s, ox, oy):
        x0, y0, x1, y1 = self.bbox()
        w = self.width
        radius = max(w * 2.5, 12) if self.rounded else w * 0.6

        def ring(expand, width, fill, r):
            box = [(x0 - ox - expand) * s, (y0 - oy - expand) * s,
                   (x1 - ox + expand) * s, (y1 - oy + expand) * s]
            r = min(r * s, (box[2] - box[0]) / 2, (box[3] - box[1]) / 2)
            d.rounded_rectangle(box, radius=max(0, r), outline=fill, width=max(1, round(width * s)))

        if self.outline:
            ring(w / 2 + OUTLINE, w + 2 * OUTLINE, WHITE, radius + OUTLINE)
        ring(w / 2, w, rgba(self.color), radius)


class Oval(Box):
    def hit(self, x, y, tol):
        x0, y0, x1, y1 = self.bbox()
        rx, ry = max((x1 - x0) / 2, 1), max((y1 - y0) / 2, 1)
        k = math.hypot((x - (x0 + x1) / 2) / rx, (y - (y0 + y1) / 2) / ry)
        return abs(k - 1) * min(rx, ry) <= self.width / 2 + tol

    def draw(self, d, s, ox, oy):
        x0, y0, x1, y1 = self.bbox()
        w = self.width

        def ring(expand, width, fill):
            d.ellipse([(x0 - ox - expand) * s, (y0 - oy - expand) * s,
                       (x1 - ox + expand) * s, (y1 - oy + expand) * s],
                      outline=fill, width=max(1, round(width * s)))

        if self.outline:
            ring(w / 2 + OUTLINE, w + 2 * OUTLINE, WHITE)
        ring(w / 2, w, rgba(self.color))


class Mosaic(Box):
    """Pixelates or blurs whatever lies beneath it. width = strength."""
    shadow = False

    def __init__(self, width, x0, y0, x1, y1, mode="pixelate"):
        super().__init__("#000000", width, x0, y0, x1, y1)
        self.mode = mode

    def render_onto(self, img):
        if self.hidden:
            return
        x0, y0, x1, y1 = (int(round(v)) for v in self.bbox())
        x0, y0, x1, y1 = max(0, x0), max(0, y0), min(img.width, x1), min(img.height, y1)
        if x1 - x0 < 2 or y1 - y0 < 2:
            return
        region = img.crop((x0, y0, x1, y1))
        if self.mode == "blur":
            region = region.filter(ImageFilter.GaussianBlur(max(6, self.width * 2)))
        else:
            b = max(6, self.width * 2)
            small = region.resize((max(1, region.width // b), max(1, region.height // b)), Image.BOX)
            region = small.resize(region.size, Image.NEAREST)
        img.paste(region, (x0, y0))


class Freehand(Annotation):
    def __init__(self, color, width, points, highlighter=False):
        super().__init__(color, width)
        self.points = list(points)
        self.highlighter = highlighter
        self.shadow = self.outline = not highlighter

    def stroke_width(self):
        return self.width * 3.5 if self.highlighter else self.width

    def bbox(self):
        xs = [p[0] for p in self.points]
        ys = [p[1] for p in self.points]
        return min(xs), min(ys), max(xs), max(ys)

    def pad(self):
        return self.stroke_width() / 2 + OUTLINE + SHADOW_BLUR * 2 + 4

    def move(self, dx, dy):
        self.points = [(x + dx, y + dy) for x, y in self.points]
        self.changed()

    def hit(self, x, y, tol):
        m = self.stroke_width() / 2 + tol
        pts = self.points
        if len(pts) == 1:
            return math.hypot(x - pts[0][0], y - pts[0][1]) <= m
        return any(seg_dist(x, y, *pts[i], *pts[i + 1]) <= m for i in range(len(pts) - 1))

    def draw(self, d, s, ox, oy):
        pts = [((x - ox) * s, (y - oy) * s) for x, y in self.points]
        sw = self.stroke_width()
        if self.outline:
            _stroke(d, pts, (sw + 2 * OUTLINE) * s, WHITE)
        _stroke(d, pts, sw * s, rgba(self.color))

    def post(self, layer):
        if self.highlighter:
            layer.putalpha(layer.getchannel("A").point(lambda v: int(v * 0.45)))
        return layer


class Text(Annotation):
    """Bold text with a contrasting outline. width = font size in px."""

    def __init__(self, color, size, x, y, text):
        super().__init__(color, size)
        self.x, self.y, self.text = x, y, text
        self._bb = None

    def changed(self):
        super().changed()
        self._bb = None

    def _stroke_px(self):
        return max(2, round(self.width / 7))

    def _spacing(self):
        return self.width * 0.2

    def bbox(self):
        if self._bb is None:
            d = ImageDraw.Draw(Image.new("L", (1, 1)))
            self._bb = d.multiline_textbbox((0, 0), self.text or " ", font=get_font(self.width),
                                            stroke_width=self._stroke_px(), spacing=self._spacing())
        b = self._bb
        return self.x + b[0], self.y + b[1], self.x + b[2], self.y + b[3]

    def pad(self):
        return self._stroke_px() + SHADOW_BLUR * 2 + 6

    def move(self, dx, dy):
        self.x += dx
        self.y += dy
        self.changed()

    def draw(self, d, s, ox, oy):
        stroke = "#222222" if is_light(self.color) else "#FFFFFF"
        d.multiline_text(((self.x - ox) * s, (self.y - oy) * s), self.text, font=get_font(self.width * s),
                         fill=rgba(self.color), stroke_width=self._stroke_px() * s,
                         stroke_fill=rgba(stroke), spacing=self._spacing() * s)


class Stamp(Annotation):
    """Round badge with a symbol. width = radius."""

    def __init__(self, color, radius, cx, cy, kind="check"):
        super().__init__(color, radius)
        self.cx, self.cy, self.kind = cx, cy, kind

    def bbox(self):
        r = self.width
        return self.cx - r, self.cy - r, self.cx + r, self.cy + r

    def pad(self):
        return SHADOW_BLUR * 2 + 4

    def move(self, dx, dy):
        self.cx += dx
        self.cy += dy
        self.changed()

    def handles(self):
        k = self.width * 0.7071
        return [("r", self.cx + k, self.cy + k)]

    def drag_handle(self, name, x, y):
        self.width = max(8, math.hypot(x - self.cx, y - self.cy))
        self.changed()

    def hit(self, x, y, tol):
        return math.hypot(x - self.cx, y - self.cy) <= self.width + tol

    def draw(self, d, s, ox, oy):
        cx, cy, r = (self.cx - ox) * s, (self.cy - oy) * s, self.width * s
        ring = max(2 * s, r * 0.09)
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=WHITE)
        r2 = r - ring
        d.ellipse((cx - r2, cy - r2, cx + r2, cy + r2), fill=rgba(self.color))
        glyph = STAMPS.get(self.kind, "?")
        symbol = glyph not in "?!"
        fg = rgba("#222222") if is_light(self.color) else WHITE
        d.text((cx, cy), glyph, font=get_font(r * (1.0 if symbol else 1.2), symbol=symbol),
               fill=fg, anchor="mm")
