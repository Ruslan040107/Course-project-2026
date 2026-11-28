# Builds 6 CS2-style grenade FBX models in Assets/Resources/GrenadeModels/:
#   HE Grenade.fbx, Flashbang.fbx, Smoke Grenade.fbx, Molotov.fbx, Incendiary.fbx, Decoy Grenade.fbx
# Run (headless):
#   "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" -b --factory-startup -P Tools/Blender/make_grenades.py -- [out_dir] [preview_png]
# Blender axes: forward +X, right -Y, up +Z. Metres, real size.
# Every FBX contains opaque meshes and empties Grip, Up, Muzzle (plus Pin or Wick) and optional separate Pin mesh.
import bpy, bmesh, sys, os, math
from mathutils import Vector, Matrix

args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
here = os.path.dirname(os.path.abspath(__file__))
project = os.path.normpath(os.path.join(here, '..', '..'))
out_dir = os.path.abspath(args[0]) if len(args) > 0 and args[0] else os.path.join(project, 'Assets', 'Resources', 'GrenadeModels')
preview_path = os.path.abspath(args[1]) if len(args) > 1 and args[1] else None

os.makedirs(out_dir, exist_ok=True)


def make_mat(name, color, metallic=0.0, roughness=0.5):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes.get('Principled BSDF')
    b.inputs['Base Color'].default_value = (*color, 1.0)
    b.inputs['Metallic'].default_value = metallic
    b.inputs['Roughness'].default_value = roughness
    b.inputs['Alpha'].default_value = 1.0
    m.diffuse_color = (*color, 1.0)
    m.metallic, m.roughness = metallic, roughness
    if hasattr(m, 'blend_method'): m.blend_method = 'OPAQUE'
    if hasattr(m, 'surface_render_method'): m.surface_render_method = 'DITHERED'
    return m


def bm_box(c, s):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(c) @ Matrix.Diagonal((s[0], s[1], s[2], 1.0)), verts=bm.verts)
    return bm


def bm_cyl(c, r0, r1, depth, axis='Z', segs=20):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segs, radius1=r0, radius2=r1, depth=depth)
    rot = {'X': Matrix.Rotation(math.pi / 2, 4, 'Y'), 'Y': Matrix.Rotation(math.pi / 2, 4, 'X'), 'Z': Matrix.Identity(4)}[axis]
    bmesh.ops.transform(bm, matrix=Matrix.Translation(c) @ rot, verts=bm.verts)
    return bm


def bm_uv_sphere(c, rx, ry, rz, u_segs=20, v_segs=12):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=u_segs, v_segments=v_segs, radius=1.0)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(c) @ Matrix.Diagonal((rx, ry, rz, 1.0)), verts=bm.verts)
    return bm


