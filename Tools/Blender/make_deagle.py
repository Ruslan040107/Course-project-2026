# Builds our own Desert Eagle model from simple shapes (no external assets) and exports it for the game.
# Run (headless):
#   blender -b --factory-startup -P Tools/Blender/make_deagle.py -- [out.fbx] [preview folder]
# Without arguments it writes Assets/Resources/WeaponModels/Desert Eagle.fbx.
# Blender axes: barrel along +X, up +Z, the right side of the gun is -Y. Metres, real size.
# Objects: DesertEagle_Body (frame, grip, guard, hammer, trigger, barrel), Slide (moves on a shot),
# Mag (base pad + magazine inside the grip) and the empties Grip, Up, Muzzle, Eject.
import bpy, bmesh, sys, os, math
from mathutils import Vector, Matrix

args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
here = os.path.dirname(os.path.abspath(__file__))
out = args[0] if len(args) > 0 else os.path.join(here, '..', '..', 'Assets', 'Resources', 'WeaponModels', 'Desert Eagle.fbx')
preview = args[1] if len(args) > 1 else None

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
T = math.tan(math.radians(18))   # grip angle
TB = math.tan(math.radians(8))   # the grip bottom is a bit lower at the front


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

STEEL = material('DE_Steel', (0.55, 0.555, 0.565), 0.9, 0.35)
DARK = material('DE_Dark', (0.05, 0.05, 0.055), 0.8, 0.45)
GRIP = material('DE_Grip', (0.02, 0.02, 0.02), 0.0, 0.8)


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


