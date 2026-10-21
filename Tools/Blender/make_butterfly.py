# Builds our own butterfly knife (balisong) from simple shapes, no external assets, and exports it as the Knife.
# Run (headless):
#   blender -b --factory-startup -P Tools/Blender/make_butterfly.py -- [out.fbx] [preview folder]
# Without arguments it writes Assets/Resources/WeaponModels/Knife.fbx.
# Blender axes: the blade along +X (spine up +Z, edge down), the right side is -Y. Metres, real size.
# Open pose: the blade points forward, the two handles lie together behind it and form the grip.
# Objects:
#   Blade   - blade and tang, its origin in the middle between the two pins;
#   HandleA - the safe handle (-Y side), its origin on its pin PivotA (spine side of the tang);
#   HandleB - the bite handle (+Y side) with the latch, its origin on its pin PivotB (edge side of the tang);
#   empties PivotA, PivotB (the pins), Grip, Up, Muzzle (the blade tip).
# Flipping: a handle turns about its pin around the axis across the blade (Blender Y, i.e. the normal of the plane
# through Grip, Up and Muzzle); +-180 degrees brings it from the open pose to the closed one over the blade.
import bpy, bmesh, sys, os, math
from mathutils import Vector, Matrix

args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
here = os.path.dirname(os.path.abspath(__file__))
out = args[0] if len(args) > 0 else os.path.join(here, '..', '..', 'Assets', 'Resources', 'WeaponModels', 'Knife.fbx')
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

STEEL = material('BF_Steel', (0.5, 0.51, 0.53), 0.9, 0.3)
EDGE = material('BF_Edge', (0.78, 0.79, 0.8), 0.95, 0.15)
HANDLE = material('BF_Handle', (0.09, 0.09, 0.1), 0.7, 0.45)
PIN = material('BF_Pin', (0.6, 0.42, 0.12), 0.9, 0.3)
BLACK = material('BF_Black', (0.018, 0.018, 0.02), 0.0, 0.6)


# ---------- geometry helpers ----------
def rounded(pts, segs=3):
    # closed outline from (a, b, r) points: a corner with r > 0 becomes an arc;
    # returns the points and, for each one, the index of the corner it came from
    res, src = [], []
    n = len(pts)
    for i in range(n):
        a, b, r = pts[i]
        p = Vector((a, b))
        if r <= 0:
            res.append(p); src.append(i)
            continue
        da = Vector(pts[i - 1][:2]) - p
        db = Vector(pts[(i + 1) % n][:2]) - p
        ang = da.angle(db)
        if ang > 3.0:   # almost straight, nothing to round
            res.append(p); src.append(i)
            continue
        d = min(r / math.tan(ang / 2), 0.45 * da.length, 0.45 * db.length)
        p0 = p + da.normalized() * d
        p1 = p + db.normalized() * d
        for k in range(segs + 1):
            s = k / segs
            res.append(p0 * (1 - s) ** 2 + p * 2 * s * (1 - s) + p1 * s * s); src.append(i)
    return [tuple(v) for v in res], src


def extrude_xz(pts, y0, y1):
    # side profile (x, z) pushed across from y0 to y1; verts 0..n-1 are at y0, n..2n-1 at y1
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


def cylinder(c, r, depth, axis='Y', segs=16):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r, radius2=r, depth=depth)
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


def obj(name, bm, mats, bevel=0.0004, segments=1):
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


