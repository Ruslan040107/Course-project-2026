# Builds our own Zeus x27 model (a compact yellow and black taser) from simple shapes, no external assets.
# Run (headless):
#   blender -b --factory-startup -P Tools/Blender/make_zeus.py -- [out.fbx] [preview folder]
# Without arguments it writes Assets/Resources/WeaponModels/Zeus x27.fbx.
# Blender axes: barrel along +X, up +Z, the right side of the gun is -Y. Metres, real size.
# Objects: ZeusX27_Body (housing, cartridge with the electrodes, grip, guard, trigger) and the empties
# Grip, Up, Muzzle (between the two electrodes). No Mag and no Eject: it is recharged, not reloaded.
import bpy, bmesh, sys, os, math
from mathutils import Vector, Matrix

args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
here = os.path.dirname(os.path.abspath(__file__))
out = args[0] if len(args) > 0 else os.path.join(here, '..', '..', 'Assets', 'Resources', 'WeaponModels', 'Zeus x27.fbx')
preview = args[1] if len(args) > 1 else None

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene


# ---------- materials ----------
def material(name, color, metallic, roughness):
    m = bpy.data.materials.new(name)
    if m.node_tree is None or m.node_tree.nodes.get('Principled BSDF') is None:
        m.use_nodes = True
    b = m.node_tree.nodes.get('Principled BSDF')
    b.inputs['Base Color'].default_value = (*color, 1.0)
    b.inputs['Metallic'].default_value = metallic
    b.inputs['Roughness'].default_value = roughness
    b.inputs['Alpha'].default_value = 1.0          # opaque, Unity must not import it as transparent
    m.diffuse_color = (*color, 1.0)                # colour in the viewport and in workbench renders
    m.metallic, m.roughness = metallic, roughness
    if hasattr(m, 'blend_method'): m.blend_method = 'OPAQUE'
    if hasattr(m, 'surface_render_method'): m.surface_render_method = 'DITHERED'
    return m

YELLOW = material('Zeus_Yellow', (0.85, 0.58, 0.02), 0.0, 0.45)
BLACK = material('Zeus_Black', (0.018, 0.018, 0.02), 0.0, 0.6)
RUBBER = material('Zeus_Rubber', (0.03, 0.03, 0.032), 0.0, 0.9)
METAL = material('Zeus_Metal', (0.55, 0.56, 0.58), 0.9, 0.3)
RED = material('Zeus_Red', (0.6, 0.02, 0.01), 0.0, 0.4)


# ---------- geometry helpers ----------
def rounded(pts, segs=3):
    # closed outline from (a, b, r) points: a corner with r > 0 becomes an arc
    res = []
    n = len(pts)
    for i in range(n):
        a, b, r = pts[i]
        p = Vector((a, b))
        if r <= 0:
            res.append(p)
            continue
        da = Vector(pts[i - 1][:2]) - p
        db = Vector(pts[(i + 1) % n][:2]) - p
        ang = da.angle(db)
        if ang > 3.0:   # almost straight, nothing to round
            res.append(p)
            continue
        d = min(r / math.tan(ang / 2), 0.45 * da.length, 0.45 * db.length)
        p0 = p + da.normalized() * d
        p1 = p + db.normalized() * d
        for k in range(segs + 1):
            s = k / segs
            res.append(p0 * (1 - s) ** 2 + p * 2 * s * (1 - s) + p1 * s * s)
    return [tuple(v) for v in res]


def band(pts, th):
    # outline of a strip of thickness th along an open polyline (trigger guard, trigger)
    P = [Vector(p) for p in pts]
    n = len(P)
    left, right = [], []
    for i in range(n):
        d1 = (P[i] - P[i - 1]).normalized() if i > 0 else None
        d2 = (P[i + 1] - P[i]).normalized() if i < n - 1 else None
        d = ((d1 or d2) + (d2 or d1)).normalized()
        nrm = Vector((-d.y, d.x))
        k = 1.0
        if d1 and d2:
            k = 1.0 / max(0.4, nrm.dot(Vector((-d1.y, d1.x))))
        left.append(P[i] + nrm * th / 2 * k)
        right.append(P[i] - nrm * th / 2 * k)
    return [tuple(v) for v in left + right[::-1]]


