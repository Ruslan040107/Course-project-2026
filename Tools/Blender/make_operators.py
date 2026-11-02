# Builds the four CS-like operator models of the game on the Quaternius CharacterArmature rig: CT_SAS, CT_FBI,
# T_Phoenix and T_Guerrilla. The outfit parts come from the Quaternius Ultimate Modular Men pack (CC0, the pack of
# CT_Swat / T_Adventurer; all 11 outfits share one rig and the same 24 actions). The gear (plate carriers, pouches,
# straps, helmets, wraps, goggles ...) is made here: shells of the outfit surface and simple shapes. Which parts,
# which gear and which colours every model has: operators.json next to this script.
# Run (headless):
#   blender -b --factory-startup -P Tools/Blender/make_operators.py -- <pack zip or folder> [out folder] [models...]
#       [--report file.json] [--preview folder] [--blend folder] [--config operators.json]
# The pack is "Ultimate Modular Men Pack-zip.zip" (or the folder it unpacks to). Without an out folder it writes
# Assets/Resources/Characters/<model>.fbx; without model names it builds all of them.
# Blender axes (rest pose): metres, Z up, the character faces -Y, its left is +X.
# Each FBX: the armature CharacterArmature (79 bones, rest pose untouched) with the 24 actions of the pack, one skinned
# mesh <model>_Mesh and the materials <prefix>_<name> (flat colours, alpha 1). The file is saved with Idle_Gun
# assigned: CharacterBody measures the facing in the pose the file was saved in, Idle_Gun gives the same 27 degrees
# as the original models.
# Weights: shells and straps copy the weights of the outfit under them (cloth that crosses joints); a pouch gets the
# weights of the point it sits on, the same for all its vertices, so it moves as one piece with the carrier; head,
# arm and leg gear is 100 % on one bone. --report writes the triangles, materials, heights and the extents of every
# gear piece checked against the gear envelope agreed with CharacterBody's hitboxes ("envelope" in operators.json).
import bpy, bmesh, sys, os, json, math, zipfile, tempfile, shutil
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
opts, pos = {}, []
i = 0
while i < len(args):
    if args[i].startswith('--'):
        opts[args[i][2:]] = args[i + 1]
        i += 2
    else:
        pos.append(args[i])
        i += 1
here = os.path.dirname(os.path.abspath(__file__))
cfg = json.load(open(opts.get('config', os.path.join(here, 'operators.json')), encoding='utf-8'))
if not pos:
    sys.exit('make_operators: give the pack zip or folder (Ultimate Modular Men Pack)')
out_dir = os.path.abspath(pos[1]) if len(pos) > 1 else os.path.join(here, '..', '..', 'Assets', 'Resources', 'Characters')
models = pos[2:] or [k for k in cfg['models']]
ENV = cfg['envelope']


# ---------- the pack: a folder, or the zip unpacked once into a temporary folder ----------
def open_pack(src):
    src = os.path.abspath(src)
    if os.path.isdir(src):
        return src, None
    tmp = tempfile.mkdtemp(prefix='umm_pack_')
    with zipfile.ZipFile(src) as z:
        for n in z.namelist():
            if n.lower().endswith('.fbx'):
                z.extract(n, tmp)
    return tmp, tmp

PACK, PACK_TMP = open_pack(pos[0])


# ---------- small helpers ----------
def linear(hexcol):
    # the colours in operators.json are sRGB like the game shows them (URP shows the FBX colour as sRGB)
    c = [int(hexcol.lstrip('#')[k:k + 2], 16) / 255.0 for k in (0, 2, 4)]
    return [x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c]


def material(name, hexcol):
    m = bpy.data.materials.new(name)
    if m.node_tree is None or m.node_tree.nodes.get('Principled BSDF') is None:
        m.use_nodes = True
    b = m.node_tree.nodes.get('Principled BSDF')
    c = linear(hexcol)
    b.inputs['Base Color'].default_value = (*c, 1.0)
    b.inputs['Metallic'].default_value = 0.0
    b.inputs['Roughness'].default_value = 0.69     # like the pack
    b.inputs['Alpha'].default_value = 1.0          # the pack files read as alpha 0 (transparent in URP)
    m.diffuse_color = (*c, 1.0)                    # colour in the viewport and in workbench renders
    m.roughness = 0.69
    if hasattr(m, 'blend_method'): m.blend_method = 'OPAQUE'
    if hasattr(m, 'surface_render_method'): m.surface_render_method = 'DITHERED'
    return m


def base_name(m):
    return m.name.split('.')[0] if m else ''


def inside(c, box):
    return all(box[k][0] <= c[i] <= box[k][1] for i, k in enumerate('xyz') if k in box)


def mirror_bone(n):
    return n[:-2] + ('.R' if n.endswith('.L') else '.L') if n.endswith(('.L', '.R')) else n


def mirrored(g):
    # the same item on the other side: x -> -x (the character's left is +X), .L bones <-> .R bones
    m = json.loads(json.dumps(g))
    m.pop('mirror', None)
    fx = lambda v: [-v[0]] + list(v[1:])
    for k in ('at', 'dir', 'up', 'centre', 'axis'):
        if k in m:
            m[k] = fx(m[k])
    if 'path' in m:
        m['path'] = [fx(p) for p in m['path']]
    if 'ellipse' in m:
        m['ellipse']['c'] = fx(m['ellipse']['c'])
    if 'outline' in m:
        m['outline'] = [[-u, v] for u, v in m['outline']][::-1]
    for k in ('box', 'cut'):
        if k in m and 'x' in m[k]:
            a, b = m[k]['x']
            m[k]['x'] = [None if b is None else -b, None if a is None else -a]
    if 'planes' in m:
        m['planes'] = [[-p[0], p[1], p[2], -p[3], p[4], p[5]] for p in m['planes']]
    if 'rot' in m:
        m['rot'] = [m['rot'][0], -m['rot'][1], -m['rot'][2]]
    if 'bone' in m:
        m['bone'] = mirror_bone(m['bone'])
    m['label'] = m.get('label', m['type']) + ' (mirrored)'
    return m


