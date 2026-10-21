import bpy, bmesh, sys, os, mathutils, json, math
# Makes Assets/Resources/WeaponModels/<weapon>.fbx from the Quaternius Ultimate Gun Pack models (CC0).
# blender -b --factory-startup -P process.py -- <source folder> <output folder> cfg.json [weapon names...]
# Blender axes: barrel along +X, up +Z, metres. Positions below are fractions of the length: x from the rear
# end, z from the middle (the drawings in the scratch folder have the same grid).
# cfg.json, for each weapon:
#   fn (source file), len (length in m, without a silencer), kind (pistol / knife),
#   grip [x, z] - where the right hand holds it,
#   remove - areas whose faces are deleted first (a stock, a magazine the weapon must not have); the new
#     holes are closed and the rest is scaled to len again, so all the other numbers refer to the trimmed model,
#   mag - areas of the faces that become the separate object Mag (ViewModel drops it on reload), its open top
#     is closed; mag_parts - boxes added to Mag: xz (4 points [x, z]), w (half width in m), y (middle in m),
#     mat (material name) - the part of a pistol magazine hidden in the grip, or a whole magazine,
#   parts - other moving pieces cut off the body the same way: [{name, areas, by, mats}], e.g. the Pump of a
#     shotgun or the Cylinder of a revolver; their pivot is in their middle,
#   add - new pieces: {box: [x0, x1, z0, z1]} or {poly: [[x, z], ...]} (prism, w = half width in m, y = middle),
#     {cyl: [[x, y, z], [x, y, z]], r, r1, seg} (y in m) or {model: source file, size (length along x in m),
#     rot [deg x, y, z], squeeze [sx, sy, sz], at [x, y, z], anchor [-1..1 for x, y, z]}; each has mat
#     (material name, a model keeps its own) and name (the object; pieces with the same name are joined,
#     "Mag" / "Pump" / "Body" join that object),
#   silencer [radius, length, back (how far it goes over the barrel, m), material],
#   eject [x, z] (where the shells fly out) or false (no shells: revolver), muzzle [x, z] if not the barrel tip,
#   tint {material: [r, g, b]} - new colour (linear, like Blender's) of a source material; "*" multiplies all others,
#   colors {name: [r, g, b]} - new materials for the added pieces (a box magazine, a taser body),
#   skip: true - not made here (Desert Eagle: make_deagle.py, Knife: make_butterfly.py, Zeus x27: make_zeus.py).
# An area is a box [x0, x1, z0, z1] or an outline [[x, z], ...]. A selection takes the faces with all corners
# inside (by "face") or whole connected pieces (by "island"); mats limits it to these materials.
args = sys.argv[sys.argv.index('--')+1:]
src, out, cfgpath = args[0], args[1], args[2]
only = args[3:]
cfg = json.loads(open(cfgpath).read())
V = mathutils.Vector
report = {}


def base_name(m):
    return m.name.split('.')[0] if m else ''


