# Wallbang (bullet through a wall) icon for the killfeed: white on transparent, 48 px high, plus its .meta.
# Two ways to make it (python 3 with numpy and PIL):
#   python "Tools/Icons/make_wallbang_icon.py" --from "C:\Users\Ruslan\Downloads\icons.png"
#       cut the icon out of a picture of white icons on black (by default the top left icon,
#       or give the box in pixels: --box x0 y0 x1 y1)
#   python "Tools/Icons/make_wallbang_icon.py"
#       draw our own icon: a wall slab with a hole and a bullet flying out of it with sparks
# Output: Assets/Resources/KillIcons/wallbang.png (or --out <file>)
# The game uses OUR OWN drawing (run without --from). The --from mode is only a tool for the user's own pictures:
# do not put Counter-Strike / Valve icons into this public project.
import os, sys, math, argparse
import numpy as np
from PIL import Image, ImageDraw

HEIGHT = 48     # icon height in pixels
PAD = 2         # transparent border
SS = 4          # the own icon is drawn this many times bigger, then scaled down (smooth edges)

here = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True   # no __pycache__ next to the scripts
sys.path.insert(0, here)
from unity_meta import write_icon_meta


def to_icon(alpha):
    # alpha: float array 0..1 of any size -> 48 px high white icon, tight crop, 2 px border
    rows = np.where(alpha.max(axis=1) > 0.02)[0]
    cols = np.where(alpha.max(axis=0) > 0.02)[0]
    a = alpha[rows[0]:rows[-1] + 1, cols[0]:cols[-1] + 1]
    inner = HEIGHT - 2 * PAD
    w = max(4, int(round(a.shape[1] * inner / a.shape[0])))
    small = Image.fromarray(a.astype(np.float32), "F").resize((w, inner), Image.LANCZOS)
    small = np.clip(np.asarray(small), 0, 1)
    out = np.zeros((HEIGHT, w + 2 * PAD, 4), dtype=np.uint8)
    out[:, :, :3] = 255
    out[PAD:PAD + inner, PAD:PAD + w, 3] = np.round(small * 255).astype(np.uint8)
    return Image.fromarray(out, "RGBA")


def cut_from_picture(path, box):
    lum = np.asarray(Image.open(path).convert("L")).astype(np.float32)
    h, w = lum.shape
    if box is None:
        # the top left icon: everything bright in the top left quarter
        q = lum[:h // 2, :w // 2]
        ys, xs = np.where(q > 60)
        m = 4
        box = (max(0, xs.min() - m), max(0, ys.min() - m), xs.max() + m + 1, ys.max() + m + 1)
    x0, y0, x1, y1 = box
    part = lum[y0:y1, x0:x1]
    # black background -> transparent, white -> opaque (cuts the jpeg noise of the background)
    return np.clip((part - 24) / (230 - 24), 0, 1)


def draw_own():
    s = SS
    W, H = 52 * s, HEIGHT * s
    img = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(img)

    def P(pts):
        return [(x * s, y * s) for x, y in pts]

    # wall slab seen a bit from the side: the far edge is shorter, the thickness is a narrow strip
    d.polygon(P([(2, 3), (16, 6), (16, 42), (2, 45)]), fill=255)
    d.polygon(P([(17.2, 6.4), (20.5, 8.6), (20.5, 39.4), (17.2, 41.6)]), fill=255)

    # hole with cracks around it (cut out of the slab)
    hx, hy = 9.5, 21.0
    d.ellipse(P([(hx - 3.0, hy - 4.2), (hx + 3.0, hy + 4.2)]), fill=0)
    for ang, ln in ((-120, 7.5), (-35, 6.0), (60, 7.0), (150, 5.5), (100, 4.5)):
        a = math.radians(ang)
        x2, y2 = hx + math.cos(a) * ln, hy + math.sin(a) * ln * 1.3
        d.line(P([(hx, hy), (x2, y2)]), fill=0, width=int(0.9 * s))

    # bullet flying out to the right: a thin trail getting thicker towards the pointed tip
    y = 21.0
    d.polygon(P([(23, y - 0.5), (34, y - 1.6), (37.5, y - 1.6), (40.5, y), (37.5, y + 1.6), (34, y + 1.6), (23, y + 0.5)]), fill=255)
    for dy, x0, x1 in ((-4.2, 25.0, 31.0), (4.2, 26.5, 32.5)):
        d.line(P([(x0, y + dy), (x1, y + dy)]), fill=255, width=int(0.9 * s))

    # sparks in front of the tip
    tx = 40.5
    for ang, r0, r1 in ((-75, 3.2, 6.4), (-38, 3.4, 7.6), (0, 3.6, 8.0), (38, 3.4, 7.6), (75, 3.2, 6.4)):
        a = math.radians(ang)
        d.line(P([(tx + math.cos(a) * r0, y + math.sin(a) * r0), (tx + math.cos(a) * r1, y + math.sin(a) * r1)]),
               fill=255, width=int(1.4 * s))

    return np.asarray(img).astype(np.float32) / 255.0


parser = argparse.ArgumentParser()
parser.add_argument("--from", dest="src", help="picture with the icon (white on black)")
parser.add_argument("--box", type=int, nargs=4, help="x0 y0 x1 y1 of the icon in the picture")
parser.add_argument("--out", default=os.path.join(here, "..", "..", "Assets", "Resources", "KillIcons", "wallbang.png"))
args = parser.parse_args()

alpha = cut_from_picture(args.src, args.box) if args.src else draw_own()
icon = to_icon(alpha)
out = os.path.normpath(args.out)
os.makedirs(os.path.dirname(out), exist_ok=True)
icon.save(out)
if os.path.basename(os.path.dirname(out)) == "KillIcons":
    write_icon_meta(out)
print("wallbang icon ->", out, icon.size)