def import_fbx(path):
    before_o, before_a = set(bpy.data.objects), set(bpy.data.actions)
    bpy.ops.import_scene.fbx(filepath=path)
    return [o for o in bpy.data.objects if o not in before_o], [a for a in bpy.data.actions if a not in before_a]


def adopt(ob, arm):
    # bake the object transform into the vertices (Casual_Body, Suit_Body, Backpack are not identity), then put the
    # mesh under our armature with identity transforms; its vertex groups are bone names of the same rig
    mw = ob.matrix_world.copy()
    ob.parent = None
    ob.data.transform(mw)
    ob.matrix_world = Matrix.Identity(4)
    ob.parent = arm
    ob.matrix_parent_inverse = Matrix.Identity(4)
    ob.matrix_basis = Matrix.Identity(4)
    mods = [m for m in ob.modifiers if m.type == 'ARMATURE']
    if not mods:
        mods = [ob.modifiers.new('Armature', 'ARMATURE')]
    for m in mods:
        m.object = arm


def tri_count(me):
    return sum(len(p.vertices) - 2 for p in me.polygons)


def bm_copy(bm):
    me = bpy.data.meshes.new('tmp')
    bm.to_mesh(me)
    c = bmesh.new()
    c.from_mesh(me)
    bpy.data.meshes.remove(me)
    return c