def bm_torus(c, major_r, minor_r, axis='Y', maj_segs=20, min_segs=8):
    bm = bmesh.new()
    rings = []
    for i in range(maj_segs):
        a = 2.0 * math.pi * i / maj_segs
        ca, sa = math.cos(a), math.sin(a)
        ring = []
        for j in range(min_segs):
            b = 2.0 * math.pi * j / min_segs
            r = major_r + minor_r * math.cos(b)
            z = minor_r * math.sin(b)
            ring.append(bm.verts.new((r * ca, r * sa, z)))
        rings.append(ring)
    for i in range(maj_segs):
        ni = (i + 1) % maj_segs
        for j in range(min_segs):
            nj = (j + 1) % min_segs
            bm.faces.new((rings[i][j], rings[ni][j], rings[ni][nj], rings[i][nj]))
    rot = {'X': Matrix.Rotation(math.pi / 2, 4, 'Y'), 'Y': Matrix.Rotation(math.pi / 2, 4, 'X'), 'Z': Matrix.Identity(4)}[axis]
    bmesh.ops.transform(bm, matrix=Matrix.Translation(c) @ rot, verts=bm.verts)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def bm_lathe(profile_rz, segs=20):
    bm = bmesh.new()
    rings = []
    for r, z in profile_rz:
        if r <= 1e-5:
            rings.append([bm.verts.new((0.0, 0.0, z))])
        else:
            ring = []
            for i in range(segs):
                a = 2.0 * math.pi * i / segs
                ring.append(bm.verts.new((r * math.cos(a), r * math.sin(a), z)))
            rings.append(ring)
    for k in range(len(rings) - 1):
        r0, r1 = rings[k], rings[k + 1]
        if len(r0) == 1 and len(r1) > 1:
            for i in range(segs):
                bm.faces.new((r0[0], r1[i], r1[(i + 1) % segs]))
        elif len(r0) > 1 and len(r1) == 1:
            for i in range(segs):
                bm.faces.new((r0[i], r1[0], r0[(i + 1) % segs]))
        elif len(r0) > 1 and len(r1) > 1:
            for i in range(segs):
                ni = (i + 1) % segs
                bm.faces.new((r0[i], r1[i], r1[ni], r0[ni]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def add_bevel(bm, width=0.0008, angle=32):
    bm.edges.ensure_lookup_table()
    edges = [e for e in bm.edges if e.is_manifold and e.calc_face_angle(0) > math.radians(angle)]
    if not edges:
        return
    verts = list({v for e in edges for v in e.verts})
    bmesh.ops.bevel(bm, geom=edges + verts, offset=width, segments=1, profile=0.5, affect='EDGES', clamp_overlap=True)


def make_obj(name, bm, mats, bevel=0.0005):
    if bevel > 0:
        add_bevel(bm, bevel)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in mats:
        me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def join_objs(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = name
    ob.data.name = name
    return ob


def finalize_mesh(ob):
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bmesh.ops.triangulate(bm, faces=bm.faces, quad_method='BEAUTY', ngon_method='BEAUTY')
    for f in bm.faces:
        f.smooth = True
    for e in bm.edges:
        e.smooth = e.is_manifold and e.calc_face_angle(0) < math.radians(28)
    tris = len(bm.faces)
    bm.to_mesh(ob.data)
    bm.free()
    return tris


def add_empty(name, loc):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.015
    e.location = loc
    bpy.context.scene.collection.objects.link(e)
    return e


def add_spoon_and_fuse(parts, top_z, fuse_r, steel, brass, dark, body_r=0.028):
    # Fuze neck + striker housing + curved spoon lever along -X side hugging the body
    parts.append(make_obj('fuze_base', bm_cyl((0, 0, top_z + 0.006), fuse_r, fuse_r * 0.92, 0.012), [brass], bevel=0.0004))
    parts.append(make_obj('fuze_cap', bm_cyl((0, 0, top_z + 0.016), fuse_r * 0.78, fuse_r * 0.72, 0.010), [steel], bevel=0.0004))
    parts.append(make_obj('fuze_head', bm_box((-0.003, 0, top_z + 0.024), (0.022, 0.013, 0.010)), [steel], bevel=0.0008))
    # Spoon lever hugging the -X side of the canister/body
    arm_x = -(body_r + 0.0025)
    parts.append(make_obj('spoon_top', bm_box((arm_x * 0.52, 0, top_z + 0.024), (abs(arm_x) + 0.005, 0.012, 0.004)), [dark], bevel=0.0004))
    parts.append(make_obj('spoon_bend', bm_box((arm_x + 0.001, 0, top_z + 0.015), (0.005, 0.012, 0.020)), [dark], bevel=0.0004))
    parts.append(make_obj('spoon_arm', bm_box((arm_x, 0, top_z - 0.018), (0.004, 0.012, 0.048)), [dark], bevel=0.0004))
    # Pin stud on side
    parts.append(make_obj('pin_stud', bm_cyl((0.002, 0.009, top_z + 0.023), 0.0025, 0.0025, 0.010, axis='Y', segs=12), [steel], bevel=0.0))


def make_pin_ring(loc, ring_mat):
    # Ring on the +Y side of the fuze head so left hand grabs it cleanly
    ring = make_obj('Pin', bm_torus(loc, 0.011, 0.0018, axis='X', maj_segs=20, min_segs=8), [ring_mat], bevel=0.0)
    return ring


def build_he():
    olive = make_mat('HE_Olive', (0.20, 0.27, 0.14), 0.15, 0.55)
    band = make_mat('HE_Yellow', (0.82, 0.65, 0.12), 0.05, 0.45)
    steel = make_mat('HE_Steel', (0.56, 0.58, 0.60), 0.85, 0.32)
    brass = make_mat('HE_Brass', (0.68, 0.54, 0.22), 0.85, 0.35)
    dark = make_mat('HE_Dark', (0.14, 0.16, 0.14), 0.5, 0.45)

    parts = []
    # M67 rounded egg-sphere body (~64 mm diameter, ~75 mm tall)
    parts.append(make_obj('body', bm_uv_sphere((0, 0, 0), 0.032, 0.032, 0.037, 24, 14), [olive], bevel=0.0))
    # Equatorial seam + yellow identification band
    parts.append(make_obj('seam', bm_cyl((0, 0, 0.002), 0.0332, 0.0332, 0.005, segs=24), [dark], bevel=0.0004))
    parts.append(make_obj('stripe', bm_cyl((0, 0, 0.014), 0.0308, 0.0292, 0.006, segs=24), [band], bevel=0.0003))
    add_spoon_and_fuse(parts, 0.033, 0.011, steel, brass, dark, 0.031)

    body = join_objs(parts, 'HE_Body')
    pin = make_pin_ring((0.004, 0.019, 0.056), steel)
    return [body, pin], (0, 0, 0), (0, 0, 0.08), (0.045, 0, 0.01), ('Pin', (0.004, 0.019, 0.056))


def build_flash():
    steel = make_mat('Flash_Steel', (0.52, 0.55, 0.58), 0.82, 0.34)
    body_c = make_mat('Flash_Body', (0.16, 0.18, 0.20), 0.4, 0.45)
    blue = make_mat('Flash_Blue', (0.16, 0.42, 0.82), 0.1, 0.4)
    brass = make_mat('Flash_Brass', (0.68, 0.54, 0.22), 0.85, 0.35)
    dark = make_mat('Flash_Dark', (0.09, 0.10, 0.11), 0.5, 0.5)

    parts = []
    # M84 stun cylinder (radius 0.022, height 0.115)
    parts.append(make_obj('core', bm_cyl((0, 0, 0), 0.021, 0.021, 0.104, segs=22), [body_c], bevel=0.001))
    parts.append(make_obj('cap_top', bm_cyl((0, 0, 0.052), 0.023, 0.022, 0.014, segs=22), [steel], bevel=0.0008))
    parts.append(make_obj('cap_bot', bm_cyl((0, 0, -0.052), 0.022, 0.023, 0.014, segs=22), [steel], bevel=0.0008))
    parts.append(make_obj('blue_band', bm_cyl((0, 0, 0.020), 0.0216, 0.0216, 0.016, segs=22), [blue], bevel=0.0003))
    # Hexagonal/vent ports around top & bottom
    for a_i in range(6):
        ang = a_i * math.pi / 3.0
        vx, vy = math.cos(ang) * 0.021, math.sin(ang) * 0.021
        parts.append(make_obj('vent_t', bm_cyl((vx, vy, 0.051), 0.0035, 0.0035, 0.004, axis='X', segs=8), [dark], bevel=0.0))
        parts.append(make_obj('vent_b', bm_cyl((vx, vy, -0.051), 0.0035, 0.0035, 0.004, axis='X', segs=8), [dark], bevel=0.0))

    add_spoon_and_fuse(parts, 0.058, 0.010, steel, brass, dark, 0.022)
    body = join_objs(parts, 'Flashbang_Body')
    pin = make_pin_ring((0.004, 0.018, 0.081), steel)
    return [body, pin], (0, 0, -0.005), (0, 0, 0.08), (0.04, 0, 0.01), ('Pin', (0.004, 0.018, 0.081))


def build_smoke():
    can = make_mat('Smoke_Can', (0.24, 0.27, 0.25), 0.35, 0.48)
    band = make_mat('Smoke_Band', (0.68, 0.72, 0.66), 0.05, 0.45)
    top_m = make_mat('Smoke_Top', (0.34, 0.38, 0.35), 0.55, 0.4)
    steel = make_mat('Smoke_Steel', (0.58, 0.60, 0.62), 0.85, 0.32)
    brass = make_mat('Smoke_Brass', (0.68, 0.54, 0.22), 0.85, 0.35)
    dark = make_mat('Smoke_Dark', (0.10, 0.11, 0.11), 0.5, 0.5)

    parts = []
    # M18 taller smoke canister (radius 0.028, height 0.125)
    parts.append(make_obj('canister', bm_cyl((0, 0, -0.004), 0.0275, 0.0275, 0.118, segs=24), [can], bevel=0.0012))
    parts.append(make_obj('lid_top', bm_cyl((0, 0, 0.055), 0.0282, 0.0265, 0.010, segs=24), [top_m], bevel=0.0008))
    parts.append(make_obj('rim_bot', bm_cyl((0, 0, -0.062), 0.0282, 0.0282, 0.006, segs=24), [dark], bevel=0.0006))
    parts.append(make_obj('smoke_band', bm_cyl((0, 0, 0.018), 0.0280, 0.0280, 0.018, segs=24), [band], bevel=0.0003))
    parts.append(make_obj('lower_band', bm_cyl((0, 0, -0.026), 0.0280, 0.0280, 0.008, segs=24), [dark], bevel=0.0003))
    add_spoon_and_fuse(parts, 0.058, 0.0105, steel, brass, dark, 0.028)

    body = join_objs(parts, 'Smoke_Body')
    pin = make_pin_ring((0.004, 0.018, 0.081), steel)
    return [body, pin], (0, 0, -0.008), (0, 0, 0.08), (0.045, 0, 0.01), ('Pin', (0.004, 0.018, 0.081))


def build_molotov():
    glass = make_mat('Molotov_Glass', (0.28, 0.42, 0.16), 0.1, 0.14)
    fuel = make_mat('Molotov_Fuel', (0.76, 0.38, 0.06), 0.0, 0.25)
    rag = make_mat('Molotov_Rag', (0.76, 0.70, 0.58), 0.0, 0.9)
    tape = make_mat('Molotov_Tape', (0.14, 0.12, 0.10), 0.0, 0.75)

    parts = []
    # Bottle profile (radius, z)
    prof = [
        (0.0, -0.105),
        (0.029, -0.105),
        (0.032, -0.095),
        (0.032, 0.005),
        (0.026, 0.026),
        (0.014, 0.052),
        (0.013, 0.086),
        (0.0155, 0.092),
        (0.012, 0.095),
        (0.0, 0.095),
    ]
    parts.append(make_obj('bottle', bm_lathe(prof, segs=24), [glass], bevel=0.0))
    # Fuel band / lower liquid visual ring
    parts.append(make_obj('fuel_fill', bm_cyl((0, 0, -0.055), 0.0324, 0.0324, 0.055, segs=24), [fuel], bevel=0.0005))
    # Label / grip wrap
    parts.append(make_obj('wrap', bm_cyl((0, 0, -0.012), 0.0326, 0.0326, 0.024, segs=24), [tape], bevel=0.0004))
    # Cloth wick stuffed into neck and draping smoothly out
    parts.append(make_obj('wick_neck', bm_cyl((0, 0, 0.102), 0.011, 0.013, 0.022, segs=14), [rag], bevel=0.001))
    bm_tail = bm_box((0, 0, 0), (0.016, 0.014, 0.034))
    bmesh.ops.transform(bm_tail, matrix=Matrix.Translation((0.008, 0, 0.120)) @ Matrix.Rotation(math.radians(32), 4, 'Y'), verts=bm_tail.verts)
    parts.append(make_obj('wick_tail', bm_tail, [rag], bevel=0.002))

    body = join_objs(parts, 'Molotov_Body')
    return [body], (0, 0, -0.02), (0, 0, 0.08), (0.045, 0, 0.03), ('Wick', (0.016, 0, 0.134))


def build_incendiary():
    body_m = make_mat('Inc_Body', (0.36, 0.34, 0.32), 0.5, 0.42)
    red = make_mat('Inc_Red', (0.78, 0.14, 0.10), 0.1, 0.42)
    steel = make_mat('Inc_Steel', (0.58, 0.60, 0.62), 0.85, 0.32)
    brass = make_mat('Inc_Brass', (0.68, 0.54, 0.22), 0.85, 0.35)
    dark = make_mat('Inc_Dark', (0.12, 0.12, 0.13), 0.5, 0.5)

    parts = []
    # AN-M14 tactical thermite canister (radius 0.026, height 0.125) with protective ribs
    parts.append(make_obj('can', bm_cyl((0, 0, -0.004), 0.025, 0.025, 0.118, segs=22), [body_m], bevel=0.001))
    parts.append(make_obj('red_band_top', bm_cyl((0, 0, 0.026), 0.0258, 0.0258, 0.014, segs=22), [red], bevel=0.0004))
    parts.append(make_obj('red_band_mid', bm_cyl((0, 0, -0.018), 0.0258, 0.0258, 0.012, segs=22), [red], bevel=0.0004))
    parts.append(make_obj('cap_top', bm_cyl((0, 0, 0.056), 0.0265, 0.024, 0.010, segs=22), [dark], bevel=0.0008))
    parts.append(make_obj('cap_bot', bm_cyl((0, 0, -0.062), 0.0265, 0.0265, 0.008, segs=22), [dark], bevel=0.0006))
    for i in range(4):
        ang = i * math.pi / 2.0 + math.pi / 4.0
        parts.append(make_obj('rib', bm_box((math.cos(ang) * 0.025, math.sin(ang) * 0.025, -0.004), (0.004, 0.004, 0.095)), [dark], bevel=0.0004))
    add_spoon_and_fuse(parts, 0.059, 0.010, steel, brass, dark, 0.026)

    body = join_objs(parts, 'Incendiary_Body')
    pin = make_pin_ring((0.004, 0.018, 0.082), steel)
    return [body, pin], (0, 0, -0.006), (0, 0, 0.08), (0.045, 0, 0.01), ('Pin', (0.004, 0.018, 0.082))


def build_decoy():
    body_m = make_mat('Decoy_Body', (0.20, 0.21, 0.23), 0.45, 0.46)
    orange = make_mat('Decoy_Orange', (0.85, 0.42, 0.08), 0.1, 0.42)
    steel = make_mat('Decoy_Steel', (0.58, 0.60, 0.62), 0.85, 0.32)
    brass = make_mat('Decoy_Brass', (0.68, 0.54, 0.22), 0.85, 0.35)
    dark = make_mat('Decoy_Dark', (0.10, 0.10, 0.11), 0.5, 0.5)

    parts = []
    # Compact slim cylinder with flared rings
    parts.append(make_obj('can', bm_cyl((0, 0, -0.002), 0.019, 0.019, 0.096, segs=20), [body_m], bevel=0.0009))
    parts.append(make_obj('ring_top', bm_cyl((0, 0, 0.036), 0.021, 0.020, 0.010, segs=20), [dark], bevel=0.0006))
    parts.append(make_obj('ring_bot', bm_cyl((0, 0, -0.042), 0.020, 0.021, 0.010, segs=20), [dark], bevel=0.0006))
    parts.append(make_obj('orange_stripe', bm_cyl((0, 0, 0.008), 0.0198, 0.0198, 0.016, segs=20), [orange], bevel=0.0003))
    add_spoon_and_fuse(parts, 0.042, 0.009, steel, brass, dark, 0.020)

    body = join_objs(parts, 'Decoy_Body')
    pin = make_pin_ring((0.004, 0.017, 0.065), steel)
    return [body, pin], (0, 0, -0.005), (0, 0, 0.08), (0.04, 0, 0.01), ('Pin', (0.004, 0.017, 0.065))


BUILDERS = [
    ('HE Grenade', build_he),
    ('Flashbang', build_flash),
    ('Smoke Grenade', build_smoke),
    ('Molotov', build_molotov),
    ('Incendiary', build_incendiary),
    ('Decoy Grenade', build_decoy),
]

for name, builder in BUILDERS:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    meshes, grip_loc, up_loc, muzzle_loc, extra = builder()
    tris = 0
    for m in meshes:
        tris += finalize_mesh(m)
    add_empty('Grip', grip_loc)
    add_empty('Up', up_loc)
    add_empty('Muzzle', muzzle_loc)
    if extra is not None:
        add_empty(extra[0], extra[1])

    fbx_path = os.path.join(out_dir, name + '.fbx')
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.export_scene.fbx(
        filepath=fbx_path,
        use_selection=False,
        object_types={'MESH', 'EMPTY'},
        apply_scale_options='FBX_SCALE_ALL',
        axis_forward='-Z',
        axis_up='Y',
        bake_space_transform=True,
        add_leaf_bones=False,
        mesh_smooth_type='FACE',
    )
    print('GRENADE %s -> %s (%d tris)' % (name, fbx_path, tris))

if preview_path:
    os.makedirs(os.path.dirname(preview_path), exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    for idx, (name, _) in enumerate(BUILDERS):
        fbx_path = os.path.join(out_dir, name + '.fbx')
        before = set(scene.objects)
        bpy.ops.import_scene.fbx(filepath=fbx_path)
        added = [o for o in scene.objects if o not in before]
        x_off = (idx - (len(BUILDERS) - 1) * 0.5) * 0.14
        for o in added:
            if o.parent is None:
                o.location.x += x_off

    world = bpy.data.worlds.new('preview')
    world.color = (0.16, 0.18, 0.21)
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
    cam.data.ortho_scale = 0.98
    d = Vector((0.25, -1.0, 0.25)).normalized()
    cam.location = d * 1.5 + Vector((0, 0, 0.01))
    cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
    scene.render.resolution_x = 1400
    scene.render.resolution_y = 500
    scene.render.filepath = preview_path
    bpy.ops.render.render(write_still=True)
    print('GRENADE preview ->', preview_path)
