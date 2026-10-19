# Killfeed weapon icons: a white side-view silhouette of every weapon model, muzzle to the right.
# Run from anywhere (Blender 5.2, no window):
#   "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" -b --factory-startup -P "Tools/Icons/make_kill_icons.py"
# Optional arguments after "--": models folder, output folder, names to render, e.g.
#   ... -P make_kill_icons.py -- "Assets/Resources/WeaponModels" "Assets/Resources/KillIcons" "Desert Eagle"
# Reads  Assets/Resources/WeaponModels/<weapon name>.fbx (empties Grip, Up, Muzzle give the orientation)
# Writes Assets/Resources/KillIcons/<weapon name>.png: white on transparent, 48 px high (2 px padding),
# and its .meta (GUI texture import settings, see unity_meta.py)
import bpy, sys, os, math, tempfile, mathutils
import numpy as np

NAMES = ["AK-47", "M4A4", "AWP", "MP9", "Glock-18", "USP-S", "Desert Eagle", "Knife"]
HEIGHT = 48          # icon height in pixels
PAD = 2              # transparent border
RENDER_PX = 480      # height of the silhouette in the big render (downscaled later)
# part of the icon height the silhouette may use (the knife is long and thin, it would be as wide as the AWP)
FILL = {"Knife": 0.62}

V = mathutils.Vector
here = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True   # no __pycache__ next to the scripts
sys.path.insert(0, here)
from unity_meta import write_icon_meta
project = os.path.normpath(os.path.join(here, "..", ".."))
args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
src = os.path.abspath(args[0]) if len(args) > 0 else os.path.join(project, "Assets", "Resources", "WeaponModels")
out = os.path.abspath(args[1]) if len(args) > 1 else os.path.join(project, "Assets", "Resources", "KillIcons")
names = args[2:] if len(args) > 2 else NAMES
os.makedirs(out, exist_ok=True)


def axes(objs):
    # barrel direction and up direction of the model, from its empties
    e = {o.name.split(".")[0]: o.matrix_world.translation.copy() for o in objs if o.type == "EMPTY"}
    up = V((0, 0, 1))
    if "Grip" in e and "Up" in e and (e["Up"] - e["Grip"]).length > 1e-5:
        up = (e["Up"] - e["Grip"]).normalized()
    fwd = None
    if "Grip" in e and "Muzzle" in e:
        d = e["Muzzle"] - e["Grip"]
        d = d - up * d.dot(up)
        if d.length > 1e-5: fwd = d.normalized()
    if fwd is None:
        # no empties: the longest horizontal side of the bounding box, towards +
        fwd = V((1, 0, 0))
    return fwd, up


def render_alpha(objs, fwd, up, path):
    # orthographic camera looking at the side of the gun so that fwd is screen right and up is screen up
    pts = [o.matrix_world @ v.co for o in objs if o.type == "MESH" for v in o.data.vertices]
    xs = [p.dot(fwd) for p in pts]
    ys = [p.dot(up) for p in pts]
    w, h = max(xs) - min(xs), max(ys) - min(ys)
    side = up.cross(fwd)    # camera looks along this
    centre = fwd * (max(xs) + min(xs)) / 2 + up * (max(ys) + min(ys)) / 2
    centre += side * sum(p.dot(side) for p in pts) / len(pts)

    sc = bpy.context.scene
    cam = bpy.data.objects.new("IconCam", bpy.data.cameras.new("IconCam"))
    sc.collection.objects.link(cam)
    rot = mathutils.Matrix((fwd, up, -side)).transposed()   # camera x = right, y = up, -z = view direction
    cam.matrix_world = mathutils.Matrix.Translation(centre - side * 5.0) @ rot.to_4x4()
    cam.data.type = "ORTHO"
    cam.data.clip_start = 0.01
    cam.data.clip_end = 20.0
    margin = 1.08
    cam.data.ortho_scale = max(w, h) * margin
    sc.camera = cam

    px = RENDER_PX / h
    sc.render.resolution_x = max(8, int(math.ceil(w * margin * px)))
    sc.render.resolution_y = max(8, int(math.ceil(h * margin * px)))
    sc.render.resolution_percentage = 100
    sc.render.engine = "BLENDER_WORKBENCH"
    sc.render.film_transparent = True
    sc.display.render_aa = "32"
    sh = sc.display.shading
    sh.light = "FLAT"
    sh.color_type = "SINGLE"
    sh.single_color = (1, 1, 1)
    sh.show_shadows = False
    sh.show_cavity = False
    sh.show_object_outline = False
    sh.show_xray = False
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)

    img = bpy.data.images.load(path)
    rw, rh = img.size
    buf = np.empty(rw * rh * 4, dtype=np.float32)
    img.pixels.foreach_get(buf)
    bpy.data.images.remove(img)
    # blender rows go bottom to top
    return buf.reshape(rh, rw, 4)[::-1, :, 3].copy()


def area_weights(n_in, n_out):
    # each output pixel averages the input pixels it covers (exact box filter)
    m = np.zeros((n_out, n_in), dtype=np.float64)
    s = n_in / n_out
    for i in range(n_out):
        a, b = i * s, (i + 1) * s
        j0, j1 = int(math.floor(a)), min(n_in, int(math.ceil(b)))
        for j in range(j0, j1):
            m[i, j] = min(b, j + 1) - max(a, j)
        m[i] /= m[i].sum()
    return m


def make_icon(alpha, fill):
    rows = np.where(alpha.max(axis=1) > 0.02)[0]
    cols = np.where(alpha.max(axis=0) > 0.02)[0]
    a = alpha[rows[0]:rows[-1] + 1, cols[0]:cols[-1] + 1]
    inner = HEIGHT - 2 * PAD
    th = max(4, int(round(inner * fill)))
    tw = max(4, int(round(a.shape[1] * th / a.shape[0])))
    small = area_weights(a.shape[0], th) @ a @ area_weights(a.shape[1], tw).T
    icon = np.zeros((HEIGHT, tw + 2 * PAD), dtype=np.float64)
    top = PAD + (inner - th) // 2
    icon[top:top + th, PAD:PAD + tw] = np.clip(small, 0, 1)
    return icon


def save_png(alpha, path):
    h, w = alpha.shape
    img = bpy.data.images.new("Icon", w, h, alpha=True)
    img.alpha_mode = "STRAIGHT"
    px = np.ones((h, w, 4), dtype=np.float32)
    px[:, :, 3] = alpha
    img.pixels.foreach_set(px[::-1].ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


tmp = os.path.join(tempfile.gettempdir(), "kill_icon_render.png")
done = []
for name in names:
    fbx = os.path.join(src, name + ".fbx")
    if not os.path.exists(fbx):
        print("ICON skip (no model):", name)
        continue
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        bpy.ops.import_scene.fbx(filepath=fbx)
    except Exception as ex:
        print("ICON skip (import failed):", name, ex)
        continue
    objs = list(bpy.context.scene.objects)
    if not any(o.type == "MESH" for o in objs):
        print("ICON skip (no mesh):", name)
        continue
    fwd, up = axes(objs)
    alpha = render_alpha(objs, fwd, up, tmp)
    icon = make_icon(alpha, FILL.get(name, 1.0))
    path = os.path.join(out, name + ".png")
    save_png(icon, path)
    write_icon_meta(path)
    done.append(name)
    print("ICON", name, "->", path, "%dx%d" % (icon.shape[1], icon.shape[0]))

if os.path.exists(tmp): os.remove(tmp)
print("ICONS done:", len(done), "of", len(names))
