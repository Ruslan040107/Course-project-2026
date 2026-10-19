import bpy, bmesh, sys, os, mathutils, json, math
# Makes Assets/Resources/WeaponModels/<weapon>.fbx from the Quaternius Ultimate Gun Pack models.
# blender -b --factory-startup -P process.py -- <source folder> <output folder> cfg.json [weapon names...]
# cfg.json, for each weapon: fn (source file), len (length in m), grip (where the right hand holds it,
# x from the rear end and z as fractions of the length), kind (pistol / knife), silencer [radius, length],
# mag: areas of the side view, boxes [x0, x1, z0, z1] or outlines [[x, z], ...] (x from the rear end, z from
#   the middle, fractions of the length) - the faces with all corners inside them become the separate object Mag
#   (ViewModel drops it on reload), its open top is closed,
# mag_parts: boxes added to Mag: xz (4 points [x, z] in the same fractions), w (half width in m),
#   y (middle in m, 0 if not given), mat (material name) - the part of a pistol magazine hidden in the grip,
#   or a whole magazine the model does not have.
# skip: true - not made here (Desert Eagle is our own model, Tools/Blender/make_deagle.py).
args = sys.argv[sys.argv.index('--')+1:]
src, out, cfgpath = args[0], args[1], args[2]
only = args[3:]
cfg = json.loads(open(cfgpath).read())
V = mathutils.Vector
report = {}
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
    vs = [v.co for v in ob.data.vertices]
    mn = V((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
    mx = V((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    L = c['len']; s = L / (mx.x - mn.x)
    ob.location = -(mn + mx) / 2 * s; ob.scale = (s, s, s)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    ob.name = name.replace(' ', '') + '_Body'
    vs = [v.co for v in ob.data.vertices]
    x0 = min(v.x for v in vs)
    tipv = [v for v in vs if v.x > L / 2 - 0.02 * L]
    muzzle = V((L / 2, sum(v.y for v in tipv) / len(tipv), sum(v.z for v in tipv) / len(tipv)))
    grip = V((x0 + c['grip'][0] * L, 0.0, c['grip'][1] * L))
    eject = V((grip.x + 0.12 * L if c.get('kind') != 'pistol' else grip.x + 0.3 * L, 0.03, muzzle.z + 0.02))

    # silencer for the USP-S
    if c.get('silencer'):
        r = c['silencer'][0]; ln = c['silencer'][1]
        bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=r, depth=ln, location=(muzzle.x + ln / 2 - 0.005, muzzle.y, muzzle.z), rotation=(0, math.radians(90), 0))
        sil = bpy.context.active_object; sil.name = 'Silencer'
        black = next((m for m in bpy.data.materials if m.name.startswith('Black')), None)
        if black: sil.data.materials.append(black)
        muzzle = muzzle + V((ln - 0.005, 0, 0))

    # magazine: the faces with all corners inside the areas are cut off the body into their own object
    parts = []
    if c.get('mag'):
        bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active = ob
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.context.tool_settings.mesh_select_mode = (False, False, True)
        bpy.ops.mesh.select_all(action='DESELECT')
        bm = bmesh.from_edit_mesh(ob.data)
        # a box is turned into an outline
        areas = [[(a[0], a[2]), (a[1], a[2]), (a[1], a[3]), (a[0], a[3])] if not isinstance(a[0], list) else a for a in c['mag']]
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
        picked = 0
        for f in bm.faces:
            if all(inside(v.co) for v in f.verts):
                f.select_set(True); picked += 1
        bmesh.update_edit_mesh(ob.data)
        before = set(bpy.context.scene.objects)
        if picked:
            bpy.ops.mesh.separate(type='SELECTED')
        bpy.ops.object.mode_set(mode='OBJECT')
        parts += [o for o in bpy.context.scene.objects if o not in before]
        report[name + '_mag_faces_cut'] = picked

    # extra boxes of the magazine
    for i, p in enumerate(c.get('mag_parts', [])):
        me = bpy.data.meshes.new('MagPart%d' % i)
        bm = bmesh.new()
        y = p.get('y', 0.0); w = p['w']
        pts = [(x0 + q[0] * L, q[1] * L) for q in p['xz']]
        left = [bm.verts.new((x, y - w, z)) for x, z in pts]
        right = [bm.verts.new((x, y + w, z)) for x, z in pts]
        bm.faces.new(left); bm.faces.new(right)
        for k in range(4):
            bm.faces.new((left[k], left[(k + 1) % 4], right[(k + 1) % 4], right[k]))
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        bm.to_mesh(me); bm.free()
        mat = bpy.data.materials.get(p['mat'])
        if mat: me.materials.append(mat)
        o = bpy.data.objects.new('MagPart%d' % i, me)
        bpy.context.scene.collection.objects.link(o)
        parts.append(o)

    if parts:
        bpy.ops.object.select_all(action='DESELECT')
        for o in parts: o.select_set(True)
        bpy.context.view_layer.objects.active = parts[0]
        if len(parts) > 1:
            bpy.ops.object.join()
        mag = bpy.context.view_layer.objects.active
        mag.name = 'Mag'; mag.data.name = 'Mag'
        # close the holes left where the magazine was cut off (seen from above when it drops)
        bm = bmesh.new(); bm.from_mesh(mag.data)
        mats = [f.material_index for f in bm.faces]
        holes = bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)['faces']
        for f in holes:
            f.material_index = max(set(mats), key=mats.count)
        bm.to_mesh(mag.data); bm.free()
        report[name + '_mag_caps'] = len(holes)
        bpy.ops.object.material_slot_remove_unused()
        # the pivot in the middle of the magazine, like the Desert Eagle one
        mvs = [v.co for v in mag.data.vertices]
        cen = V(((min(v.x for v in mvs) + max(v.x for v in mvs)) / 2, (min(v.y for v in mvs) + max(v.y for v in mvs)) / 2,
                 (min(v.z for v in mvs) + max(v.z for v in mvs)) / 2))
        mag.data.transform(mathutils.Matrix.Translation(-cen))
        mag.location = cen
        mvs = [cen + v.co for v in mag.data.vertices]
        report[name + '_mag'] = dict(faces=len(mag.data.polygons),
            min=[round(min(v[i] for v in mvs), 4) for i in range(3)], max=[round(max(v[i] for v in mvs), 4) for i in range(3)])

    def empty(nm, loc):
        e = bpy.data.objects.new(nm, None); e.location = loc; bpy.context.scene.collection.objects.link(e); return e
    empty('Muzzle', muzzle); empty('Grip', grip); empty('Up', grip + V((0, 0, 0.1))); empty('Eject', eject)
    report[name] = dict(muzzle=[round(x, 3) for x in muzzle], grip=[round(x, 3) for x in grip])

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