def extrude_yz(pts, x0, x1):
    # cross-section (y, z) pushed along the barrel from x0 to x1
    bm = bmesh.new()
    a = [bm.verts.new((x0, y, z)) for y, z in pts]
    b = [bm.verts.new((x1, y, z)) for y, z in pts]
    bm.faces.new(a[::-1])
    bm.faces.new(b)
    for i in range(len(pts)):
        j = (i + 1) % len(pts)
        bm.faces.new((a[i], a[j], b[j], b[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def box(c, s, rot_y=0.0):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    m = Matrix.Translation(c) @ Matrix.Rotation(math.radians(rot_y), 4, 'Y') @ Matrix.Diagonal((s[0], s[1], s[2], 1.0))
    bmesh.ops.transform(bm, matrix=m, verts=bm.verts)
    return bm


def cylinder(c, r, depth, axis='Y', segs=20):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r, radius2=r, depth=depth)
    rot = {'X': Matrix.Rotation(math.pi / 2, 4, 'Y'), 'Y': Matrix.Rotation(math.pi / 2, 4, 'X'), 'Z': Matrix.Identity(4)}[axis]
    bmesh.ops.transform(bm, matrix=Matrix.Translation(c) @ rot, verts=bm.verts)
    return bm


def tube(c, r_out, r_in, x0, x1, segs=24):
    # barrel crown along +X: steel outside and front ring, dark bore inside (material 1)
    bm = bmesh.new()

    def ring(x, r):
        return [bm.verts.new((x, c[1] + r * math.cos(2 * math.pi * k / segs), c[2] + r * math.sin(2 * math.pi * k / segs))) for k in range(segs)]
    o0, o1, i0, i1 = ring(x0, r_out), ring(x1, r_out), ring(x0, r_in), ring(x1, r_in)
    for k in range(segs):
        j = (k + 1) % segs
        bm.faces.new((o0[k], o0[j], o1[j], o1[k]))
        bm.faces.new((i0[k], i1[k], i1[j], i0[j])).material_index = 1
        bm.faces.new((o1[k], o1[j], i1[j], i1[k]))
        bm.faces.new((o0[k], i0[k], i0[j], o0[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def bevel_edges(bm, edges, width, segments=1):
    if not edges: return
    verts = list({v for e in edges for v in e.verts})
    bmesh.ops.bevel(bm, geom=list(edges) + verts, offset=width, segments=segments, profile=0.5,
                    affect='EDGES', clamp_overlap=True)


def bevel_sharp(bm, width, segments=1, angle=30):
    # small bevel on every sharp edge, so the edges catch the light
    bm.edges.ensure_lookup_table()
    edges = [e for e in bm.edges if e.is_manifold and e.calc_face_angle(0) > math.radians(angle)]
    bevel_edges(bm, edges, width, segments)


def obj(name, bm, mats, bevel=0.0006, segments=1):
    if bevel > 0: bevel_sharp(bm, bevel, segments)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in mats: me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    return ob


def cut(ob, cutters):
    # boolean difference; the new faces get the cutter's material
    for i, c in enumerate(cutters):
        md = ob.modifiers.new('cut%d' % i, 'BOOLEAN')
        md.operation = 'DIFFERENCE'
        md.solver = 'EXACT'
        md.object = c
        md.material_mode = 'TRANSFER'
        c.hide_render = True
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
    ob.modifiers.clear()
    old = ob.data
    ob.data = me
    bpy.data.meshes.remove(old)
    for c in cutters:
        m = c.data
        bpy.data.objects.remove(c)
        bpy.data.meshes.remove(m)


def cutter(bm):
    return obj('cutter', bm, [DARK], bevel=0)


def join(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = name
    ob.data.name = name
    return ob


def solve(fx, fz, z=-0.1):
    # point where the line x = fx(z) meets the line z = fz(x)
    for _ in range(30):
        x = fx(z)
        z = fz(x)
    return fx(z), z


# ---------- grip lines ----------
def F(z): return 0.100 + (z + 0.050) * T    # front strap
def B(z): return 0.0465 + (z + 0.050) * T   # back strap
ZBB = -0.1255                               # grip bottom at the back strap
XBB = B(ZBB)
def BOT(x): return ZBB - (x - XBB) * TB     # grip bottom line

ZB = -0.0125      # bore axis height (top of the slide is z = 0)
SW = 0.016        # half width of the slide


# ---------- slide (moves back on a shot) ----------
slide_pts = [(0.0, -0.034), (0.160, -0.034), (0.160, -0.020), (0.155, -0.020), (0.138, 0.0), (0.0, 0.0)]
bm = extrude_xz(slide_pts, -SW, SW)
# wide chamfer along the top and the sloped front
top = [e for e in bm.edges
       if all(abs(abs(v.co.y) - SW) < 1e-6 for v in e.verts)
       and abs(e.verts[0].co.x - e.verts[1].co.x) > 1e-4
       and (e.verts[0].co.z + e.verts[1].co.z) / 2 > -0.019]
bevel_edges(bm, top, 0.0035)
slide = obj('Slide', bm, [STEEL], bevel=0.0006)

cuts = []
for side in (-1, 1):
    # rear serrations, parallel to the grip
    for i in range(9):
        cuts.append(cutter(box((0.023 + i * 0.0033, side * 0.0166, -0.0185), (0.0013, 0.0032, 0.025), rot_y=18)))
# ejection port on the right side
cuts.append(cutter(box((0.116, -0.0175, -0.0100), (0.036, 0.007, 0.0125))))
cut(slide, cuts)

# rear sight with a U notch
rear = obj('rear_sight', extrude_xz([(0.0045, -0.0006), (0.0150, -0.0006), (0.0135, 0.0065), (0.0058, 0.0065)], -0.0105, 0.0105), [DARK], bevel=0.0004)
cut(rear, [cutter(box((0.0097, 0.0, 0.0068), (0.02, 0.0036, 0.006)))])

# ambidextrous safety at the rear of the slide
parts = [slide, rear, obj('safety_hub', cylinder((0.0105, 0.0, -0.0105), 0.0042, 0.0366, 'Y', 16), [DARK], bevel=0.0004)]
for side in (-1, 1):
    parts.append(obj('safety_lever', extrude_xz(rounded([(0.0105, -0.0078, 0.001), (0.0010, -0.0062, 0.0012), (0.0010, -0.0105, 0.0012), (0.0105, -0.0132, 0.001)]),
                                                side * 0.0163, side * 0.0181), [DARK], bevel=0.0003))
slide = join(parts, 'Slide')


# ---------- barrel (fixed, the slide covers its rear part) ----------
barrel = obj('barrel', extrude_yz([(-0.015, -0.031), (0.015, -0.031), (0.015, -0.016), (0.0055, -0.0005), (-0.0055, -0.0005), (-0.015, -0.016)], 0.095, 0.250), [STEEL], bevel=0.0006)
rib = obj('rib', extrude_yz([(-0.0042, -0.0006), (0.0042, -0.0006), (0.0042, 0.0026), (-0.0042, 0.0026)], 0.1405, 0.2490), [STEEL], bevel=0.0004)
cut(rib, [cutter(box((0.150 + i * 0.0115, 0.0, 0.0028), (0.0018, 0.012, 0.0017))) for i in range(8)])
front_sight = obj('front_sight', extrude_xz([(0.2400, 0.0020), (0.2482, 0.0020), (0.2448, 0.0092), (0.2400, 0.0092)], -0.0018, 0.0018), [DARK], bevel=0.0003)
crown = obj('crown', tube((0, 0.0, ZB), 0.0095, 0.0064, 0.2485, 0.2550), [STEEL, DARK], bevel=0.0004)
bore = obj('bore', cylinder((0.2508, 0.0, ZB), 0.0067, 0.0006, 'X', 24), [DARK], bevel=0)


# ---------- frame: rails, dust cover, grip and tail ----------
xfb, zfb = solve(F, BOT)
web = []
p0 = Vector((B(-0.064), -0.064))
p1 = p0 + Vector((math.sin(math.radians(18)), math.cos(math.radians(18)))) * 0.012
p2, p3 = Vector((0.030, -0.0445)), Vector((0.016, -0.0445))
for k in range(1, 6):
    s = k / 5
    p = p0 * (1 - s) ** 3 + p1 * 3 * s * (1 - s) ** 2 + p2 * 3 * s * s * (1 - s) + p3 * s ** 3
    web.append((p.x, p.y, 0))
frame_pts = ([(0.246, -0.029, 0), (0.246, -0.036, 0.001), (0.2345, -0.050, 0.0015),
              (F(-0.050), -0.050, 0.003),          # inside the trigger guard, to the front strap
              (xfb, zfb, 0.004), (XBB, ZBB, 0.004),  # grip bottom
              (p0.x, p0.y, 0)] + web +             # back strap curves up into the tail
             [(-0.004, -0.0435, 0), (-0.0140, -0.0410, 0.004), (-0.0128, -0.0335, 0.002),
              (-0.0105, -0.0265, 0.0015), (0.0015, -0.0265, 0), (0.0015, -0.029, 0)])
frame = obj('frame', extrude_xz(rounded(frame_pts), -0.014, 0.014), [STEEL], bevel=0.0008)

# black grip panels on both sides
pf = solve(lambda z: F(z) - 0.0055, lambda x: BOT(x) + 0.0045)
pb = solve(lambda z: B(z) + 0.0045, lambda x: BOT(x) + 0.0045)
panel_pts = rounded([(F(-0.0575) - 0.0055, -0.0575, 0.004), (pf[0], pf[1], 0.004), (pb[0], pb[1], 0.004), (B(-0.0575) + 0.0045, -0.0575, 0.004)])
panels = [obj('panel', extrude_xz(panel_pts, side * 0.0125, side * 0.0172), [GRIP], bevel=0.0012, segments=2) for side in (-1, 1)]
gx = (F(-0.092) + B(-0.092)) / 2 - 0.0005
screws = [obj('screw', cylinder((gx, side * 0.0172, -0.092), 0.0026, 0.0012, 'Y', 12), [DARK], bevel=0.0003) for side in (-1, 1)]

# big trigger guard with a squared front
guard_line = [(0.151, -0.046), (0.1525, -0.060), (0.1535, -0.073), (0.1555, -0.0800), (0.1535, -0.0870),
              (0.1485, -0.0905), (0.120, -0.0905), (0.104, -0.0895), (0.0955, -0.0860), (0.0905, -0.0800), (0.0875, -0.0740)]
guard = obj('guard', extrude_xz(band(guard_line, 0.0068), -0.0062, 0.0062), [STEEL], bevel=0.0008)
trigger = obj('trigger', extrude_xz(band([(0.1120, -0.046), (0.1128, -0.054), (0.1150, -0.062), (0.1178, -0.0685), (0.1192, -0.0735)], 0.0058), -0.0042, 0.0042), [DARK], bevel=0.0006)

# round exposed hammer at the rear
hammer = obj('hammer', cylinder((-0.0055, 0.0, -0.0225), 0.0085, 0.0075, 'Y', 20), [DARK], bevel=0.0006)

# slide stop and barrel latch on the left side, trigger pin
stop = obj('slide_stop', extrude_xz(rounded([(0.073, -0.0355, 0.001), (0.097, -0.0355, 0.001), (0.0995, -0.039, 0.0015),
                                             (0.097, -0.0425, 0.001), (0.077, -0.0425, 0.001), (0.073, -0.040, 0.001)]), 0.0136, 0.0156), [DARK], bevel=0.0004)
latch = obj('barrel_latch', cylinder((0.138, 0.0148, -0.0415), 0.0032, 0.0028, 'Y', 16), [DARK], bevel=0.0004)
pin = obj('trigger_pin', cylinder((0.1075, 0.0, -0.0445), 0.0016, 0.0292, 'Y', 10), [DARK], bevel=0)

body = join([frame, barrel, rib, front_sight, crown, bore] + panels + screws + [guard, trigger, hammer, stop, latch, pin], 'DesertEagle_Body')


# ---------- magazine: base pad below the grip and the body inside it ----------
c1 = solve(lambda z: F(z) + 0.0025, lambda x: BOT(x) + 0.0008)
c2 = solve(lambda z: F(z) + 0.0025, lambda x: BOT(x) - 0.0100)
c3 = solve(lambda z: B(z) - 0.0015, lambda x: BOT(x) - 0.0100)
c4 = solve(lambda z: B(z) - 0.0015, lambda x: BOT(x) + 0.0008)
pad = obj('pad', extrude_xz(rounded([(c1[0], c1[1], 0.001), (c2[0], c2[1], 0.003), (c3[0], c3[1], 0.003), (c4[0], c4[1], 0.001)]), -0.0155, 0.0155), [DARK], bevel=0.0007)
m1 = solve(lambda z: F(z) - 0.0045, lambda x: BOT(x) + 0.001)
m2 = solve(lambda z: B(z) + 0.0040, lambda x: BOT(x) + 0.001)
mag_body = obj('mag_body', extrude_xz(rounded([(F(-0.040) - 0.0045, -0.040, 0.002), (m1[0], m1[1], 0.002), (m2[0], m2[1], 0.002), (B(-0.040) + 0.004, -0.040, 0.002)]), -0.0105, 0.0105), [STEEL], bevel=0.0005)
mag = join([pad, mag_body], 'Mag')


# ---------- empties ----------
def empty(name, loc):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.01
    e.location = loc
    scene.collection.objects.link(e)
    return e

gz = -0.090
grip = Vector(((F(gz) + B(gz)) / 2, 0.0, gz))   # middle of the grip, where the right hand holds it
empties = [empty('Grip', grip), empty('Up', grip + Vector((0, 0, 0.1))),
           empty('Muzzle', (0.2550 + 0.005, 0.0, ZB)),           # 5 mm in front of the crown, on the bore axis
           empty('Eject', (0.116, -SW - 0.002, -0.0095))]        # ejection port, right side of the slide


# ---------- clean up, centre, shading ----------
meshes = [body, slide, mag]
pts = [v.co.copy() for o in meshes for v in o.data.vertices]
mn = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
mx = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
centre = (mn + mx) / 2
for o in meshes:
    o.data.transform(Matrix.Translation(-centre))
for e in empties:
    e.location = e.location - centre
# the moving parts get their pivot in their own middle
for o in (slide, mag):
    vs = [v.co for v in o.data.vertices]
    c = Vector(((min(v.x for v in vs) + max(v.x for v in vs)) / 2, (min(v.y for v in vs) + max(v.y for v in vs)) / 2, (min(v.z for v in vs) + max(v.z for v in vs)) / 2))
    o.data.transform(Matrix.Translation(-c))
    o.location = c

tris = 0
for o in meshes:
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bmesh.ops.triangulate(bm, faces=bm.faces, quad_method='BEAUTY', ngon_method='BEAUTY')
    for f in bm.faces: f.smooth = True
    for e in bm.edges: e.smooth = e.is_manifold and e.calc_face_angle(0) < math.radians(25)
    tris += len(bm.faces)
    bm.to_mesh(o.data)
    bm.free()

size = mx - mn
print('DEAGLE size %.4f x %.4f x %.4f m, %d triangles' % (size.x, size.y, size.z, tris))
for e in empties:
    print('DEAGLE empty %s (%.4f, %.4f, %.4f)' % (e.name, *e.location))


# ---------- export ----------
os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=out, use_selection=False, object_types={'MESH', 'EMPTY'},
                         apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                         bake_space_transform=True, add_leaf_bones=False, mesh_smooth_type='FACE')
print('DEAGLE exported', os.path.abspath(out))


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

    def shot(name, loc, target, ortho=0.0, res=(1200, 720)):
        cam.location = loc
        cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
        cam.data.type = 'ORTHO' if ortho else 'PERSP'
        if ortho: cam.data.ortho_scale = ortho
        cam.data.lens = 50
        cam.data.clip_start = 0.005
        scene.render.resolution_x, scene.render.resolution_y = res
        scene.render.filepath = os.path.join(preview, name + '.png')
        bpy.ops.render.render(write_still=True)

    shot('side_right', (0, -1, 0), (0, 0, 0), ortho=0.32)
    shot('side_left', (0, 1, 0), (0, 0, 0), ortho=0.32)
    shot('top', (0, 0, 1), (0, 0, 0), ortho=0.32, res=(1200, 400))
    shot('front', (1, 0, 0), (0, 0, 0), ortho=0.17, res=(700, 720))
    shot('three_quarter', (0.42, -0.52, 0.2), (0.0, 0.0, -0.005))
    shot('first_person', (-0.34, 0.14, 0.10), (0.08, 0.0, 0.0))
    slide.location.x -= 0.035                 # slide pulled back, the barrel shows
    shot('side_racked', (0, -1, 0), (0, 0, 0), ortho=0.32)
    slide.location.x += 0.035
