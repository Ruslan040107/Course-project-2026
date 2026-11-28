"""Build editable Blender maps from a local CS2 installation, preserving local albedo textures, geometry and navigation references.

blender -b -P Tools/Blender/make_classic_maps.py -- --vrf PATH/Source2Viewer-CLI.exe
An existing .blend opens independently of Source 2 Viewer and the game.
"""
import argparse
import csv
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from collections import defaultdict
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector
from mathutils.bvhtree import BVHTree


PROJECT = Path(__file__).resolve().parents[2]
BLENDS = PROJECT / "Tools/Blender/Maps"
EXPORTS = PROJECT / "Assets/Models/Maps"
SCALE = .036  # m per Hammer unit; doorways also accommodate the course project's taller capsule.
SOURCE_METRE = .0254
MAPS = {
    "Mirage": ("de_mirage", (-640, -850, -165), (-3230, 1713, 5.0)),
    "Dust2": ("de_dust2", (-200, 1030, 20), (-2476, 3239, 4.4)),
}


def extract(cli, cache, slug):
    vpk = "steam:730/game/csgo/maps/" + slug + ".vpk"
    def run(asset, output, *flags):
        subprocess.run([cli, "-i", vpk, "-f", asset, "--game", "steam:730/game/csgo",
                        "-o", str(output), *flags, "-q"], check=True)
    prefix = "maps/" + slug + "/"
    run(prefix+"world.vwrld_c", cache/(slug+"_surfaces.glb"), "--gltf_export_format", "glb", "--gltf_export_materials")
    run(prefix+"world_physics.vmdl_c", cache/(slug+".glb"), "--gltf_export_format", "glb", "--gltf_export_materials")
    run("maps/"+slug+".nav", cache/(slug+"_nav"), "-d")
    run(prefix+"entities/default_ents.vents_c", cache/(slug+"_ents"), "-d")


