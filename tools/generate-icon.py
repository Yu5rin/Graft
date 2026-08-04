"""Graft ロゴを 256 単位グリッドのベクター定義から描画し、ICO / PNG を生成する。"""
import sys
from PIL import Image, ImageDraw

S = 256
BG = "#FBFAF2"
DARK = "#3E4F43"
GREEN = "#7CAA4E"

# --- パス定義（サブパスのリスト。各要素は ('L', x, y) or ('C', x1,y1,x2,y2,x,y)） ---

TRUNK = [
    ('M', 78.5, 229.5),
    ('L', 78.5, 149.0),
    ('C', 78.5, 145.0, 80.0, 141.5, 83.0, 139.0),
    ('L', 100.0, 125.0),
    ('C', 103.5, 122.0, 109.5, 124.5, 109.5, 129.0),
    ('L', 109.5, 161.0),
    ('L', 117.0, 161.0),
    ('C', 121.0, 161.0, 124.0, 164.0, 124.0, 168.0),
    ('L', 124.0, 175.0),
    ('C', 124.0, 179.0, 121.0, 182.0, 117.0, 182.0),
    ('L', 109.5, 182.0),
    ('L', 109.5, 229.5),
    ('Z',),
]

STALK = [
    ('M', 109.5, 127.0),
    ('C', 126.0, 127.0, 138.0, 118.0, 144.0, 104.0),
    ('C', 148.0, 96.0, 149.5, 90.0, 150.0, 83.0),
    ('L', 163.5, 86.0),
    ('C', 162.0, 96.0, 159.0, 112.0, 150.0, 126.0),
    ('C', 140.0, 141.0, 126.0, 152.0, 109.5, 158.0),
    ('Z',),
]

LEAF_BIG = [
    ('M', 150.0, 88.0),
    ('C', 145.0, 62.0, 164.0, 37.0, 196.0, 26.0),
    ('C', 207.0, 50.0, 197.0, 77.0, 170.0, 89.0),
    ('C', 163.0, 92.0, 154.0, 93.0, 150.0, 88.0),
    ('Z',),
]

LEAF_BIG_VEIN = [
    ('M', 196.0, 26.0),
    ('C', 176.0, 44.0, 163.0, 63.0, 157.0, 85.0),
    ('C', 161.0, 62.0, 178.0, 42.0, 196.0, 26.0),
    ('Z',),
]

LEAF_SMALL = [
    ('M', 152.0, 122.0),
    ('C', 163.0, 100.0, 189.0, 90.0, 210.0, 98.0),
    ('C', 203.0, 120.0, 180.0, 132.0, 158.0, 128.0),
    ('C', 154.0, 127.0, 151.0, 125.0, 152.0, 122.0),
    ('Z',),
]

LEAF_SMALL_VEIN = [
    ('M', 210.0, 98.0),
    ('C', 191.0, 100.0, 173.0, 108.0, 158.0, 122.0),
    ('C', 174.0, 112.0, 191.0, 103.0, 210.0, 98.0),
    ('Z',),
]


def flatten(path, steps=48):
    """パスをポリゴンの点列へ変換する。"""
    pts = []
    cur = (0.0, 0.0)
    for seg in path:
        op = seg[0]
        if op == 'M':
            cur = (seg[1], seg[2])
            pts.append(cur)
        elif op == 'L':
            cur = (seg[1], seg[2])
            pts.append(cur)
        elif op == 'C':
            x0, y0 = cur
            x1, y1, x2, y2, x3, y3 = seg[1:]
            for i in range(1, steps + 1):
                t = i / steps
                u = 1 - t
                x = u*u*u*x0 + 3*u*u*t*x1 + 3*u*t*t*x2 + t*t*t*x3
                y = u*u*u*y0 + 3*u*u*t*y1 + 3*u*t*t*y2 + t*t*t*y3
                pts.append((x, y))
            cur = (x3, y3)
        elif op == 'Z':
            pass
    return pts


def render(size):
    ss = 8  # スーパーサンプリング倍率
    n = size * ss
    k = n / S
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(44 * k)
    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=r, fill=BG)

    def draw(path, color):
        d.polygon([(x * k, y * k) for x, y in flatten(path)], fill=color)

    draw(TRUNK, DARK)
    draw(STALK, GREEN)
    draw(LEAF_BIG, GREEN)
    draw(LEAF_SMALL, GREEN)
    draw(LEAF_BIG_VEIN, BG)
    draw(LEAF_SMALL_VEIN, BG)
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "preview.png"
    render(512).save(out)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    imgs = [render(s) for s in sizes]
    imgs[-1].save(sys.argv[2] if len(sys.argv) > 2 else "Graft.ico",
                  format="ICO", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
    print("生成完了")