def normalize(ob, L):
    # the model is scaled to the length L along x and its box is centred at the origin
    vs = [v.co for v in ob.data.vertices]
    mn = V((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
    mx = V((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    s = L / (mx.x - mn.x)
    ob.data.transform(mathutils.Matrix.Scale(s, 4) @ mathutils.Matrix.Translation(-(mn + mx) / 2))


def selections(spec, by='face', mats=None):
    # a list of areas, a dict {areas, by, mats} or a list of such dicts
    if isinstance(spec, dict):
        spec = [spec]
    if spec and all(isinstance(a, dict) for a in spec):
        return [dict(areas=a['areas'], by=a.get('by', 'face'), mats=a.get('mats')) for a in spec]
    return [dict(areas=spec, by=by, mats=mats)]


def pick(ob, bm, sels, x0, L):
    # the faces of bm inside the selections
    names = [base_name(s.material) for s in ob.material_slots]
    res = set()
    for sel in sels:
        # a box is turned into an outline
        areas = [[(a[0], a[2]), (a[1], a[2]), (a[1], a[3]), (a[0], a[3])] if not isinstance(a[0], list) else a for a in sel['areas']]
        mats = sel.get('mats')

        def inside(co):
            fx = (co.x - x0) / L; fz = co.z / L
            for poly in areas:
                hit = False
                for k in range(len(poly)):
                    (ax, az), (bx, bz) = poly[k], poly[k - 1]
                    if (az > fz) != (bz > fz) and fx < ax + (fz - az) * (bx - ax) / (bz - az):
                        hit = not hit
                if hit: return True
            return False

        def mat_ok(f):
            return not mats or (f.material_index < len(names) and names[f.material_index] in mats)

        if sel.get('by') == 'island':
            seen = set()
            for f in bm.faces:
                if f in seen: continue
                # all faces connected to f
                isle, stack = [], [f]
                seen.add(f)
                while stack:
                    g = stack.pop(); isle.append(g)
                    for v in g.verts:
                        for h in v.link_faces:
                            if h not in seen:
                                seen.add(h); stack.append(h)
                if all(inside(v.co) for g in isle for v in g.verts) and all(mat_ok(g) for g in isle):
                    res.update(isle)
        else:
            for f in bm.faces:
                if mat_ok(f) and all(inside(v.co) for v in f.verts):
                    res.add(f)
    return res


def cut_off(ob, sels, x0, L, name, caps=True):
    # the picked faces of ob become their own object (None if nothing was picked)
    bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.context.tool_settings.mesh_select_mode = (False, False, True)
    bpy.ops.mesh.select_all(action='DESELECT')
    bm = bmesh.from_edit_mesh(ob.data)
    faces = pick(ob, bm, sels, x0, L)
    for f in faces: f.select_set(True)
    bmesh.update_edit_mesh(ob.data)
    before = set(bpy.context.scene.objects)
    if faces:
        bpy.ops.mesh.separate(type='SELECTED')
    bpy.ops.object.mode_set(mode='OBJECT')
    new = [o for o in bpy.context.scene.objects if o not in before]
    report[name + '_faces_cut'] = len(faces)
    return new


def finish_part(objs, name, caps=True):
    # joins the pieces into one object, closes its holes, puts the pivot in its middle
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    part = bpy.context.view_layer.objects.active
    part.name = name; part.data.name = name
    # close the holes left where it was cut off (seen when it moves)
    if caps:
        bm = bmesh.new(); bm.from_mesh(part.data)
        mats = [f.material_index for f in bm.faces]
        holes = bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)['faces']
        for f in holes:
            f.material_index = max(set(mats), key=mats.count)
        bm.to_mesh(part.data); bm.free()
        report[name + '_caps'] = len(holes)
    bpy.ops.object.material_slot_remove_unused()
    pvs = [part.matrix_world @ v.co for v in part.data.vertices]
    cen = V(((min(v.x for v in pvs) + max(v.x for v in pvs)) / 2, (min(v.y for v in pvs) + max(v.y for v in pvs)) / 2,
             (min(v.z for v in pvs) + max(v.z for v in pvs)) / 2))
    part.data.transform(part.matrix_world)
    part.matrix_world = mathutils.Matrix.Identity(4)
    part.data.transform(mathutils.Matrix.Translation(-cen))
    part.location = cen
    report[name] = dict(faces=len(part.data.polygons),
        min=[round(min(v[i] for v in pvs), 4) for i in range(3)], max=[round(max(v[i] for v in pvs), 4) for i in range(3)])
    return part


def material(name, fallback):
    m = next((m for m in bpy.data.materials if base_name(m) == name), None)
    return m if m else fallback


def prism(nm, pts, y, w, mat):
    # side outline (x, z in m) pushed across from y - w to y + w
    me = bpy.data.meshes.new(nm)
    bm = bmesh.new()
    left = [bm.verts.new((x, y - w, z)) for x, z in pts]
    right = [bm.verts.new((x, y + w, z)) for x, z in pts]
    caps = [bm.faces.new(left), bm.faces.new(right)]
    n = len(pts)
    for k in range(n):
        bm.faces.new((left[k], left[(k + 1) % n], right[(k + 1) % n], right[k]))
    if n > 4:
        bmesh.ops.triangulate(bm, faces=caps, quad_method='BEAUTY', ngon_method='EAR_CLIP')
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me); bm.free()
    if mat: me.materials.append(mat)
    o = bpy.data.objects.new(nm, me)
    bpy.context.scene.collection.objects.link(o)
    return o


def cylinder(nm, p0, p1, r, r1, seg, mat):
    # a closed tube from p0 to p1 (radius r at p0, r1 at p1)
    d = p1 - p0
    me = bpy.data.meshes.new(nm)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=seg, radius1=r, radius2=r1, depth=d.length)
    rot = V((0, 0, 1)).rotation_difference(d.normalized()).to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=mathutils.Matrix.Translation((p0 + p1) / 2) @ rot, verts=bm.verts)
    bm.to_mesh(me); bm.free()
    if mat: me.materials.append(mat)
    o = bpy.data.objects.new(nm, me)
    bpy.context.scene.collection.objects.link(o)
    return o


