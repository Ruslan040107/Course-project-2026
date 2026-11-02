# Blender CLI: a Poly Haven glTF (1k) -> FBX for Unity. Called by fetch_map_assets.py, one prop per run.
#   blender -b --factory-startup -P gltf2fbx.py -- <in.gltf> <out.fbx>
# - Unity axes: -Z forward and Y up with the transform baked, so the objects come in unrotated, 1 unit = 1 m
#   (the same export settings as Tools/Blender/process.py). The front of a Poly Haven model (glTF +Z) faces Unity +Z.
# - Textures are not copied or embedded (path_mode STRIP): fetch_map_assets.py puts them beside the FBX and
#   Assets/Editor/PropImport.cs makes the URP materials from them.
# - Material names must be unique: Blender renames a second "covered_car" to "covered_car.001", and the materials
#   made in Unity would not match. Duplicates get "_2", "_3" ... in a temporary copy of the glTF (same folder, so the
#   .bin and texture paths still work); "gltfMaterials" lists the names in the order of the glTF materials.
# - Prints one line "PROPINFO {json}": size in Unity axes (x, y = height, z), the bottom of the bounds, triangles and materials.
import bpy, sys, os, json, mathutils

argv = sys.argv[sys.argv.index('--') + 1:]
src, out = argv[0], argv[1]
os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)

with open(src, encoding='utf-8') as f:
    gltf = json.load(f)
names, seen = [], {}
for i, m in enumerate(gltf.get('materials', [])):
    base = m.get('name') or 'material_%d' % i
    seen[base] = seen.get(base, 0) + 1
    names.append(base if seen[base] == 1 else '%s_%d' % (base, seen[base]))
load = src
if names != [m.get('name') for m in gltf.get('materials', [])]:
    for m, n in zip(gltf['materials'], names):
        m['name'] = n
    load = os.path.splitext(src)[0] + '.unique.gltf'
    with open(load, 'w', encoding='utf-8') as f:
        json.dump(gltf, f)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=load)
if load != src:
    os.remove(load)

meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
mn = mathutils.Vector((1e9, 1e9, 1e9))
mx = -mn
tris = 0
for o in meshes:
    for v in o.bound_box:
        w = o.matrix_world @ mathutils.Vector(v)
        mn = mathutils.Vector(map(min, mn, w))
        mx = mathutils.Vector(map(max, mx, w))
    o.data.calc_loop_triangles()
    tris += len(o.data.loop_triangles)

# lights and cameras never go to the FBX (the export takes meshes and empties only)
size = mx - mn
centre = (mn + mx) * 0.5
info = {
    'objects': len(meshes),
    'tris': tris,
    # Blender (x, y, z up) -> Unity (-x, z, -y); sizes are positive, the bottom centre in Unity axes
    'size': [round(size.x, 4), round(size.z, 4), round(size.y, 4)],
    'bottomCentre': [round(-centre.x, 4), round(mn.z, 4), round(-centre.y, 4)],
    'materials': sorted({s.material.name for o in meshes for s in o.material_slots if s.material}),
    'gltfMaterials': names,
}

bpy.ops.export_scene.fbx(filepath=out, use_selection=False, object_types={'MESH', 'EMPTY'},
                         apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                         bake_space_transform=True, add_leaf_bones=False, mesh_smooth_type='FACE',
                         path_mode='STRIP', embed_textures=False)
print('PROPINFO ' + json.dumps(info))