def join(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = name
    ob.data.name = name
    return ob


PIN_A = Vector((0.0, 0.0, 0.0020))    # safe handle pin, spine side of the tang
PIN_B = Vector((0.0, 0.0, -0.0020))   # bite handle pin, edge side of the tang
BT = 0.0016                           # half thickness of the blade
TIP = Vector((0.1075, 0.0, 0.0035))


# ---------- blade: tang, clip point, sharpened edge ----------
blade_pts = [(-0.0080, 0.0085, 0.004),    # 0 tang, rear top
             (0.0700, 0.0100, 0.0),       # 1 spine
             (0.0780, 0.0100, 0.002),     # 2 start of the swedge
             (TIP.x, TIP.z, 0.0006),      # 3 tip
             (0.0950, -0.0045, 0.008),    # 4 belly
             (0.0600, -0.0105, 0.010),    # 5 edge
             (0.0200, -0.0105, 0.0),      # 6 rear end of the edge
             (0.0165, -0.0080, 0.0015),   # 7 choil
             (0.0135, -0.0080, 0.0015),
             (0.0125, -0.0095, 0.0),
             (-0.0080, -0.0085, 0.004)]   # 10 tang, rear bottom
outline, src = rounded(blade_pts)
n = len(outline)
bm = extrude_xz(outline, -BT, BT)
bm.verts.ensure_lookup_table()
# how thick each outline point stays: the edge and the tip are ground thin, the swedge tapers to the tip
thin = {}
for i in range(n):
    c = src[i]
    f = 1.0
    if c == 3: f = 0.15
    elif c in (4, 5, 6): f = 0.12
    thin[i] = f
# the grind line along the blade: everything below it is the bevel of the edge
geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
res = bmesh.ops.bisect_plane(bm, geom=geom, plane_co=(0, 0, -0.0035), plane_no=(0, 0, 1))
bm.verts.ensure_lookup_table()
for i in range(n):
    for v in (bm.verts[i], bm.verts[n + i]):
        v.co.y *= thin[i]
for v in bm.verts:
    # the last 2 cm to the tip get thinner
    if v.co.x > 0.085:
        v.co.y *= max(0.35, 1.0 - (v.co.x - 0.085) / 0.0225 * 0.65)
# the bevel faces (below the grind line, in front of the choil) get the bright edge material
for f in bm.faces:
    c = f.calc_center_median()
    if c.z < -0.0035 and c.x > 0.019 and abs(f.normal.y) > 0.2:
        f.material_index = 1
blade = obj('Blade', bm, [STEEL, EDGE], bevel=0)
# a round hole in the blade, like the ones on the real knives
cutters = [obj('c', cylinder((0.030, 0.0, 0.0030), 0.0032, 0.02, 'Y', 20), [STEEL], bevel=0)]
cut(blade, cutters)


# ---------- handles ----------
HL0, HL1 = -0.1150, 0.0105        # rear and front end of a handle (open pose)
HH = 0.0102                       # half height
HT = 0.0075                       # thickness
GAP = 0.0003                      # gap between the blade and a handle


def handle(name, side, pin):
    y_in = side * (BT + GAP)
    y_out = side * (BT + GAP + HT)
    prof, _ = rounded([(HL0, -HH, 0.004), (HL1, -HH, 0.009), (HL1, HH, 0.009), (HL0, HH, 0.004)], segs=4)
    h = obj(name, extrude_xz(prof, min(y_in, y_out), max(y_in, y_out)), [HANDLE], bevel=0.0007, segments=2)
    # skeleton slots through the handle
    cut(h, [obj('c', box((x, side * (BT + GAP + HT / 2), 0.0), (0.020, HT * 3, 0.0085)), [BLACK], bevel=0)
            for x in (-0.026, -0.054, -0.082)])
    parts = [h]
    # the pin with a head on the outside, and a smaller screw at the butt
    parts.append(obj('pin', cylinder((pin.x, side * (BT + GAP + HT / 2 + 0.0004), pin.z), 0.0032, HT + 0.0008, 'Y', 16), [PIN], bevel=0.0003))
    parts.append(obj('screw', cylinder((HL0 + 0.0065, side * (BT + GAP + HT / 2 + 0.0003), 0.0), 0.0022, HT + 0.0006, 'Y', 12), [PIN], bevel=0.0002))
    return parts


ha = join(handle('HandleA', -1, PIN_A), 'HandleA')
hb_parts = handle('HandleB', 1, PIN_B)
# the latch on the butt of the bite handle reaches over to the safe handle and locks the two together
hb_parts.append(obj('latch', box((HL0 + 0.0035, -0.0012, 0.0), (0.0055, 0.0185, 0.0075)), [BLACK], bevel=0.0005))
hb_parts.append(obj('latch_pin', cylinder((HL0 + 0.0035, 0.0, 0.0), 0.0012, 0.0205, 'Y', 10), [PIN], bevel=0))
hb = join(hb_parts, 'HandleB')


# ---------- empties ----------
def empty(name, loc):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.01
    e.location = loc
    scene.collection.objects.link(e)
    return e

grip = Vector(((HL0 + 0.0) / 2 - 0.004, 0.0, 0.0))     # middle of the closed-together handles
empties = [empty('Grip', grip), empty('Up', grip + Vector((0, 0, 0.1))), empty('Muzzle', TIP.copy()),
           empty('PivotA', PIN_A.copy()), empty('PivotB', PIN_B.copy())]


# ---------- clean up, centre, origins on the pins ----------
meshes = [blade, ha, hb]
pts = [v.co.copy() for o in meshes for v in o.data.vertices]
mn = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
mx = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
centre = (mn + mx) / 2
for o in meshes:
    o.data.transform(Matrix.Translation(-centre))
for e in empties:
    e.location = e.location - centre
for o, p in ((blade, (PIN_A + PIN_B) / 2), (ha, PIN_A), (hb, PIN_B)):
    p = p - centre
    o.data.transform(Matrix.Translation(-p))
    o.location = p

tris = 0
for o in meshes:
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-7)
    bmesh.ops.triangulate(bm, faces=bm.faces, quad_method='BEAUTY', ngon_method='BEAUTY')
    for f in bm.faces: f.smooth = True
    for e in bm.edges: e.smooth = e.is_manifold and e.calc_face_angle(0) < math.radians(25)
    tris += len(bm.faces)
    bm.to_mesh(o.data)
    bm.free()

size = mx - mn
print('BUTTERFLY size %.4f x %.4f x %.4f m, %d triangles' % (size.x, size.y, size.z, tris))
for e in empties:
    print('BUTTERFLY empty %s (%.4f, %.4f, %.4f)' % (e.name, *e.location))
for o in meshes:
    print('BUTTERFLY object %s origin (%.4f, %.4f, %.4f)' % (o.name, *o.location))


# ---------- export ----------
os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=out, use_selection=False, object_types={'MESH', 'EMPTY'},
                         apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                         bake_space_transform=True, add_leaf_bones=False, mesh_smooth_type='FACE')
print('BUTTERFLY exported', os.path.abspath(out))


# ---------- preview renders (optional): open, half flipped and closed ----------
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
    cam.data.ortho_scale = size.x * 1.25
    scene.render.resolution_x, scene.render.resolution_y = 900, 500
    for pose, ang in (('open', 0.0), ('half', 90.0), ('closed', 180.0)):
        ha.rotation_euler = (0, math.radians(ang), 0)
        hb.rotation_euler = (0, math.radians(-ang), 0)
        for tag, d in (('right', (0, -1, 0)), ('three_quarter', (0.5, -1.0, 0.6))):
            d = Vector(d).normalized()
            cam.location = d * 1.0
            cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
            scene.render.filepath = os.path.join(preview, 'butterfly_%s_%s.png' % (pose, tag))
            bpy.ops.render.render(write_still=True)
    ha.rotation_euler = (0, 0, 0)
    hb.rotation_euler = (0, 0, 0)
