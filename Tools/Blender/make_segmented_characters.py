"""Create the two segmented, untextured character source files with stock Blender."""
import math
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector

OUT = Path(__file__).resolve().parent / "Characters"
OUT.mkdir(exist_ok=True)
TAU = math.tau


def material(name, hex_color, roughness=.82):
    rgb = [int(hex_color[i:i+2], 16)/255 for i in (0, 2, 4)]
    linear = tuple(((v+.055)/1.055)**2.4 if v>.04045 else v/12.92 for v in rgb)+(1,)
    m = bpy.data.materials.new(name)
    m.diffuse_color = linear
    m.use_nodes = True
    shader = m.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = linear
    shader.inputs["Roughness"].default_value = roughness
    return m


def loft(name, rows, mat, parent=None, sides=8):
    # Rows are section centres and elliptical radii, ordered from feet towards head.
    origin = Vector(rows[0][:3])
    vertices = []
    for x,y,z,w,d in rows:
        for i in range(sides):
            a = TAU*(i+.5)/sides
            vertices.append(Vector((x+w*math.cos(a), y+d*math.sin(a), z))-origin)
    last = sides*(len(rows)-1)
    faces = [tuple(reversed(range(sides))), tuple(last+i for i in range(sides))]
    for base in range(0,last,sides):
        for i in range(sides):
            j=(i+1)%sides
            faces.append((base+i,base+j,base+sides+j,base+sides+i))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices,[],faces)
    mesh.update()
    obj = bpy.data.objects.new(name,mesh)
    bpy.context.scene.collection.children[0].objects.link(obj)
    obj.data.materials.append(mat)
    obj.location = origin
    if parent:
        bpy.context.view_layer.update()
        obj.parent = parent
        obj.location = parent.matrix_world.inverted() @ origin
    return obj


def panel(name,x,y,z,w,d,h,mat,parent):
    return loft(name,[(x,y,z-h*.5,w*.84,d*.8),(x,y,z-h*.38,w,d),
                      (x,y,z+h*.35,w,d),(x,y,z+h*.5,w*.82,d*.78)],mat,parent,6)