class Glb:
    def __init__(self, path):
        self.directory = path.parent
        with path.open("rb") as f:
            if f.read(4) != b"glTF": raise ValueError(path)
            f.seek(12)
            length, kind = struct.unpack("<II", f.read(8))
            self.doc = json.loads(f.read(length))
            length, kind = struct.unpack("<II", f.read(8))
            self.bin = f.read(length)

    def array(self, idx):
        acc = self.doc["accessors"][idx]
        view = self.doc["bufferViews"][acc["bufferView"]]
        dtype = {5121:"u1",5123:"<u2",5125:"<u4",5126:"<f4"}[acc["componentType"]]
        width = {"SCALAR":1,"VEC2":2,"VEC3":3,"VEC4":4}[acc["type"]]
        offset = view.get("byteOffset",0) + acc.get("byteOffset",0)
        step = np.dtype(dtype).itemsize
        return np.ndarray((acc["count"],width),dtype,buffer=self.bin,offset=offset,
                          strides=(view.get("byteStride",width*step),step))

    def local_surface(self, index, title):
        source = self.doc["materials"][index]
        pbr = source.get("pbrMetallicRoughness", {})
        if "baseColorTexture" not in pbr: return None
        name = source["name"].lower()
        if any(tag in name for tag in ("tools/", "toolsnodraw", "toolssky", "sky_dome", "shadowcard", "lightbeam", "lightshaft", "volumetric")):
            return None
        alpha = source.get("alphaMode", "OPAQUE")
        if alpha == "BLEND" and not any(tag in name for tag in ("bombsite", "sign", "graff", "poster", "letter", "wood", "grate", "rug", "branch", "leaf", "carpet", "logo")):
            return None
        factor = pbr.get("baseColorFactor", [1,1,1,1])[:3]
        if "bombsite" in name:
            factor = source.get("extras", {}).get("vmat", {}).get("VectorParams", {}).get("TextureLayer1Color", factor)[:3]
        tint = "".join(f"{int(round(min(1,max(0,c))**(1/2.2)*255)):02x}" for c in factor)
        label = re.sub(r"[^a-z0-9]+", "_", name)[:22]
        key = f"L_{title}_{index}_{'O' if alpha=='OPAQUE' else 'C'}_{tint}_{label}"
        image = self.doc["textures"][pbr["baseColorTexture"]["index"]]["source"]
        uri = self.doc["images"][image]["uri"]
        src = self.directory / uri
        dst = EXPORTS / title / (key + ".png")
        if not dst.exists() or src.stat().st_mtime > dst.stat().st_mtime:
            shutil.copy2(src, dst)
        return key

    def parts(self, title=None):
        surface_keys = {}

        nodes = self.doc["nodes"]
        def visit(index, parent):
            node = nodes[index]
            if "matrix" in node:
                local = np.array(node["matrix"]).reshape(4,4).T
            else:
                x,y,z,w = node.get("rotation",(0,0,0,1))
                local = np.array(Matrix.LocRotScale(Vector(node.get("translation",(0,0,0))),
                    Quaternion((w,x,y,z)),Vector(node.get("scale",(1,1,1)))))
            transform = parent @ local
            if "mesh" in node and "skin" not in node:
                model = self.doc["meshes"][node["mesh"]]
                for prim in model["primitives"]:
                    if prim.get("mode",4)!=4: continue
                    v = self.array(prim["attributes"]["POSITION"]) @ transform[:3,:3].T + transform[:3,3]
                    # VRF's world glTF axes are Source Y,Z,X, even for instanced props.
                    v = v[:,[2,0,1]] / SOURCE_METRE
                    idx = self.array(prim["indices"]).reshape(-1,3) if "indices" in prim else np.arange(len(v)).reshape(-1,3)
                    if np.linalg.det(transform[:3,:3])<0: idx = idx[:,[0,2,1]]
                    extra = node.get("extras",{})
                    if title:
                        i=prim.get("material")
                        if i is None or "TEXCOORD_0" not in prim["attributes"]: continue
                        if i not in surface_keys: surface_keys[i] = self.local_surface(i,title)
                        if surface_keys[i] is None: continue
                        uv=self.array(prim["attributes"]["TEXCOORD_0"]).copy()
                        uv[:,1]=1-uv[:,1]
                        extra={"key":surface_keys[i],"uv":uv}
                    yield model["name"],extra,v,idx
            for child in node.get("children",[]): yield from visit(child,transform)
        roots = self.doc["scenes"][self.doc.get("scene",0)]["nodes"]
        for root in roots: yield from visit(root,np.eye(4))


def material(name):
    mat=bpy.data.materials.get(name)
    if mat: return mat
    mat=bpy.data.materials.new(name)
    mat.use_nodes=True
    bs=mat.node_tree.nodes.get("Principled BSDF")
    fields=name.split("_")
    local=fields[0]=="L"
    tint=fields[4] if local else fields[1]
    rgb=[int(tint[i:i+2],16)/255 for i in (0,2,4)]
    color=tuple(((c+.055)/1.055)**2.4 if c>.04045 else c/12.92 for c in rgb)+(1,)
    mat.diffuse_color=color
    bs.inputs["Base Color"].default_value=color
    if local:
        path=EXPORTS/fields[1]/(name+".png")
        node=mat.node_tree.nodes.new("ShaderNodeTexImage")
        node.image=bpy.data.images.load(str(path),check_existing=True)
        if fields[3]=="C":
            mat.node_tree.links.new(node.outputs["Alpha"],bs.inputs["Alpha"])
        mix=mat.node_tree.nodes.new("ShaderNodeMixRGB")
        mix.blend_type="MULTIPLY"
        mix.inputs[0].default_value=1
        mix.inputs[2].default_value=color
        mat.node_tree.links.new(node.outputs["Color"],mix.inputs[1])
        mat.node_tree.links.new(mix.outputs[0],bs.inputs["Base Color"])
        mat.node_tree.nodes.active=node
    bs.inputs["Roughness"].default_value=.85
    return mat