def import_part(a, at, own_mats):
    # a piece of another source model (bipod, scope), turned, scaled and moved to "at"
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.join(src, a['model']))
    new = [o for o in bpy.context.scene.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    for o in new:
        if o.type != 'MESH': continue
        o.data.transform(o.matrix_world)
        o.parent = None
        o.matrix_world = mathutils.Matrix.Identity(4)
    for o in new:
        if o.type != 'MESH': bpy.data.objects.remove(o)
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes: o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1: bpy.ops.object.join()
    o = bpy.context.view_layer.objects.active
    me = o.data

    def box():
        vs = [v.co for v in me.vertices]
        return (V([min(v[i] for v in vs) for i in range(3)]), V([max(v[i] for v in vs) for i in range(3)]))
    mn, mx = box()
    rot = mathutils.Euler([math.radians(x) for x in a.get('rot', [0, 0, 0])]).to_matrix().to_4x4()
    me.transform(rot @ mathutils.Matrix.Translation(-(mn + mx) / 2))
    mn, mx = box()
    me.transform(mathutils.Matrix.Scale(a['size'] / (mx.x - mn.x), 4))
    sq = a.get('squeeze', [1, 1, 1])
    me.transform(mathutils.Matrix.Diagonal((sq[0], sq[1], sq[2], 1)))
    mn, mx = box()
    an = a.get('anchor', [0, 0, 0])
    p = V([(mn[i] + mx[i]) / 2 + an[i] * (mx[i] - mn[i]) / 2 for i in range(3)])
    me.transform(mathutils.Matrix.Translation(at - p))
    # the same materials as the gun where the names match
    for s in o.material_slots:
        m = own_mats.get(base_name(s.material))
        if m: s.material = m
    return o


for name, c in cfg.items():
    if c.get('skip') or (only and name not in only):
        continue
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.join(src, c['fn']))
    ob = [o for o in bpy.context.scene.objects if o.type == 'MESH'][0]
    for o in list(bpy.context.scene.objects):
        if o is not ob: bpy.data.objects.remove(o)
    bpy.context.view_layer.objects.active = ob; ob.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    ob.parent = None
    L = c['len']
    normalize(ob, L)
    ob.name = name.replace(' ', '') + '_Body'
    # new materials for the added pieces
    for nm, col in c.get('colors', {}).items():
        m = bpy.data.materials.new(nm)
        m.diffuse_color = (col[0], col[1], col[2], 1.0)
        m.use_nodes = True
        m.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (col[0], col[1], col[2], 1.0)
    own_mats = {base_name(m): m for m in bpy.data.materials}

    # parts the weapon must not have: deleted, the new holes closed, the rest scaled to the length again
    if c.get('remove'):
        x0 = min(v.co.x for v in ob.data.vertices)
        bm = bmesh.new(); bm.from_mesh(ob.data)
        faces = pick(ob, bm, selections(c['remove']), x0, L)
        open_before = {e for e in bm.edges if e.is_boundary}
        bmesh.ops.delete(bm, geom=list(faces), context='FACES')
        holes = []
        if c.get('remove_fill', True):
            holes = bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary and e not in open_before], sides=0)['faces']
        bm.to_mesh(ob.data); bm.free()
        ob.data.update()
        normalize(ob, L)
        report[name + '_removed'] = [len(faces), len(holes)]

    vs = [v.co for v in ob.data.vertices]
    x0 = min(v.x for v in vs)
    tipv = [v for v in vs if v.x > L / 2 - 0.02 * L]
    muzzle = V((L / 2, sum(v.y for v in tipv) / len(tipv), sum(v.z for v in tipv) / len(tipv)))
    if c.get('muzzle'):
        muzzle = V((x0 + c['muzzle'][0] * L, 0.0, c['muzzle'][1] * L))
    grip = V((x0 + c['grip'][0] * L, 0.0, c['grip'][1] * L))
    eject = V((grip.x + 0.12 * L if c.get('kind') != 'pistol' else grip.x + 0.3 * L, 0.03, muzzle.z + 0.02))
    if c.get('eject'):
        eject = V((x0 + c['eject'][0] * L, 0.03, c['eject'][1] * L))

    def at(p):
        # [x, y, z]: x, z fractions of the length, y in m
        return V((x0 + p[0] * L, p[1], p[2] * L))

    # silencer (USP-S, M4A1-S, MP5-SD)
    if c.get('silencer'):
        sc = c['silencer']
        r = sc[0]; ln = sc[1]
        back = sc[2] if len(sc) > 2 else 0.005
        mat = material(sc[3] if len(sc) > 3 else 'Black', None)
        bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=r, depth=ln, location=(muzzle.x + ln / 2 - back, muzzle.y, muzzle.z), rotation=(0, math.radians(90), 0))
        sil = bpy.context.active_object; sil.name = 'Silencer'
        if mat: sil.data.materials.append(mat)
        muzzle = muzzle + V((ln - back, 0, 0))

    # new pieces, by the object they belong to
    adds = {}
    for i, a in enumerate(c.get('add', [])):
        nm = a.get('name', 'Part')
        mat = material(a['mat'], None) if a.get('mat') else None
        w, y = a.get('w', 0.005), a.get('y', 0.0)
        if 'box' in a:
            b = a['box']
            o = prism('%s%d' % (nm, i), [(x0 + b[0] * L, b[2] * L), (x0 + b[1] * L, b[2] * L), (x0 + b[1] * L, b[3] * L), (x0 + b[0] * L, b[3] * L)], y, w, mat)
        elif 'poly' in a:
            o = prism('%s%d' % (nm, i), [(x0 + q[0] * L, q[1] * L) for q in a['poly']], y, w, mat)
        elif 'cyl' in a:
            r = a['r']
            o = cylinder('%s%d' % (nm, i), at(a['cyl'][0]), at(a['cyl'][1]), r, a.get('r1', r), a.get('seg', 16), mat)
        else:
            o = import_part(a, at(a['at']), own_mats)
            if mat:
                for s in o.material_slots: s.material = mat
        adds.setdefault(nm, []).append(o)

    # magazine: the faces with all corners inside the areas are cut off the body into their own object
    pieces = []
    if c.get('mag'):
        pieces += cut_off(ob, selections(c['mag'], c.get('mag_by', 'face'), c.get('mag_mats')), x0, L, name + '_mag')
    # extra boxes of the magazine
    for i, p in enumerate(c.get('mag_parts', [])):
        pts = [(x0 + q[0] * L, q[1] * L) for q in p['xz']]
        pieces.append(prism('MagPart%d' % i, pts, p.get('y', 0.0), p['w'], bpy.data.materials.get(p['mat'])))
    pieces += adds.pop('Mag', [])
    if pieces:
        finish_part(pieces, 'Mag')

    # other moving pieces (Pump, Cylinder)
    for p in c.get('parts', []):
        objs = cut_off(ob, selections(p['areas'], p.get('by', 'face'), p.get('mats')), x0, L, name + '_' + p['name'])
        objs += adds.pop(p['name'], [])
        if objs:
            finish_part(objs, p['name'], p.get('caps', True))

    # the rest of the new pieces: joined into the body or into their own objects
    for nm, objs in adds.items():
        if nm == 'Body': objs = [ob] + objs
        bpy.ops.object.select_all(action='DESELECT')
        for o in objs: o.select_set(True)
        bpy.context.view_layer.objects.active = objs[0]
        if len(objs) > 1: bpy.ops.object.join()
        if nm != 'Body':
            o = bpy.context.view_layer.objects.active
            o.name = nm; o.data.name = nm

    def empty(nm, loc):
        e = bpy.data.objects.new(nm, None); e.location = loc; bpy.context.scene.collection.objects.link(e); return e
    empty('Muzzle', muzzle); empty('Grip', grip); empty('Up', grip + V((0, 0, 0.1)))
    if c.get('eject', True) is not False:
        empty('Eject', eject)
    report[name] = dict(muzzle=[round(x, 3) for x in muzzle], grip=[round(x, 3) for x in grip],
                        objects=sorted(o.name for o in bpy.context.scene.objects))

    # new colours of the source materials
    tint = c.get('tint', {})
    for mat in bpy.data.materials:
        col = tint.get(base_name(mat))
        if col is None and '*' in tint:
            col = [mat.diffuse_color[i] * tint['*'][i] for i in range(3)]
        if col is None: continue
        mat.diffuse_color = (col[0], col[1], col[2], 1.0)
        if mat.use_nodes:
            b = mat.node_tree.nodes.get('Principled BSDF')
            if b: b.inputs['Base Color'].default_value = (col[0], col[1], col[2], 1.0)

    # the source materials have alpha 0, Unity would import them as invisible transparent ones
    for mat in bpy.data.materials:
        if mat.use_nodes:
            b = mat.node_tree.nodes.get('Principled BSDF')
            if b: b.inputs['Alpha'].default_value = 1.0
        if hasattr(mat, 'blend_method'): mat.blend_method = 'OPAQUE'
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.export_scene.fbx(filepath=os.path.join(out, name + '.fbx'), use_selection=False,
        object_types={'MESH', 'EMPTY'}, apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
        bake_space_transform=True, add_leaf_bones=False, mesh_smooth_type='FACE')
print('REPORT', json.dumps(report))