def make(side):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    ct = side=="CT"
    scene = bpy.context.scene
    col = bpy.data.collections.new(side+"_Character")
    scene.collection.children.link(col)
    cloth = material("Field uniform","354659" if ct else "756143")
    pants = material("Trousers","293848" if ct else "666b4c")
    leather = material("Boot leather","282c30" if ct else "453b34",.75)
    gear = material("Carrier","414a50" if ct else "54583e")
    trim = material("Webbing","272e34" if ct else "424634")
    edge = material("Stitch and binding","67727b" if ct else "888469")
    dark = material("Rubber and knit","25272a")
    skin = material("Skin","b68265" if ct else "a67a5a")
    eye = material("Eye shadow","453833")
    accent = material("Badge or scarf border","9ba8af" if ct else "a18b67")
    def body(name,sections,mat,parent=None,n=10):
        return loft(name,[(0,y,z,w,d) for z,w,d,y in sections],mat,parent,n)
    pelvis=body("Pelvis",[(.82,.125,.087,0),(.9,.166,.11,.012),(1.015,.15,.10,0)],pants)
    torso=body("Torso",[(.97,.15,.103,0),(1.07,.147,.105,0),(1.25,.183,.121,0),
        (1.41,.213,.116,.004),(1.49,.18,.092,0),(1.528,.078,.065,0)],cloth,pelvis)
    head=body("Head",[(1.49,.055,.054,.006),(1.555,.057,.057,0),(1.576,.058,.073,-.019),
        (1.617,.089,.09,-.013),(1.695,.093,.101,0),(1.756,.09,.096,.004),
        (1.795,.066,.077,.005),(1.812,.033,.043,.005)],skin,torso,12)
    loft("Nose",[(0,-.094,1.643,.023,.018),(0,-.106,1.657,.021,.025),
        (0,-.09,1.696,.011,.015)],skin,head,6)
    carrier=body("Plate_Carrier" if ct else "Tactical_Vest",[(1.075,.16,.109,0),(1.105,.173,.126,-.004),
        (1.335,.191,.143,0),(1.415,.158,.119,0)],gear,torso)
    body("Belt",[(.981,.155,.106,0),(1.022,.156,.107,0)],trim,pelvis)
    panel("Buckle",0,-.105,1.003,.034,.01,.036,edge,pelvis)
    for idx,x in enumerate((-.091,0,.091)):
        panel("Pouch_"+str(idx+1),x,-.125,1.172,.042,.033,.142,gear,torso)
        panel("Pouch_Flap_"+str(idx+1),x,-.15,1.211,.043,.013,.038,trim,torso)
    for sign,tag in ((1,"_L"),(-1,"_R")):
        def limb(name,sections,mat,parent,n=8):
            return loft(name+tag,[(sign*x,y,z,w,d) for x,y,z,w,d in sections],mat,parent,n)
        limb("Shoulder_Strap",[(.12,-.061,1.38,.033,.064),(.121,-.031,1.46,.032,.06),
             (.12,0,1.49,.032,.049)],trim,torso)
        arm=limb("UpperArm",[(.278,.007,1.13,.05,.052),(.254,.004,1.237,.062,.066),
             (.215,0,1.386,.077,.079),(.195,.001,1.454,.049,.054)],cloth,torso)
        fore=limb("LowerArm",[(.306,-.043,.907,.034,.036),(.303,-.029,.963,.047,.049),
             (.294,-.004,1.067,.054,.055),(.278,.008,1.154,.05,.054)],cloth,arm)
        hand=limb("Hand",[(.314,-.062,.789,.023,.023),(.316,-.065,.824,.037,.035),
             (.313,-.059,.87,.041,.036),(.306,-.046,.925,.034,.031)],dark if ct else skin,fore)
        limb("Thumb",[(.287,-.091,.827,.014,.015),(.286,-.087,.852,.019,.018),
             (.292,-.069,.883,.018,.017)],dark if ct else skin,hand,6)
        thigh=limb("UpperLeg",[(.116,0,.502,.065,.074),(.114,.003,.573,.078,.085),
             (.103,.01,.737,.088,.10),(.094,.012,.922,.086,.10)],pants,pelvis)
        shin=limb("LowerLeg",[(.123,.014,.156,.048,.054),(.123,.018,.225,.059,.067),
             (.12,.021,.372,.073,.08),(.116,0,.517,.065,.073)],pants,thigh)
        foot=limb("Foot",[(.123,-.03,.023,.06,.115),(.123,-.036,.069,.067,.129),
             (.123,-.016,.105,.061,.105),(.123,.017,.179,.049,.058)],leather,shin)
        limb("Boot_Sole",[(.123,-.038,.001,.066,.126),(.123,-.038,.031,.068,.13)],dark,foot)
        limb("Boot_Cuff",[(.123,.015,.15,.058,.066),(.123,.015,.208,.061,.068)],leather,foot)
        panel("Cargo_Pocket"+tag,.168*sign,.015,.717,.039,.064,.145,cloth,thigh)
        panel("Eye"+tag,.037*sign,-.094,1.688,.02,.008,.012,eye,head)
        panel("Brow"+tag,.039*sign,-.094,1.702,.027,.009,.012,leather,head)
        if ct:
            panel("Knee_Pad"+tag,.116*sign,-.07,.503,.058,.029,.132,gear,shin)
            panel("Elbow_Pad"+tag,.284*sign,.051,1.136,.044,.027,.091,gear,fore)
            panel("Glove_Cuff"+tag,.306*sign,-.045,.917,.039,.041,.04,dark,hand)
            panel("Earcup"+tag,.097*sign,.003,1.672,.024,.044,.081,dark,head)
        else:
            panel("Jacket_Elbow_Patch"+tag,.28*sign,.049,1.13,.042,.015,.086,leather,fore)
            panel("Jacket_Cuff"+tag,.306*sign,-.044,.925,.038,.041,.038,leather,fore)
    if ct:
        body("Ballistic_Helmet",[(1.706,.109,.114,.003),(1.733,.121,.128,.003),
            (1.79,.113,.12,.008),(1.832,.083,.093,.011),(1.85,.042,.049,.012)],gear,head,12)
        body("Helmet_Rim",[(1.708,.114,.12,.003),(1.727,.122,.129,.003)],trim,head,12)
        panel("Helmet_Front_Mount",0,-.122,1.754,.03,.009,.042,dark,head)
        panel("Chest_Patch",0,-.133,1.35,.061,.009,.03,accent,torso)
    else:
        body("Cap",[(1.715,.097,.103,.003),(1.755,.1,.106,.005),
            (1.793,.085,.094,.01),(1.815,.04,.049,.012)],pants,head,12)
        panel("Cap_Brim",0,-.093,1.724,.081,.071,.016,pants,head)
        weave=material("Bandana • terracotta cotton","b05438",.89)
        hem=material("Bandana • woven edging","e7bd88",.88)
        scarf=body("Bandana",[(1.602,.085,.094,0),(1.628,.095,.111,-.003),
            (1.667,.097,.117,-.002)],weave,head,16)
        # Drop the centre-front corner, leaving the sides and back high around the cheeks.
        for v in scarf.data.vertices:
            front=max(0,-v.co.y/.094)
            if v.index<16: v.co.z-=.105*front**7
        border=body("Bandana_Hem",[(1.644,.096,.116,-.003),(1.652,.098,.118,-.003)],hem,head,16)
        for v in border.data.vertices:
            v.co.z-=.013*max(0,-v.co.y/.118)**3
        panel("Bandana_Knot",0,.1,1.633,.031,.025,.043,weave,head)
        for sign,tag in ((1,"_L"),(-1,"_R")):
            loft("Bandana_Tie"+tag,[(sign*.047,.116,1.55,.009,.007),
                 (sign*.03,.128,1.596,.022,.009),(sign*.012,.11,1.636,.012,.009)],weave,head,6)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    bpy.ops.object.select_all(action="DESELECT")
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type=="VIEW_3D":
                sp=area.spaces.active
                sp.region_3d.view_rotation=Vector((-.22,1,-.09)).to_track_quat("-Z","Y")
                sp.region_3d.view_distance=3.55
                sp.region_3d.view_location=(0,0,.94)
                sp.shading.color_type="MATERIAL"
                sp.overlay.show_overlays=False
    path=OUT/(col.name+".blend")
    bpy.context.preferences.filepaths.save_version=0
    bpy.ops.wm.save_as_mainfile(filepath=str(path),compress=True)
    bpy.ops.wm.open_mainfile(filepath=str(path))
    objs=list(bpy.context.scene.objects)
    tris=0
    required={"Head","Torso","Pelvis"}|{a+b for a in ("UpperArm","LowerArm","Hand","UpperLeg","LowerLeg","Foot") for b in ("_L","_R")}
    assert required <= {o.name for o in objs}
    for o in objs:
        assert o.users_collection[0].name==side+"_Character" and len(o.users_collection)==1
        assert o.parent or o.name=="Pelvis"
        assert (o.scale-Vector((1,1,1))).length<1e-5 and o.rotation_euler.to_matrix().to_quaternion().angle<1e-5
        bm=bmesh.new(); bm.from_mesh(o.data)
        assert all(e.is_manifold for e in bm.edges) and bm.calc_volume(signed=True)>0
        assert all(f.calc_area()>1e-9 for f in bm.faces)
        bm.free()
        o.data.calc_loop_triangles();tris+=len(o.data.loop_triangles)
        nodes=o.active_material.node_tree.nodes
        assert .699<=nodes.get("Principled BSDF").inputs["Roughness"].default_value<=.901 and not any(n.type=="TEX_IMAGE" for n in nodes)
    assert tris<3000
    print(f"VERIFIED {side}: {len(objs)} objects, {tris} triangles",flush=True)

for side in ("T","CT"):
    make(side)