def extrude_xz(pts, y0, y1):
    # side profile (x, z) pushed across the gun from y0 to y1
    bm = bmesh.new()
    a = [bm.verts.new((x, y0, z)) for x, z in pts]
    b = [bm.verts.new((x, y1, z)) for x, z in pts]
    bm.faces.new(a[::-1])
    bm.faces.new(b)
    for i in range(len(pts)):
        j = (i + 1) % len(pts)
        bm.faces.new((a[i], a[j], b[j], b[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def box(c, s):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(c) @ Matrix.Diagonal((s[0], s[1], s[2], 1.0)), verts=bm.verts)
    return bm


def cylinder(c, r, depth, axis='Y', segs=20, r1=None):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r, radius2=r if r1 is None else r1, depth=depth)
    rot = {'X': Matrix.Rotation(math.pi / 2, 4, 'Y'), 'Y': Matrix.Rotation(math.pi / 2, 4, 'X'), 'Z': Matrix.Identity(4)}[axis]
    bmesh.ops.transform(bm, matrix=Matrix.Translation(c) @ rot, verts=bm.verts)
    return bm


def bevel_sharp(bm, width, segments=1, angle=30):
    # small bevel on every sharp edge, so the edges catch the light
    bm.edges.ensure_lookup_table()
    edges = [e for e in bm.edges if e.is_manifold and e.calc_face_angle(0) > math.radians(angle)]
    if not edges: return
    verts = list({v for e in edges for v in e.verts})
    bmesh.ops.bevel(bm, geom=edges + verts, offset=width, segments=segments, profile=0.5, affect='EDGES', clamp_overlap=True)


def obj(name, bm, mats, bevel=0.0006, segments=1):
    if bevel > 0: bevel_sharp(bm, bevel, segments)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in mats: me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    return ob


def join(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = name
    ob.data.name = name
    return ob


HW = 0.016          # half width of the housing
ZE = 0.0225         # height of the electrodes (the "bore")
parts = []

# ---------- yellow housing ----------
housing = [(0.000, 0.004, 0.004), (0.000, 0.036, 0.006), (0.012, 0.045, 0.004), (0.104, 0.045, 0.003),
           (0.116, 0.039, 0.002), (0.116, 0.003, 0.002), (0.100, 0.000, 0.002), (0.056, 0.000, 0.0)]
parts.append(obj('housing', extrude_xz(rounded(housing), -HW, HW), [YELLOW], bevel=0.0012, segments=2))

# black side panels with a red charge light, black top strip with sights
for side in (-1, 1):
    panel = rounded([(0.030, 0.012, 0.003), (0.092, 0.012, 0.003), (0.092, 0.032, 0.003), (0.030, 0.032, 0.003)])
    parts.append(obj('panel', extrude_xz(panel, side * HW, side * (HW + 0.0009)), [BLACK], bevel=0.0003))
    parts.append(obj('switch', box((0.020, side * (HW + 0.0012), 0.030), (0.010, 0.0024, 0.005)), [BLACK], bevel=0.0005))
parts.append(obj('top_strip', box((0.058, 0.0, 0.0465), (0.090, 0.010, 0.004)), [BLACK], bevel=0.0006))
parts.append(obj('rear_sight', box((0.016, 0.0, 0.0500), (0.006, 0.012, 0.005)), [BLACK], bevel=0.0006))
parts.append(obj('front_sight', box((0.100, 0.0, 0.0500), (0.005, 0.004, 0.005)), [BLACK], bevel=0.0005))
parts.append(obj('led', cylinder((0.006, 0.0, 0.0415), 0.0022, 0.004, 'Z', 12), [RED], bevel=0))

# ---------- black cartridge in front with two electrodes ----------
cart = rounded([(0.112, 0.003, 0.002), (0.150, 0.003, 0.004), (0.150, 0.042, 0.004), (0.112, 0.042, 0.002)])
parts.append(obj('cartridge', extrude_xz(cart, -0.0145, 0.0145), [BLACK], bevel=0.0010, segments=2))
parts.append(obj('cartridge_band', box((0.1195, 0.0, 0.0225), (0.004, 0.0302, 0.0402)), [YELLOW], bevel=0.0004))
parts.append(obj('face', box((0.1515, 0.0, 0.0225), (0.003, 0.024, 0.034)), [RUBBER], bevel=0.0005))
for z in (ZE - 0.010, ZE + 0.010):
    parts.append(obj('electrode', cylinder((0.1565, 0.0, z), 0.0024, 0.007, 'X', 12, r1=0.0016), [METAL], bevel=0))
    parts.append(obj('electrode_ring', cylinder((0.1532, 0.0, z), 0.0042, 0.0016, 'X', 16), [METAL], bevel=0))

# ---------- grip, guard, trigger ----------
grip_pts = [(0.054, 0.002, 0.0), (0.040, -0.070, 0.006), (0.034, -0.078, 0.004), (-0.010, -0.076, 0.004),
            (-0.013, -0.068, 0.004), (0.002, 0.004, 0.0)]
parts.append(obj('grip', extrude_xz(rounded(grip_pts), -0.0145, 0.0145), [RUBBER], bevel=0.0015, segments=2))
for side in (-1, 1):
    # yellow strips on the grip sides
    strip = rounded([(0.044, -0.016, 0.002), (0.040, -0.060, 0.002), (0.034, -0.060, 0.002), (0.038, -0.016, 0.002)])
    parts.append(obj('grip_strip', extrude_xz(strip, side * 0.0145, side * 0.0153), [YELLOW], bevel=0.0002))
parts.append(obj('base', extrude_xz(rounded([(0.036, -0.074, 0.002), (0.034, -0.082, 0.002), (-0.012, -0.080, 0.002), (-0.013, -0.072, 0.002)]),
                                    -0.0152, 0.0152), [BLACK], bevel=0.0008))
guard_line = [(0.052, -0.002), (0.055, -0.020), (0.062, -0.030), (0.090, -0.030), (0.100, -0.022), (0.104, -0.001)]
parts.append(obj('guard', extrude_xz(band(guard_line, 0.0055), -0.0055, 0.0055), [BLACK], bevel=0.0007))
trig = [(0.078, -0.001), (0.077, -0.010), (0.073, -0.018), (0.069, -0.022)]
parts.append(obj('trigger', extrude_xz(band(trig, 0.0050), -0.0035, 0.0035), [YELLOW], bevel=0.0005))

body = join(parts, 'ZeusX27_Body')


# ---------- empties ----------
def empty(name, loc):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.01
    e.location = loc
    scene.collection.objects.link(e)
    return e

gz = -0.035
fx = 0.054 + (gz - 0.002) / (-0.072) * (0.040 - 0.054)     # front of the grip at this height
bx = 0.002 + (gz - 0.004) / (-0.072) * (-0.013 - 0.002)    # back of the grip
grip = Vector(((fx + bx) / 2, 0.0, gz))
empties = [empty('Grip', grip), empty('Up', grip + Vector((0, 0, 0.1))),
           empty('Muzzle', (0.161, 0.0, ZE))]               # between the electrodes


# ---------- clean up, centre, shading ----------
vs = [v.co.copy() for v in body.data.vertices]
mn = Vector((min(p.x for p in vs), min(p.y for p in vs), min(p.z for p in vs)))
mx = Vector((max(p.x for p in vs), max(p.y for p in vs), max(p.z for p in vs)))
centre = (mn + mx) / 2
body.data.transform(Matrix.Translation(-centre))
for e in empties:
    e.location = e.location - centre

bm = bmesh.new()
bm.from_mesh(body.data)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
bmesh.ops.triangulate(bm, faces=bm.faces, quad_method='BEAUTY', ngon_method='BEAUTY')
for f in bm.faces: f.smooth = True
for e in bm.edges: e.smooth = e.is_manifold and e.calc_face_angle(0) < math.radians(25)
tris = len(bm.faces)
bm.to_mesh(body.data)
bm.free()

size = mx - mn
print('ZEUS size %.4f x %.4f x %.4f m, %d triangles' % (size.x, size.y, size.z, tris))
for e in empties:
    print('ZEUS empty %s (%.4f, %.4f, %.4f)' % (e.name, *e.location))


# ---------- export ----------
os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=out, use_selection=False, object_types={'MESH', 'EMPTY'},
                         apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                         bake_space_transform=True, add_leaf_bones=False, mesh_smooth_type='FACE')
print('ZEUS exported', os.path.abspath(out))


# ---------- preview renders (optional) ----------
if preview:
    os.makedirs(preview, exist_ok=True)
    world = bpy.data.worlds.new('preview')
    world.color = (0.18, 0.2, 0.23)
    scene.world = world
    scene.render.engine = 'BLENDER_WORKBENCH'
    sh = scene.display.shading
    sh.color_type = 'MATERIAL'
    sh.light = 'STUDIO'
    sh.show_cavity = True
    sh.cavity_type = 'BOTH'
    sh.show_specular_highlight = True
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = 'ORTHO'
    cam.data.ortho_scale = max(size.x, size.z) * 1.15
    scene.render.resolution_x, scene.render.resolution_y = 900, 700
    for tag, d in (('side_right', (0, -1, 0)), ('side_left', (0, 1, 0)), ('three_quarter', (0.6, -1.0, 0.45))):
        d = Vector(d).normalized()
        cam.location = d * 1.0
        cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(preview, 'zeus_%s.png' % tag)
        bpy.ops.render.render(write_still=True)