def object_mesh(name, triangles, center, mat, coll):
    t=(triangles[:,:,:3]-center)*SCALE
    # Weld the soup before making colliders. Grouping by map sector keeps culling local.
    q=np.rint(t.reshape(-1,3)*500).astype(np.int32)
    points,indices=np.unique(q,axis=0,return_inverse=True)
    points=points.astype(np.float32)/500
    faces=indices.reshape(-1,3).astype(np.int32)
    valid=(faces[:,0]!=faces[:,1]) & (faces[:,1]!=faces[:,2]) & (faces[:,2]!=faces[:,0])
    faces=faces[valid]
    if not len(faces): return
    mesh=bpy.data.meshes.new(name)
    mesh.vertices.add(len(points))
    mesh.vertices.foreach_set("co",points.ravel())
    mesh.loops.add(faces.size)
    mesh.loops.foreach_set("vertex_index",faces.ravel())
    mesh.polygons.add(len(faces))
    mesh.polygons.foreach_set("loop_start",np.arange(0,faces.size,3,dtype=np.int32))
    mesh.update(calc_edges=True)
    obj=bpy.data.objects.new(name,mesh)
    coll.objects.link(obj)
    if mat:
        mesh.materials.append(material(mat))
        if triangles.shape[-1]==5:
            uv=np.ascontiguousarray(triangles[valid,:,3:],dtype=np.float32)
            mesh.uv_layers.new(name="Surface UV").uv.foreach_set("vector",uv.ravel())
    else:
        obj.display_type='WIRE'
        obj.hide_render=True
    return obj


def collect(parts, low, high, physics=False):
    groups=defaultdict(list)
    for name,extra,v,idx in parts:
        if physics:
            if extra.get("CollisionGroup","Default").lower() not in ("default","passbullets"): continue
            if any(key in name.lower() for key in ("sky","grenadeclip","playerclip","ladder_pass")): continue
            mat="Wood" if "wood" in name.lower() else "Solid"
        else:
            mat=extra["key"]
        tri=v[idx]
        c=tri.mean(axis=1)
        keep=np.all((c>low)&(c<high),axis=1)
        tri=tri[keep]; c=c[keep]
        if "uv" in extra:
            tri=np.concatenate((tri,extra["uv"][idx[keep]]),axis=2)
        if not len(c): continue
        cells=np.floor(c[:,:2]/(1800 if "uv" in extra else 850)).astype(int)
        key=cells[:,0]*100+cells[:,1]
        for k in np.unique(key):
            sel=key==k
            a,b=cells[np.argmax(sel)]
            groups[(int(a),int(b),mat)].append(tri[sel].astype(np.float32))
    return groups


def tree_from(tri):
    v,inv=np.unique(tri.reshape(-1,3).round(3),axis=0,return_inverse=True)
    return BVHTree.FromPolygons(v.tolist(),inv.reshape(-1,3).tolist(),all_triangles=True)


def anchor(coll,name,source,center):
    obj=bpy.data.objects.new(name,None)
    coll.objects.link(obj)
    obj.location=(np.array(source)-center)*SCALE
    obj.empty_display_size=.24
    return obj


def new_collection(name):
    coll=bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(coll)
    return coll


def nearest_floor(tree,p):
    hit=tree.find_nearest(Vector(p))
    if hit[0] is None: raise ValueError(p)
    return np.array(hit[0])


def perch_floor(tree,target):
    # Upper box surfaces, scaffolds and window sills may deliberately be absent from the bot navmesh.
    x,y,z=target
    hits=[]
    # Close horizontal surfaces at the requested level take priority over the ground below the box.
    for dx in range(-72,73,9):
        for dy in range(-72,73,9):
            start=Vector((x+dx,y+dy,z+66))
            for _ in range(12):
                point,normal,_,_=tree.ray_cast(start,Vector((0,0,-1)),180)
                if point is None or point.z<z-105: break
                if normal.z>.65:
                    # Avoid narrow trim and points underneath solid boxes.
                    open_above=tree.ray_cast(point+Vector((0,0,1)),Vector((0,0,1)),50)[0] is None
                    if open_above: hits.append((abs(point.z-z)*3+math.hypot(dx,dy),tuple(point)))
                start=point-Vector((0,0,.25))
    if not hits: raise ValueError("No standing surface near "+str(target))
    return np.array(min(hits)[1])


