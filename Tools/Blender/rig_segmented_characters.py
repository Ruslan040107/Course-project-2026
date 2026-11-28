"""Fit the segmented Blender characters to the existing animated project skeleton."""
from pathlib import Path
import bpy
from mathutils import Vector

here = Path(__file__).resolve().parent
resources = here.parents[1] / "Assets/Resources/Characters"
for team in ("T","CT"):
    name = team + "_Character"
    bpy.ops.wm.open_mainfile(filepath=str(here/"Characters"/(name+".blend")))
    parts = list(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=str(resources/"CT_SAS.fbx"))
    rig = next(o for o in bpy.context.scene.objects if o.type=="ARMATURE")
    vertices, polygons, materials, face_materials, influences = [], [], [], [], []
    for part in parts:
        base=part
        while base.name not in ("Torso","Pelvis","Head") and not base.name.startswith(("UpperArm_","LowerArm_","Hand_","UpperLeg_","LowerLeg_","Foot_")):
            base=base.parent
        bone=base.name.replace("_",".").replace("Hand.","Wrist.").replace("Pelvis","Hips")
        start=len(vertices)
        for vert in part.data.vertices:
            p=part.matrix_world @ vert.co
            weights={bone:1.0}
            if bone.startswith(("UpperArm.","LowerArm.","Wrist.")):
                sign=1 if bone.endswith(".L") else -1
                pts=[Vector((sign*x,y,z)) for x,y,z in ((.195,0,1.432),(.278,.007,1.139),(.306,-.043,.919),(.316,-.065,.795))]
                idx={"UpperArm":0,"LowerArm":1,"Wrist":2}[bone.split(".")[0]]
                b=rig.data.bones[bone]
                origin=rig.matrix_world @ b.head_local
                direction=(rig.matrix_world @ b.tail_local)-origin
                src=pts[idx+1]-pts[idx]
                axis=src.normalized()
                axial=axis.dot(p-pts[idx])
                radial=p-pts[idx]-axis*axial
                along=axial*(direction.length/src.length if idx<2 else 1)
                p=origin+direction.normalized()*along+axis.rotation_difference(direction.normalized()) @ radial
            else:
                p.y-=.04
                if bone=="Torso":
                    # Blend the torso through the spine, including the equipment over it.
                    z=p.z
                    lower,upper,lo,hi=("Abdomen","Torso",1.04,1.23) if z<1.23 else ("Torso","Chest",1.23,1.41)
                    f=max(0,min(1,(z-lo)/(hi-lo)))
                    weights={lower:1-f,upper:f}
            vertices.append(p)
            influences.append(weights)
        mat=part.active_material
        if mat not in materials: materials.append(mat)
        for face in part.data.polygons:
            polygons.append([start+i for i in face.vertices])
            face_materials.append(materials.index(mat))
    mesh=bpy.data.meshes.new(name+"_Surface")
    mesh.from_pydata(vertices,[],polygons);mesh.update()
    obj=bpy.data.objects.new(name+"_Surface",mesh);bpy.context.scene.collection.objects.link(obj)
    for m in materials:mesh.materials.append(m)
    for face,i in zip(mesh.polygons,face_materials):face.material_index=i
    for i,weights in enumerate(influences):
        for bone,w in weights.items():
            if w>0:
                assert rig.data.bones.get(bone)
                g=obj.vertex_groups.get(bone) or obj.vertex_groups.new(name=bone)
                g.add([i],w,'REPLACE')
    obj.parent=rig;obj.matrix_parent_inverse=rig.matrix_world.inverted()
    mod=obj.modifiers.new("Skin","ARMATURE");mod.object=rig
    for action in bpy.data.actions:action.name=action.name.split("|")[-1]
    rig.animation_data.action=bpy.data.actions["Idle_Gun"]
    bpy.context.scene.frame_set(1)
    bpy.ops.object.select_all(action="DESELECT");obj.select_set(True);rig.select_set(True)
    bpy.context.view_layer.objects.active=rig
    bpy.ops.export_scene.fbx(filepath=str(resources/(name+".fbx")),use_selection=True,object_types={'ARMATURE','MESH'},
        add_leaf_bones=False,use_mesh_modifiers=False,bake_anim_use_nla_strips=False,bake_anim_use_all_actions=True)