# ---------- one model ----------
class Builder:
    def __init__(self, name, spec):
        self.name, self.spec = name, spec
        bpy.ops.wm.read_factory_settings(use_empty=True)
        self.scene = bpy.context.scene
        self.keys = list(spec['materials'])                 # material slot order
        self.slot = {k: i for i, k in enumerate(self.keys)}
        self.mats = [material(spec['prefix'] + '_' + k, spec['materials'][k]) for k in self.keys]
        self.report = {'model': name, 'gear': []}

    # ----- outfit parts -----
    def load_parts(self):
        files = []
        for p in self.spec['parts']:
            if p['file'] not in files:
                files.append(p['file'])
        self.arm, meshes = None, []
        for f in files:
            objs, acts = import_fbx(os.path.join(PACK, f))
            a = next(o for o in objs if o.type == 'ARMATURE')
            if self.arm is None:
                self.arm = a                                # the rig and the 24 actions of the first file
            else:
                for act in acts:
                    bpy.data.actions.remove(act)
            for p in self.spec['parts']:
                if p['file'] != f:
                    continue
                ob = next(o for o in objs if o.type == 'MESH' and o.name.split('.')[0] == p['mesh'])
                adopt(ob, self.arm)
                self.recolour(ob, p)
                if p.get('decimate'):
                    self.decimate(ob, p['decimate'])
                meshes.append(ob)
            for o in objs:
                if o not in meshes and o != self.arm:
                    bpy.data.objects.remove(o, do_unlink=True)
        self.arm.name = 'CharacterArmature'
        self.arm.data.pose_position = 'REST'
        # one skinned mesh: cheaper in Unity (one renderer and one shadow proxy)
        bpy.ops.object.select_all(action='DESELECT')
        for o in meshes:
            o.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        bpy.ops.object.join()
        self.O = bpy.context.view_layer.objects.active
        self.O.name = self.O.data.name = self.name + '_Mesh'
        self.outfit_tris = tri_count(self.O.data)
        bm = bmesh.new()
        bm.from_mesh(self.O.data)
        bm.faces.ensure_lookup_table()
        self.out_bm = bm
        self.out_tree = BVHTree.FromBMesh(bm)
        self.surfaces = [(bm, self.out_tree)]               # what pouches and straps can sit on
        self.pieces = []
        self.hidden = set()                                 # outfit faces covered by shells (deleted in finish)

    def recolour(self, ob, part):
        # every face gets one of the model's materials: by its source material ("colors", "*" = any other), then the
        # "rules" in order (a later rule wins): conditions mat / bones / box / out / normal, action to / delete
        me = ob.data
        src = [base_name(m) for m in me.materials]
        groups = {g.index: g.name for g in ob.vertex_groups}
        bm = bmesh.new()
        bm.from_mesh(me)
        dl = bm.verts.layers.deform.active

        def dominant(v):
            d = v[dl] if dl else {}
            return groups.get(max(d.items(), key=lambda kv: kv[1])[0], '') if len(d) else ''

        colors, rules = part.get('colors', {}), part.get('rules', [])
        kill, unmapped = [], set()
        for f in bm.faces:
            s = src[f.material_index] if f.material_index < len(src) else ''
            t = colors.get(s, colors.get('*'))
            c = f.calc_center_median()
            for r in rules:
                if 'mat' in r and s not in r['mat']:
                    continue
                if 'box' in r and not inside(c, r['box']):
                    continue
                if 'out' in r and inside(c, r['out']):
                    continue
                if 'normal' in r and f.normal.dot(Vector(r['normal'])) < r.get('cos', 0.0):
                    continue
                if 'bones' in r and not all(dominant(v).startswith(tuple(r['bones'])) for v in f.verts):
                    continue
                t = 'DELETE' if r.get('delete') else r['to']
            if t is None:
                unmapped.add(s)
                t = 'DELETE'
            if t == 'DELETE':
                kill.append(f)
            else:
                f.material_index = self.slot[t]
        bmesh.ops.delete(bm, geom=kill, context='FACES')
        me.materials.clear()                        # (this also drops the face material indices: set them after it)
        for m in self.mats:
            me.materials.append(m)
        bm.to_mesh(me)
        bm.free()
        if unmapped:
            print('OPS warning: %s %s has unmapped materials %s (deleted)' % (self.name, part['mesh'], sorted(unmapped)))
        print('OPS part %-16s %-26s faces %5d tris %5d' % (part['mesh'], part['file'], len(me.polygons), tri_count(me)))

    def decimate(self, ob, ratio):
        # fewer triangles for a part that is not seen up close (the backpack): collapse before the armature modifier;
        # the vertex weights and the face materials are interpolated / kept
        m = ob.modifiers.new('Decimate', 'DECIMATE')
        m.ratio = ratio
        bpy.ops.object.select_all(action='DESELECT')
        ob.select_set(True)
        bpy.context.view_layer.objects.active = ob
        bpy.ops.object.modifier_move_to_index(modifier=m.name, index=0)
        bpy.ops.object.modifier_apply(modifier=m.name)
        print('OPS part %-16s decimated to %.2f: tris %5d' % (ob.name, ratio, tri_count(ob.data)))

    # ----- weights -----
    def gidx(self, bone):
        vg = self.O.vertex_groups.get(bone)
        if vg is None:
            if bone not in self.arm.data.bones:
                raise ValueError('no bone ' + bone)
            vg = self.O.vertex_groups.new(name=bone)
        return vg.index

    def face_weights(self, bm, fi):
        # the mean weights of a face of a surface bmesh (outfit or gear built so far)
        dl = bm.verts.layers.deform.active
        acc = {}
        f = bm.faces[fi]
        for v in f.verts:
            for g, w in v[dl].items():
                acc[g] = acc.get(g, 0.0) + w
        s = sum(acc.values()) or 1.0
        return {g: w / s for g, w in acc.items() if w / s > 0.005}

    def set_weights(self, bm, verts, weights):
        dl = bm.verts.layers.deform.verify()
        for v in verts:
            d = v[dl]
            d.clear()
            for g, w in weights.items():
                d[g] = w

    def rigid(self, g, anchor=None):
        # the weights of a rigid piece: 100 % on "bone", or those of the surface point it sits on (anchor)
        if 'bone' in g:
            return {self.gidx(g['bone']): 1.0}
        return anchor

    # ----- queries on the surfaces built so far -----
    def ray(self, origin, direction, dist=2.0):
        best = None
        for bm, tree in self.surfaces:
            loc, nor, idx, d = tree.ray_cast(origin, direction, dist)
            if loc is not None and (best is None or d < best[3]):
                best = (loc, nor, idx, d, bm)
        return best

    def nearest(self, p, only_outfit=False):
        best = None
        for bm, tree in (self.surfaces[:1] if only_outfit else self.surfaces):
            loc, nor, idx, d = tree.find_nearest(p)
            if loc is not None and (best is None or d < best[3]):
                best = (loc, nor, idx, d, bm)
        return best

    # ----- gear generators: each returns a bmesh in rest-pose coordinates with weights and material indices -----
    def g_shell(self, g):
        # a shell over the outfit surface: the outer faces that look along "dir" inside "box", cut by planes
        # ("cut" ranges, chamfered corners, "planes"), pushed out by "off" and made "thick"
        bm = bm_copy(self.out_bm)
        bm.normal_update()
        bm.faces.index_update()                              # the same order as the outfit's faces
        d = Vector(g['dir']).normalized() if 'dir' in g else None
        on = {self.slot[k] for k in g['on']} if 'on' in g else None
        drop = []
        for f in bm.faces:
            c = f.calc_center_median()
            ok = (on is None or f.material_index in on) and ('box' not in g or inside(c, g['box'])) and \
                 ('out' not in g or not inside(c, g['out'])) and (d is None or f.normal.dot(d) >= g.get('cos', 0.25))
            if ok and g.get('outer', True):
                ok = self.out_tree.ray_cast(c + f.normal * 0.003, f.normal, 0.25)[0] is None
            if not ok:
                drop.append(f)
        planes = self.planes(g)
        if g.get('hide'):
            # the outfit faces wholly under the shell are deleted at the end: the shell has the same weights, so it
            # covers them in every pose, and its top costs no triangles then (only its side walls do)
            dropped = set(drop)
            for f in bm.faces:
                if f not in dropped and all((v.co - co).dot(no) >= 0.002 for v in f.verts for co, no in planes):
                    self.hidden.add(f.index)
        bmesh.ops.delete(bm, geom=drop, context='FACES')
        for co, no in planes:
            bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-6,
                                   plane_co=co, plane_no=no, clear_inner=True)
        for z in [z for band in g.get('bands', []) for z in band]:
            # rows of webbing across the shell: cut lines first, coloured after the solidify
            bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-6,
                                   plane_co=(0, 0, z), plane_no=(0, 0, 1))
        self.clean(bm)
        if not bm.faces:
            raise ValueError('shell %s selected no faces' % g.get('label'))
        if g.get('subdiv'):
            # smaller faces for a pattern on a coarse surface (the weights are interpolated)
            bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=g['subdiv'], use_grid_fill=True)
        self.solidify(bm, g.get('off', 0.004), g.get('thick', 0.02))
        mi = self.slot[g['mat']]
        for f in bm.faces:
            f.material_index = mi
            f.smooth = False
            if g.get('bands') and any(z0 <= f.calc_center_median().z <= z1 for z0, z1 in g['bands']):
                f.material_index = self.slot[g['band_mat']]
        self.pattern(bm, g)
        if 'bone' in g:
            self.set_weights(bm, bm.verts, {self.gidx(g['bone']): 1.0})
        return bm

    def planes(self, g):
        P = []
        cut = g.get('cut', {})
        for i, k in enumerate('xyz'):
            if k in cut:
                a, b = cut[k]
                n = Vector((0, 0, 0))
                n[i] = 1.0
                if a is not None:
                    P.append((n * a, n.copy()))
                if b is not None:
                    P.append((n * b, -n))
        r = g.get('corner')
        if r:
            ax = g.get('corner_axes', 'xz')
            i, j = 'xyz'.index(ax[0]), 'xyz'.index(ax[1])
            (a0, a1), (b0, b1) = cut[ax[0]], cut[ax[1]]
            for sa, A in ((1, a1), (-1, a0)):
                for sb, B in ((1, b1), (-1, b0)):
                    n = Vector((0, 0, 0))
                    n[i], n[j] = -sa, -sb
                    co = Vector((0, 0, 0))
                    co[i], co[j] = A - sa * r, B
                    P.append((co, n.normalized()))
        for p in g.get('planes', []):
            P.append((Vector(p[:3]), Vector(p[3:]).normalized()))
        return P

    def clean(self, bm):
        bmesh.ops.delete(bm, geom=[e for e in bm.edges if not e.link_faces], context='EDGES')
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')

    def solidify(self, bm, off, thick):
        # push the patch out by off along the vertex normals, then extrude it by thick (the side walls stay, the
        # bottom is left open: it lies on the outfit and is never seen)
        bm.normal_update()
        for v in bm.verts:
            v.co += v.normal * off
        bm.normal_update()
        key = lambda co: (round(co.x, 5), round(co.y, 5), round(co.z, 5))
        nm = {key(v.co): v.normal.copy() for v in bm.verts}
        ret = bmesh.ops.extrude_face_region(bm, geom=list(bm.faces))
        for v in [e for e in ret['geom'] if isinstance(e, bmesh.types.BMVert)]:
            v.co += nm[key(v.co)] * thick
        self.clean(bm)
        bm.normal_update()

    def pattern(self, bm, g):
        # a two-colour check (the shemagh) or stripes on a shell: by cells of the angle around the head axis and z
        p = g.get('pattern')
        if not p:
            return
        mi = self.slot[p['mat']]
        cu, cz = p['cell']
        c = Vector(p.get('axis', (0.0, -0.05)))
        for f in bm.faces:
            q = f.calc_center_median()
            u = math.atan2(q.x - c.x, -(q.y - c.y)) * 0.11
            a, b = math.floor(u / cu), math.floor((q.z - p.get('z0', 0.0)) / cz)
            kind = p.get('kind', 'check')
            if kind == 'check':
                on = (a + b) % 2 == 0
            elif kind == 'grid':        # lines every "period" cells, like the woven check of a shemagh
                on = a % p.get('period', 3) == 0 or b % p.get('period', 3) == 0
            else:                       # stripes
                on = b % 2 == 0
            if 'rows' in p:
                on = on or any(r0 <= q.z <= r1 for r0, r1 in p['rows'])
            if on:
                f.material_index = mi

    def frame(self, out, up0):
        out = out.normalized()
        up = up0 - out * up0.dot(out)
        if up.length < 1e-4:
            up = Vector((0, -1, 0)) - out * Vector((0, -1, 0)).dot(out)
        up.normalize()
        side = up.cross(out).normalized()
        return side, out, up

    def g_pouch(self, g):
        # a pouch, patch, plate or rail on the surface: rays along "dir" from "at" find where its back sits (the most
        # protruding of 5 hits, so it never sinks in) and which way its face looks (their mean normal)
        w, h, d = g['size']
        at, ray = Vector(g['at']), Vector(g['dir']).normalized()
        side, out, up = self.frame(-ray, Vector(g.get('up', (0, 0, 1))))
        hits = []
        for du, dv in ((0, 0), (0.4, 0.4), (-0.4, 0.4), (0.4, -0.4), (-0.4, -0.4)):
            hit = self.ray(at + side * (du * w) + up * (dv * h), ray)
            if hit:
                hits.append(hit)
        if not hits:
            raise ValueError('pouch %s hits nothing' % g.get('label'))
        n = sum((hh[1] if hh[1].dot(-ray) > 0 else -hh[1] for hh in hits), Vector()).normalized()
        if g.get('flat'):
            n = -ray
        side, out, up = self.frame(n, Vector(g.get('up', (0, 0, 1))))
        c0 = hits[0][0]
        back = c0 + out * max((hh[0] - c0).dot(out) for hh in hits) - out * g.get('embed', 0.003)
        back += side * g.get('shift', [0, 0])[0] + up * g.get('shift', [0, 0])[1]
        if 'tilt' in g:   # turned around the side axis (the top leans out), degrees
            R = Matrix.Rotation(math.radians(g['tilt']), 3, side)
            out, up = R @ out, R @ up
        M = Matrix((side, out, up)).transposed()
        anchor = self.face_weights(hits[0][4], hits[0][2])
        bm = bmesh.new()
        dl = bm.verts.layers.deform.verify()

        def add(centre_local, size, mat, bevel):
            # a bevelled box in the pouch frame: x = side, y = out (from the back plane), z = up
            b2 = bmesh.new()
            bmesh.ops.create_cube(b2, size=1.0)
            bmesh.ops.scale(b2, vec=Vector(size), verts=b2.verts)
            if bevel > 0:
                bmesh.ops.bevel(b2, geom=list(b2.edges), offset=min(bevel, 0.45 * min(size)), segments=1,
                                affect='EDGES', profile=0.5, clamp_overlap=True)
            T = Matrix.Translation(back + M @ Vector(centre_local)) @ M.to_4x4()
            bmesh.ops.transform(b2, matrix=T, verts=b2.verts)
            me = bpy.data.meshes.new('tmp')
            b2.to_mesh(me)
            b2.free()
            n0 = len(bm.faces)
            bm.from_mesh(me)
            bpy.data.meshes.remove(me)
            bm.faces.ensure_lookup_table()
            for f in bm.faces[n0:]:
                f.material_index = self.slot[mat]

        if 'outline' in g:
            # a flat shape (drape, triangle) instead of a box: outline in (side, up), extruded by d
            pts = [Vector((u, 0, v)) for u, v in g['outline']]
            vs0 = [bm.verts.new(back + M @ p) for p in pts]
            vs1 = [bm.verts.new(back + M @ (p + Vector((0, d, 0)))) for p in pts]
            bm.faces.new(vs1)
            for k in range(len(pts)):
                bm.faces.new((vs0[k], vs0[(k + 1) % len(pts)], vs1[(k + 1) % len(pts)], vs1[k]))
            bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
            for f in bm.faces:
                f.material_index = self.slot[g['mat']]
        else:
            add((0, d / 2, 0), (w, d, h), g['mat'], g.get('bevel', 0.006))
        fl = g.get('flap')
        if fl:
            fh = fl.get('h', 0.035)
            add((0, (d + 0.008) / 2 - 0.001, h / 2 - fh / 2 + 0.004), (w + 0.006, d + 0.008, fh), fl.get('mat', g['mat']), 0.004)
            if fl.get('tab', True):
                add((0, d + 0.006, h / 2 - fh - 0.004), (w * 0.28, 0.006, 0.022), fl.get('mat', g['mat']), 0.002)
        ln = g.get('lens')
        if ln:
            # an inset on the front face (goggle lens, visor)
            add((0, d + ln.get('d', 0.004) / 2 - 0.001, 0), (w * ln.get('k', 0.8), ln.get('d', 0.004), h * ln.get('k', 0.8)), ln['mat'], 0.001)
        mg = g.get('mag')
        if mg:
            # magazines sticking out of open-top pouches (n side by side)
            k = mg.get('n', 1)
            mw, md, mh = mg.get('w', w * 0.62 / k), mg.get('d', d * 0.6), mg.get('h', 0.025)
            for j in range(k):
                x = (j - (k - 1) / 2) * (w / k)
                add((x, d * 0.5, h / 2 + mh / 2 - 0.004), (mw, md, mh), mg['mat'], 0.003)
        for s in g.get('straps', []):
            # retention straps / webbing rows across the face: [height from the centre, thickness]
            add((0, d + 0.002, s[0]), (w + 0.004, 0.006, s[1]), g.get('strap_mat', g['mat']), 0.0015)
        for v in bm.verts:
            v.select = False
        for f in bm.faces:
            f.smooth = False
        self.set_weights(bm, bm.verts, self.rigid(g, anchor))
        return bm

    def g_strap(self, g):
        # a strap laid over the surface along "path" (rough points next to it): every sample is snapped to the
        # nearest surface point and takes its weights, so the strap moves like the cloth under it
        pts = [Vector(p) for p in g['path']]
        closed = g.get('closed', False)
        step = g.get('step', 0.02)
        seq = pts + [pts[0]] if closed else pts
        samples = []
        for a, b in zip(seq[:-1], seq[1:]):
            n = max(1, int(math.ceil((b - a).length / step)))
            samples += [a.lerp(b, k / n) for k in range(n)]
        if not closed:
            samples.append(seq[-1])
        snap = [self.nearest(p) for p in samples]
        locs = [s[0] for s in snap]
        nors = [s[1] for s in snap]
        # smooth the normals of the faceted surface along the strap
        N = len(locs)
        sm = []
        for k in range(N):
            acc = Vector()
            for dk in (-2, -1, 0, 1, 2):
                j = (k + dk) % N if closed else min(max(k + dk, 0), N - 1)
                acc += nors[j]
            sm.append(acc.normalized())
        w, t, off = g['width'], g.get('thick', 0.008), g.get('off', 0.002)
        bm = bmesh.new()
        dl = bm.verts.layers.deform.verify()
        rings = []
        for k in range(N):
            a = locs[(k - 1) % N if closed else max(k - 1, 0)]
            b = locs[(k + 1) % N if closed else min(k + 1, N - 1)]
            tan = (b - a).normalized()
            n = sm[k]
            s = n.cross(tan).normalized()
            c = locs[k] + n * off
            ring = [bm.verts.new(c - s * w / 2), bm.verts.new(c + s * w / 2),
                    bm.verts.new(c + s * w / 2 + n * t), bm.verts.new(c - s * w / 2 + n * t)]
            wts = self.rigid(g, self.face_weights(snap[k][4], snap[k][2]))
            self.set_weights(bm, ring, wts)
            rings.append((ring, n, s))
        segs = range(N) if closed else range(N - 1)
        for k in segs:
            (r0, n, s), (r1, _, _) = rings[k], rings[(k + 1) % N]
            for (i0, i1), exp in (((3, 2), n), ((1, 2), s), ((0, 3), -s)):
                f = bm.faces.new((r0[i0], r0[i1], r1[i1], r1[i0]))
                f.normal_update()
                if f.normal.dot(exp) < 0:
                    f.normal_flip()
        if not closed:
            for (r, n, s), sign in ((rings[0], -1), (rings[-1], 1)):
                f = bm.faces.new((r[0], r[1], r[2], r[3]))
                f.normal_update()
                tan = (rings[1][0][0].co - rings[0][0][0].co) if sign < 0 else (rings[-1][0][0].co - rings[-2][0][0].co)
                if f.normal.dot(tan * sign) < 0:
                    f.normal_flip()
        for f in bm.faces:
            f.material_index = self.slot[g['mat']]
            f.smooth = False
        return bm

    def g_box(self, g):
        # a rigid bevelled box: centre, size, rot (degrees, XYZ), on one bone
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.scale(bm, vec=Vector(g['size']), verts=bm.verts)
        if g.get('bevel', 0.003) > 0:
            bmesh.ops.bevel(bm, geom=list(bm.edges), offset=g.get('bevel', 0.003), segments=g.get('segments', 1),
                            affect='EDGES', profile=0.5, clamp_overlap=True)
        R = Matrix.Identity(4)
        if 'rot' in g:
            from mathutils import Euler
            R = Euler([math.radians(a) for a in g['rot']]).to_matrix().to_4x4()
        bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(g['centre'])) @ R, verts=bm.verts)
        for f in bm.faces:
            f.material_index = self.slot[g['mat']]
            f.smooth = False
        self.set_weights(bm, bm.verts, self.rigid(g, self.near_weights(Vector(g['centre']))))
        return bm

    def g_cyl(self, g):
        # a rigid cylinder or cone: centre, axis, r (r2 = other end), depth, segs
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=g.get('segs', 12), radius1=g['r'],
                              radius2=g.get('r2', g['r']), depth=g['depth'])
        rot = Vector((0, 0, 1)).rotation_difference(Vector(g['axis']).normalized()).to_matrix().to_4x4()
        bmesh.ops.transform(bm, matrix=Matrix.Translation(Vector(g['centre'])) @ rot, verts=bm.verts)
        if g.get('bevel', 0) > 0:
            edges = [e for e in bm.edges if e.calc_face_angle(0) > math.radians(60)]
            bmesh.ops.bevel(bm, geom=edges, offset=g['bevel'], segments=1, affect='EDGES', profile=0.5, clamp_overlap=True)
        for f in bm.faces:
            f.material_index = self.slot[g['mat']]
            f.smooth = g.get('smooth', False)
        self.set_weights(bm, bm.verts, self.rigid(g, self.near_weights(Vector(g['centre']))))
        return bm

    def near_weights(self, p):
        hit = self.nearest(p, only_outfit=True)
        return self.face_weights(hit[4], hit[2])

    def ellipse(self, e):
        c, (rx, ry), n = Vector(e['c']), e['r'], e.get('n', 16)
        tilt = math.radians(e.get('tilt', 0))     # turned around X: the front goes down for tilt > 0
        R = Matrix.Rotation(tilt, 3, 'X')
        return [c + R @ Vector((rx * math.sin(2 * math.pi * k / n), -ry * math.cos(2 * math.pi * k / n), 0)) for k in range(n)]

    def g_tube(self, g):
        # a tube (rolled cloth, a scarf loop, a goggle strap) along a path or an ellipse; cross-section a x b;
        # weights from the outfit point nearest to each vertex ("near") or one bone
        pts = self.ellipse(g['ellipse']) if 'ellipse' in g else [Vector(p) for p in g['path']]
        closed = g.get('closed', 'ellipse' in g)
        if 'snap' in g:
            # laid on the surface built so far (a roll on a wrap): every point goes to the nearest surface point and
            # "snap" out along its normal
            pts = [h[0] + h[1].normalized() * g['snap'] for h in (self.nearest(p) for p in pts)]
        a, b = g['section'] if 'section' in g else (g['r'], g['r'])
        m = g.get('sides', 6)
        centre = Vector(g['ellipse']['c']) if 'ellipse' in g else sum(pts, Vector()) / len(pts)
        bm = bmesh.new()
        bm.verts.layers.deform.verify()            # before the vertices: adding a layer later invalidates them
        N = len(pts)
        rings = []
        for k in range(N):
            p = pts[k]
            pa = pts[(k - 1) % N] if closed else pts[max(k - 1, 0)]
            pb = pts[(k + 1) % N] if closed else pts[min(k + 1, N - 1)]
            t = (pb - pa).normalized()
            radial = p - centre
            radial -= t * radial.dot(t)
            if radial.length < 1e-6:
                radial = Vector((0, 0, 1)) - t * t.z
            radial.normalize()
            bi = t.cross(radial).normalized()
            rings.append([bm.verts.new(p + radial * (a * math.cos(2 * math.pi * j / m)) + bi * (b * math.sin(2 * math.pi * j / m)))
                          for j in range(m)])
        for k in (range(N) if closed else range(N - 1)):
            r0, r1 = rings[k], rings[(k + 1) % N]
            for j in range(m):
                f = bm.faces.new((r0[j], r0[(j + 1) % m], r1[(j + 1) % m], r1[j]))
        if not closed:
            bm.faces.new(rings[0][::-1])
            bm.faces.new(rings[-1])
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        for f in bm.faces:
            f.material_index = self.slot[g['mat']]
            f.smooth = g.get('smooth', False)
        self.pattern(bm, g)
        if 'bone' in g:
            self.set_weights(bm, bm.verts, {self.gidx(g['bone']): 1.0})
        else:
            for v in bm.verts:
                self.set_weights(bm, [v], self.near_weights(v.co))
        return bm

    def g_helmet(self, g):
        # a high-cut helmet shell: an ellipsoid (centre, radii of the outer surface) above a rim line given as
        # [azimuth, z] points (0 = front, 90 = left side, 180 = back), "thick" thick; 100 % on Head. With "fit"
        # (material names of the head under it) the radii grow until every head vertex above the rim is "clear"
        # inside the inner surface.
        c, R0, t = Vector(g['centre']), Vector(g['radii']), g.get('thick', 0.01)
        cut = sorted(g['rim'])
        N, M = g.get('segs', 20), g.get('rows', 6)

        def z_rim(az):
            az %= 360.0
            pts = cut + [[cut[0][0] + 360.0, cut[0][1]]]
            prev = [cut[-1][0] - 360.0, cut[-1][1]]
            for p in pts:
                if az <= p[0]:
                    s = (az - prev[0]) / max(p[0] - prev[0], 1e-6)
                    return prev[1] + (p[1] - prev[1]) * s
                prev = p
            return cut[0][1]

        R = R0.copy()
        if 'fit' in g:
            fit = {self.slot[k] for k in g['fit']}
            worst, at = 0.0, None
            for f in self.out_bm.faces:
                if f.material_index not in fit:
                    continue
                for v in f.verts:
                    p = v.co - c
                    az = math.degrees(math.atan2(p.x, -p.y))
                    if v.co.z < z_rim(az) - 0.005:
                        continue
                    q = math.sqrt((p.x / (R.x - t)) ** 2 + (p.y / (R.y - t)) ** 2 + (p.z / (R.z - t)) ** 2)
                    if q + g.get('clear', 0.004) / (R.x - t) > worst:
                        worst, at = q + g.get('clear', 0.004) / (R.x - t), v.co.copy()
            if worst > 1.0:
                R = Vector(((R.x - t) * worst + t, (R.y - t) * worst + t, (R.z - t) * worst + t))
            self.report.setdefault('notes', []).append('%s fit: head reaches %.3f of the inner surface at %s, radii %s' % (
                g.get('label', 'helmet'), worst, [round(x, 3) for x in at] if at else '-', [round(x, 4) for x in R]))
        bm = bmesh.new()

        def point(rad, az, frac):
            # frac 0 = top, 1 = rim; the rim height is reached exactly at frac 1
            a = math.radians(az)
            cz = (z_rim(az) - c.z) / rad.z
            th_max = math.acos(max(-1.0, min(1.0, cz)))
            th = th_max * frac
            return c + Vector((rad.x * math.sin(th) * math.sin(a), -rad.y * math.sin(th) * math.cos(a), rad.z * math.cos(th)))

        def sheet(rad):
            top = bm.verts.new(c + Vector((0, 0, rad.z)))
            rings = [[bm.verts.new(point(rad, 360.0 * j / N, i / M)) for j in range(N)] for i in range(1, M + 1)]
            return top, rings

        Ri = Vector((R.x - t, R.y - t, R.z - t))
        to, ro = sheet(R)
        ti, ri = sheet(Ri)
        for j in range(N):
            j1 = (j + 1) % N
            bm.faces.new((to, ro[0][j], ro[0][j1]))
            bm.faces.new((ti, ri[0][j1], ri[0][j]))
            for i in range(M - 1):
                bm.faces.new((ro[i][j], ro[i + 1][j], ro[i + 1][j1], ro[i][j1]))
                bm.faces.new((ri[i][j], ri[i][j1], ri[i + 1][j1], ri[i + 1][j]))
            bm.faces.new((ro[-1][j], ri[-1][j], ri[-1][j1], ro[-1][j1]))
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        for f in bm.faces:
            f.material_index = self.slot[g['mat']]
            f.smooth = False
        self.pattern(bm, g)
        self.set_weights(bm, bm.verts, {self.gidx(g.get('bone', 'Head')): 1.0})
        return bm

    # ----- build all gear -----
    def build_gear(self):
        items = []
        for g in self.spec.get('gear', []):
            items.append(g)
            if g.get('mirror'):
                items.append(mirrored(g))
        for g in items:
            bm = getattr(self, 'g_' + g['type'])(g)
            bm.faces.ensure_lookup_table()
            label = g.get('label', g['type'])
            self.pieces.append((label, g.get('zone', 'free'), bm, g))
            if g.get('surface', True):
                self.surfaces.append((bm, BVHTree.FromBMesh(bm)))
            print('OPS gear %-34s %-10s tris %4d' % (label, g.get('zone', 'free'), sum(len(f.verts) - 2 for f in bm.faces)))

    def envelope(self):
        # extents of every gear piece in the rest pose and the checks of the agreed envelope
        E = ENV
        feet = min(v.co.z for v in self.out_bm.verts)
        for label, zone, bm, g in self.pieces:
            vs = [v.co for v in bm.verts]
            r = {'label': label, 'zone': zone, 'tris': sum(len(f.verts) - 2 for f in bm.faces),
                 'x': [round(min(v.x for v in vs), 4), round(max(v.x for v in vs), 4)],
                 'y': [round(min(v.y for v in vs), 4), round(max(v.y for v in vs), 4)],
                 'z': [round(min(v.z for v in vs), 4), round(max(v.z for v in vs), 4)]}
            bad = []
            if min(v.z for v in vs) < feet:
                bad.append('below the soles')
            if zone == 'torso':
                t = E['torso']
                for v in vs:
                    band = t['z'][0] <= v.z <= t['z'][1]
                    if v.y < t['front'] - 1e-4: bad.append('front y %.4f at z %.3f' % (v.y, v.z)); break
                    if v.y > t['back'] + 1e-4: bad.append('back y %.4f at z %.3f' % (v.y, v.z)); break
                    if abs(v.x) > (t['half_x'] if band else t['strap_x']) + 1e-4: bad.append('|x| %.4f at z %.3f' % (abs(v.x), v.z)); break
            elif zone == 'belt':
                t = E['belt']
                for v in vs:
                    if abs(v.x) > t['half_x'] + 1e-4 or not t['y'][0] - 1e-4 <= v.y <= t['y'][1] + 1e-4 or \
                            not t['z'][0] - 1e-4 <= v.z <= t['z'][1] + 1e-4:
                        bad.append('outside the belt box at %s' % [round(x, 3) for x in v]); break
            elif zone in ('head', 'head_small'):
                t = E['head']
                c, ax = Vector(t['centre']), Vector(t['axes'])
                q = [((v.x - c.x) / ax.x) ** 2 + ((v.y - c.y) / ax.y) ** 2 + ((v.z - c.z) / ax.z) ** 2 for v in vs]
                r['ellipsoid_max'] = round(max(q), 3)
                if max(v.z for v in vs) > t['top'] + 1e-4:
                    bad.append('top z %.4f' % max(v.z for v in vs))
                if zone == 'head' and max(q) > 1.0 + 1e-3:
                    bad.append('outside the head ellipsoid (%.3f)' % max(q))
                if zone == 'head_small':
                    if min(v.y for v in vs) < t['small_front'] - 1e-4:
                        bad.append('small item front y %.4f' % min(v.y for v in vs))
                    if max(max(r['x']) - min(r['x']), max(r['y']) - min(r['y']), max(r['z']) - min(r['z'])) > t['small_size']:
                        bad.append('small item larger than %.2f m' % t['small_size'])
            elif zone in ('limb', 'layer'):
                dmax = max(self.out_tree.find_nearest(v)[3] for v in vs)
                r['max_off_surface'] = round(dmax, 4)
                if dmax > E[zone]['max_off'] + 1e-4:
                    bad.append('%.4f m off the outfit' % dmax)
            r['ok'] = not bad
            if bad:
                r['problems'] = bad
                print('OPS ENVELOPE %s %s: %s' % (self.name, label, '; '.join(bad)))
            self.report['gear'].append(r)

    def finish(self):
        # the outfit faces under the shells go, then the gear pieces become objects with the outfit's vertex groups
        # and materials and are joined into the one mesh
        if self.hidden:
            bm = bmesh.new()
            bm.from_mesh(self.O.data)
            bm.faces.ensure_lookup_table()
            bmesh.ops.delete(bm, geom=[bm.faces[k] for k in sorted(self.hidden)], context='FACES')
            bm.to_mesh(self.O.data)
            bm.free()
            self.report['hidden_outfit_faces'] = len(self.hidden)
        names = [vg.name for vg in self.O.vertex_groups]
        objs = []
        for k, (label, zone, bm, g) in enumerate(self.pieces):
            me = bpy.data.meshes.new('gear%d' % k)
            bm.to_mesh(me)
            for m in self.mats:
                me.materials.append(m)
            ob = bpy.data.objects.new('gear%d' % k, me)
            self.scene.collection.objects.link(ob)
            for n in names:
                ob.vertex_groups.new(name=n)
            ob.parent = self.arm
            ob.modifiers.new('Armature', 'ARMATURE').object = self.arm
            objs.append(ob)
        if objs:
            bpy.ops.object.select_all(action='DESELECT')
            for o in objs + [self.O]:
                o.select_set(True)
            bpy.context.view_layer.objects.active = self.O
            bpy.ops.object.join()
        me = self.O.data
        used = {}
        for p in me.polygons:
            used[p.material_index] = used.get(p.material_index, 0) + 1
        self.report['materials'] = {self.mats[i].name: {'hex': self.spec['materials'][self.keys[i]], 'faces': used.get(i, 0)}
                                    for i in range(len(self.mats))}
        for i in range(len(self.mats)):
            if not used.get(i):
                print('OPS warning: material %s is not used' % self.mats[i].name)
        self.report['tris'] = tri_count(me)
        self.report['tris_outfit'] = self.outfit_tris
        self.report['verts'] = len(me.vertices)
        self.report['meshes'] = [o.name for o in bpy.data.objects if o.type == 'MESH']
        bones = {b.name.lower() for b in self.arm.data.bones}
        for o in bpy.data.objects:
            if o.name.lower() in bones or o.data.name.lower() in bones:
                raise ValueError('object %s has the name of a bone (CharacterBody would find it instead)' % o.name)
        # actions: short names, so the FBX takes are "CharacterArmature|Run" like the originals
        for a in bpy.data.actions:
            a.name = a.name.split('|')[-1]
        ad = self.arm.animation_data
        ad.action = bpy.data.actions['Idle_Gun']
        if ad.action_slot is None and ad.action.slots:
            ad.action_slot = ad.action.slots[0]
        self.arm.data.pose_position = 'POSE'
        self.scene.frame_set(1)
        self.report['height_rest'] = self.height('REST')
        self.report['height_idle_gun'] = self.height('POSE')
        self.report['actions'] = len(bpy.data.actions)
        self.report['bones'] = len(self.arm.data.bones)

    def height(self, mode):
        self.arm.data.pose_position = mode
        bpy.context.view_layer.update()
        dg = bpy.context.evaluated_depsgraph_get()
        e = self.O.evaluated_get(dg)
        me = e.to_mesh()
        zs = [(e.matrix_world @ v.co).z for v in me.vertices]
        e.to_mesh_clear()
        self.arm.data.pose_position = 'POSE'
        return [round(min(zs), 4), round(max(zs), 4)]

    def export(self, path):
        bpy.ops.object.select_all(action='DESELECT')
        for o in (self.arm, self.O):
            o.select_set(True)
        bpy.context.view_layer.objects.active = self.arm
        os.makedirs(os.path.dirname(path), exist_ok=True)
        # the settings checked in Blender and Unity against CT_Swat: same structure as the pack files (cm units,
        # armature node scale 100, rotation -90 X), takes CharacterArmature|<clip>, no leaf bones (the *_end bones are real)
        bpy.ops.export_scene.fbx(
            filepath=path, use_selection=True, object_types={'ARMATURE', 'MESH'},
            global_scale=1.0, apply_unit_scale=True, apply_scale_options='FBX_SCALE_NONE',
            axis_forward='-Z', axis_up='Y', use_space_transform=True, bake_space_transform=False,
            use_mesh_modifiers=False, mesh_smooth_type='FACE', use_tspace=False, use_custom_props=False,
            add_leaf_bones=False, primary_bone_axis='Y', secondary_bone_axis='X', use_armature_deform_only=False,
            armature_nodetype='NULL', bake_anim=True, bake_anim_use_all_bones=True, bake_anim_use_nla_strips=False,
            bake_anim_use_all_actions=True, bake_anim_force_startend_keying=True, bake_anim_step=1.0,
            bake_anim_simplify_factor=1.0, path_mode='AUTO', embed_textures=False)
        self.report['file'] = path
        self.report['size'] = os.path.getsize(path)
        print('OPS exported %s (%d bytes, %d tris)' % (path, self.report['size'], self.report['tris']))

    def preview(self, folder):
        # quick workbench renders: rest pose front / side / back and Idle_Gun front / side
        os.makedirs(folder, exist_ok=True)
        sc = self.scene
        sc.render.engine = 'BLENDER_WORKBENCH'
        sc.display.shading.light = 'STUDIO'
        sc.display.shading.color_type = 'MATERIAL'
        sc.display.shading.show_cavity = True
        sc.world = bpy.data.worlds.new('W')
        sc.world.color = (0.32, 0.34, 0.37)
        sc.render.resolution_x, sc.render.resolution_y = 480, 900
        sc.view_settings.view_transform = 'Standard'
        cd = bpy.data.cameras.new('Cam')
        cd.type = 'ORTHO'
        cd.ortho_scale = 2.0
        cam = bpy.data.objects.new('Cam', cd)
        sc.collection.objects.link(cam)
        sc.camera = cam
        for tag, mode, loc, rot in (('rest_front', 'REST', (0, -6, 0.95), (90, 0, 0)), ('rest_side', 'REST', (6, 0, 0.95), (90, 0, 90)),
                                    ('rest_back', 'REST', (0, 6, 0.95), (90, 0, 180)), ('idle_front', 'POSE', (0, -6, 0.95), (90, 0, 0)),
                                    ('idle_side', 'POSE', (6, 0, 0.95), (90, 0, 90)), ('idle_q', 'POSE', (4.5, -4.5, 1.6), (82, 0, 45))):
            self.arm.data.pose_position = mode
            sc.frame_set(1)
            cam.location = loc
            cam.rotation_euler = [math.radians(a) for a in rot]
            sc.render.filepath = os.path.join(folder, '%s_%s.png' % (self.name, tag))
            bpy.ops.render.render(write_still=True)
        self.arm.data.pose_position = 'POSE'


reports = []
try:
    for name in models:
        spec = cfg['models'][name]
        b = Builder(name, spec)
        b.load_parts()
        b.build_gear()
        b.envelope()
        b.finish()
        b.export(os.path.join(out_dir, name + '.fbx'))
        if 'blend' in opts:
            os.makedirs(opts['blend'], exist_ok=True)
            bpy.ops.wm.save_as_mainfile(filepath=os.path.join(os.path.abspath(opts['blend']), name + '.blend'))
        if 'preview' in opts:
            b.preview(os.path.abspath(opts['preview']))
        reports.append(b.report)
        print('OPS %s: %d tris (outfit %d), %d materials, rest z %s, Idle_Gun z %s' % (
            name, b.report['tris'], b.report['tris_outfit'], len(b.mats), b.report['height_rest'], b.report['height_idle_gun']))
finally:
    if PACK_TMP:
        shutil.rmtree(PACK_TMP, ignore_errors=True)
if 'report' in opts:
    json.dump(reports, open(os.path.abspath(opts['report']), 'w'), indent=1)
    print('OPS report', os.path.abspath(opts['report']))