def site_stencil(coll,letter,pos,center):
    # Painted lettering and a broken circular border, laid flat over the site floor.
    tris=[]
    paths={
        "A":[[(-.77,-1), (0,1), (.77,-1)],[(-.47,-.2),(.47,-.2)]],
        "B":[[(-.56,-1),(-.56,1),(.17,1),(.59,.77),(.64,.44),(.46,.19),(-.53,0)],
             [(-.52,0),(.24,0),(.69,-.28),(.7,-.7),(.36,-1),(-.56,-1)]],
    }
    def stroke(a,b,width):
        a,b=np.array(a),np.array(b)
        axis=b-a
        n=np.array([-axis[1],axis[0]])*width/(2*np.linalg.norm(axis))
        tris.extend([[a-n,b-n,b+n],[a-n,b+n,a+n]])
    for path in paths[letter]:
        for a,b in zip(path,path[1:]): stroke(a,b,.17)
    for i in range(64):
        if i%16 in (0,1): continue
        a,b=i*math.tau/64,(i+1)*math.tau/64
        stroke((1.58*math.cos(a),1.58*math.sin(a)),(1.58*math.cos(b),1.58*math.sin(b)),.09)
    xy=np.array(tris)/SCALE
    vertices=np.concatenate((xy,np.zeros((*xy.shape[:2],1))),axis=2)+pos+[0,0,.75]
    object_mesh("D_SiteStencil_"+letter,vertices,center,"Tint_b9482e",coll)


def make_markers(title,cache,slug,radar,center,nav,solids,coll,vis):
    txt=next((cache/(slug+"_ents")).rglob("*.vents")).read_text(encoding="utf-8-sig")
    def pct(x,y,z):
        ox,oy,zoom=radar
        return (ox+x*10.24*zoom,oy-y*10.24*zoom,z)
    for team,kind in (("T","terrorist"),("CT","counterterrorist")):
        starts=[]
        facing={}
        for b in re.split(r"====\d+====",txt):
            if not re.search(r'^classname\s+"info_player_'+kind+r'"',b,re.M): continue
            vals=re.search(r"^origin\s+\[([^\]]+)\]",b,re.M)
            p=np.array([float(x) for x in vals[1].split(",")])
            floor=nearest_floor(nav,p)
            yaw=math.radians(float(re.search(r"^angles\s+\[([^\]]+)\]",b,re.M)[1].split(",")[1]))
            starts.append(floor)
            facing[tuple(floor)]=np.array([math.cos(yaw),math.sin(yaw),0])/SCALE
        # Pick five well-spaced standard positions from the actual spawn cluster.
        chosen=[starts[0]]
        while len(chosen)<5:
            chosen.append(max(starts,key=lambda p:min(np.linalg.norm(p-q) for q in chosen)))
        for i,p in enumerate(chosen):
            key=team+str(i+1)
            anchor(coll,key,p,center)
            anchor(coll,"Facing_"+key,p+facing[tuple(p)],center)
        if team=="T":
            anchor(coll,"Training",nearest_floor(nav,chosen[0]+[140,100,0]),center)
            anchor(coll,"AimTarget",nearest_floor(nav,chosen[0]+[-170,-120,0]),center)
    if title=="Mirage":
        with (PROJECT/"Tools/Blender/mirage_positions.csv").open(encoding="utf-8-sig") as f:
            for row in csv.DictReader(f):
                p=pct(float(row["radar_x_percent"]),float(row["radar_y_percent"]),float(row["height_hu"]))
                pos=perch_floor(solids,p) if row["access"]=="perch" else nearest_floor(nav,p)
                anchor(coll,"Spot_"+row["id"],pos,center)
                print("SPOT",row["id"],np.round(pos,1), "offset_m",round(np.linalg.norm(pos-np.array(p))*SCALE,2))
        sites=[(54,76,-168),(23,28,-160)]
        routes=[("Apartments",33,18,-40),("Window",40,50,-165),("Underpass",43,32,-325),
                ("Connector",50,56,-170),("Palace",75,76,-40)]
    else:
        sites=[(80,16,90),(21,12,50)]
        routes=[("Long",80,52,0),("Mid",53,42,-70),("Tunnels",20,37,60),("Short",65,34,40)]
    for letter,p in zip("AB",sites):
        foot=nearest_floor(nav,pct(*p))
        anchor(coll,"Site"+letter,foot,center)
        floor=solids.ray_cast(Vector(foot)+Vector((0,0,8)),Vector((0,0,-1)),24)[0]
        if floor is not None and not any("bombsite_"+letter.lower() in obj.name for obj in vis.objects): site_stencil(vis,letter,np.array(floor),center)
    for name,x,y,h in routes:
        anchor(coll,"Route_"+name,nearest_floor(nav,pct(x,y,h)),center)


