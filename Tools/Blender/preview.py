import bpy, sys, os, glob, mathutils
src, out = sys.argv[sys.argv.index('--')+1:][:2]
files = sorted(glob.glob(os.path.join(src, '**', '*.fbx'), recursive=True))
report = []
for f in files:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=f)
    objs = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    pts = [o.matrix_world @ mathutils.Vector(c) for o in objs for c in o.bound_box]
    mn = mathutils.Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    mx = mathutils.Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    size = mx - mn; c = (mn + mx) / 2
    mats = sorted({s.material.name for o in objs for s in o.material_slots if s.material})
    report.append(f"{os.path.basename(f)} size={tuple(round(v,3) for v in size)} objs={[o.name for o in objs]} mats={mats}")
    # camera looking along the smallest axis
    axis = min(range(3), key=lambda i: size[i])
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    bpy.context.scene.collection.objects.link(cam)
    d = mathutils.Vector((0,0,0)); d[axis] = 1
    cam.location = c + d * (max(size) * 3)
    cam.rotation_euler = (c - cam.location).to_track_quat('-Z', 'Z' if axis != 2 else 'Y').to_euler()
    cam.data.type = 'ORTHO'; cam.data.ortho_scale = max(size) * 1.1
    bpy.context.scene.camera = cam
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.color_type = 'MATERIAL'
    sc.display.shading.light = 'STUDIO'
    sc.render.resolution_x, sc.render.resolution_y = 480, 240
    sc.render.filepath = os.path.join(out, os.path.basename(f)[:-4] + '.png')
    bpy.ops.render.render(write_still=True)
open(os.path.join(out, 'report.txt'), 'w').write('\n'.join(report))