def build(title,cache):
    slug,ctr,radar=MAPS[title]
    center=np.array(ctr)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version=0
    vis=new_collection("Architecture and props")
    hulls=new_collection("Game collision")
    markers=new_collection("Callouts and gameplay")
    refnav=next((cache/(slug+"_nav")).rglob("*.glb"))
    _,_,verts,idx=next(Glb(refnav).parts())
    nav=tree_from(verts[idx])
    low=verts.min(axis=0)-[480,480,150]
    high=verts.max(axis=0)+[480,480,800]
    physical=collect(Glb(cache/(slug+"_physics.glb")).parts(),low,high,True)
    solids=tree_from(np.concatenate([a for parts in physical.values() for a in parts]))
    for (a,b,kind),chunks in physical.items():
        object_mesh(f"C_{kind}_{a}_{b}",np.concatenate(chunks),center,None,hulls)
    del physical
    (EXPORTS/title).mkdir(parents=True,exist_ok=True)
    visual=collect(Glb(cache/(slug+"_surfaces.glb")).parts(title),low,high)
    for (a,b,mat),chunks in visual.items():
        object_mesh(f"D_{a}_{b}_{mat}",np.concatenate(chunks),center,mat,vis)
    del visual
    make_markers(title,cache,slug,radar,center,nav,solids,markers,vis)
    # Collision meshes remain editable, but are hidden while inspecting the Blender scene.
    hulls.hide_viewport=True
    for img in bpy.data.images:
        if img.filepath: img.filepath="//"+Path(os.path.relpath(bpy.path.abspath(img.filepath),BLENDS)).as_posix()
    bpy.context.scene.unit_settings.system='METRIC'
    for screen in bpy.data.screens:
        for window in screen.areas:
            if window.type=='VIEW_3D':
                view=window.spaces.active
                view.region_3d.view_distance=200
                view.region_3d.view_rotation=Vector((-45,60,-75)).to_track_quat('-Z','Y')
                view.shading.color_type='TEXTURE'
    bpy.ops.wm.save_as_mainfile(filepath=str(BLENDS/(title+".blend")),compress=True,relative_remap=False)
    hulls.hide_viewport=False
    bpy.ops.export_scene.fbx(filepath=str(EXPORTS/(title+".fbx")),object_types={'MESH','EMPTY'},
                             add_leaf_bones=False,bake_anim=False,path_mode='STRIP',
                             axis_forward='-Z',axis_up='Y',mesh_smooth_type='OFF')


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--cache",type=Path,default=Path(tempfile.gettempdir())/"blender-classic-map-input")
    parser.add_argument("--vrf",help="Source 2 Viewer CLI executable for reading the installed maps")
    parser.add_argument("--map",choices=MAPS)
    args=parser.parse_args(sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else [])
    args.cache.mkdir(parents=True,exist_ok=True)
    BLENDS.mkdir(parents=True,exist_ok=True)
    EXPORTS.mkdir(parents=True,exist_ok=True)
    for title in ([args.map] if args.map else MAPS):
        if args.vrf: extract(args.vrf,args.cache,MAPS[title][0])
        build(title,args.cache)

