# Builds the 3D assets of Galasin (gobak sodor) and exports them as
#   wwwroot/models/anak.glb     the player: one rigged, animated kid (Idle, Run, Tag, Cheer, Sad)
#   wwwroot/models/kampung.glb  the yard around the court: pos ronda, mango tree, gapura,
#                               onthel bicycle, and a flagpole whose flag waves on its own bones
# Run inside Blender 5.x (tested on 5.2):
#
#   exec(open(r"<repo>/Rustzor/samples/Galasin/assets-src/make_galasin.py").read())
#
# The models come from Rodin (Hyper3D) text-to-3D. The raw downloads are 7 to 9 MB each,
# so they are not committed: generate them again with the prompts in RODIN below and put
# them in assets-src/rodin/<name>.glb (git-ignored), or point GALASIN_RODIN at a folder.
# Everything after the download is this script: decimation, the rig (heat-map weights),
# the team-colour mask, every animation, the procedural flagpole, smaller textures and
# the export. It works in its own scene ("Galasin") and removes it afterwards, so the
# scene that was open is left untouched.
# Built by Gravicode Studios, led by Kang Fadhil.
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Quaternion, Vector

HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else r"C:\experiment\VibeCoding\Vibe_Framework\Rustzor\samples\Galasin\assets-src"
RAW = os.environ.get("GALASIN_RODIN") or os.path.join(HERE, "rodin")
OUT = os.path.normpath(os.path.join(HERE, "..", "wwwroot", "models"))

RODIN = {
    "kid": "Stylized low-poly Indonesian village kid game character standing in a T-pose, arms straight out to the sides, "
    "legs slightly apart, plain white short-sleeve t-shirt, dark red shorts, barefoot, short black hair, friendly face, "
    "clean simple shapes, full body, symmetrical",
    "posronda": "Low-poly stylized Indonesian village night-watch hut (pos ronda): small raised wooden platform with four "
    "bamboo posts, a corrugated tin roof and a wooden kentongan slit drum hanging at the front, game asset, clean shapes, warm colours",
    "tree": "Low-poly stylized big mango tree with a thick curved trunk and a round dense dark green canopy, game asset, clean shapes",
    "gapura": "Low-poly stylized Indonesian independence day village gate (gapura) made of bamboo poles decorated with red "
    "and white flags and pennants, game asset, clean shapes",
    "onthel": "Low-poly stylized old black Dutch-style bicycle (sepeda onthel) with a front basket, parked on its kickstand, "
    "game asset, clean shapes",
}
# name -> (faces to keep, height in metres, texture size)
PROPS = {
    "posronda": (4200, 2.9, 512),
    "tree": (3600, 6.4, 512),
    "gapura": (4200, 3.7, 1024),
    "onthel": (3600, 1.05, 512),
}
FPS = 30

previous_scene = bpy.context.window.scene if bpy.context.window else bpy.context.scene
old = bpy.data.scenes.get("Galasin")
if old:
    for o in list(old.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.scenes.remove(old)
scene = bpy.data.scenes.new("Galasin")
scene.render.fps = FPS
if bpy.context.window:
    bpy.context.window.scene = scene
made = {"objects": [], "meshes": [], "materials": [], "images": [], "actions": [], "armatures": []}


def ctx(active, selected):
    scene.view_layers[0].objects.active = active
    for o in scene.objects:
        o.select_set(o in selected)
    return bpy.context.temp_override(
        object=active, active_object=active, selected_objects=selected, selected_editable_objects=selected)


def import_raw(name):
    path = os.path.join(RAW, name + ".glb")
    if not os.path.exists(path):
        raise FileNotFoundError(f"{path}: generate it with Rodin from RODIN['{name}']")
    before_o, before_m, before_i, before_mat = set(bpy.data.objects), set(bpy.data.meshes), set(bpy.data.images), set(bpy.data.materials)
    bpy.ops.import_scene.gltf(filepath=path)
    made["meshes"] += [m for m in bpy.data.meshes if m not in before_m]
    made["images"] += [i for i in bpy.data.images if i not in before_i]
    made["materials"] += [m for m in bpy.data.materials if m not in before_mat]
    new = [o for o in bpy.data.objects if o not in before_o]
    made["objects"] += new
    mesh = next(o for o in new if o.type == "MESH")
    # Rodin wraps the mesh in an empty or two; keep only the mesh, with transforms applied.
    world = mesh.matrix_world.copy()
    mesh.parent = None
    mesh.matrix_world = world
    for o in new:
        if o is not mesh:
            bpy.data.objects.remove(o, do_unlink=True)
    with ctx(mesh, [mesh]):
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return mesh


def decimate(mesh, faces):
    ratio = min(1.0, faces / max(1, len(mesh.data.polygons)))
    m = mesh.modifiers.new("decimate", "DECIMATE")
    m.ratio = ratio
    with ctx(mesh, [mesh]):
        bpy.ops.object.modifier_apply(modifier=m.name)
    bm = bmesh.new()
    bm.from_mesh(mesh.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0015)
    bm.to_mesh(mesh.data)
    bm.free()


def ground(mesh, height=None):
    """Origin at the bottom centre, standing on z = 0, optionally scaled to a height."""
    vs = [v.co for v in mesh.data.vertices]
    lo = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
    hi = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    s = height / (hi.z - lo.z) if height else 1.0
    c = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
    for v in mesh.data.vertices:
        v.co = (v.co - c) * s
    mesh.data.update()


def base_color_image(mesh):
    mat = mesh.data.materials[0]
    tex = next(n for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and "diffuse" in n.image.name.lower())
    return tex.image


def simple_material(name, image, size):
    """Only the base colour: the game shades stylised, so normal and roughness maps would be wasted bytes."""
    img = image.copy()
    img.name = name + " colour"
    img.scale(size, size)
    made["images"].append(img)
    m = bpy.data.materials.new(name)
    made["materials"].append(m)
    m.use_nodes = True
    nodes = m.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.9
    bsdf.inputs["Metallic"].default_value = 0.0
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = img
    m.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return m


def replace_material(mesh, mat):
    mesh.data.materials.clear()
    mesh.data.materials.append(mat)


# ── the kid ──────────────────────────────────────────────────────────────
kid = import_raw("kid")
kid.name = kid.data.name = "Anak"
decimate(kid, 4600)
ground(kid)            # 1.72 m as modelled; the game scales it to a 1.3 m child

# Bones in parent-first order; the kid faces -Y, so +X is the kid's left.
BONES = {
    "hips": ((0, 0, 0.47), (0, 0, 0.62), None),
    "spine": ((0, 0, 0.62), (0, 0, 0.82), "hips"),
    "chest": ((0, 0, 0.82), (0, 0, 1.02), "spine"),
    "neck": ((0, 0, 1.17), (0, 0, 1.27), "chest"),
    "head": ((0, 0, 1.27), (0, 0, 1.70), "neck"),
}
for side, sx in (("L", 1), ("R", -1)):
    BONES["shoulder." + side] = ((0.05 * sx, 0, 1.02), (0.19 * sx, 0, 1.01), "chest")
    BONES["upperarm." + side] = ((0.19 * sx, 0, 1.01), (0.47 * sx, 0, 1.0), "shoulder." + side)
    BONES["forearm." + side] = ((0.47 * sx, 0, 1.0), (0.71 * sx, 0, 0.99), "upperarm." + side)
    BONES["hand." + side] = ((0.71 * sx, 0, 0.99), (0.82 * sx, 0, 0.99), "forearm." + side)
    BONES["thigh." + side] = ((0.10 * sx, 0, 0.45), (0.135 * sx, 0, 0.25), "hips")
    BONES["shin." + side] = ((0.135 * sx, 0, 0.25), (0.17 * sx, 0, 0.08), "thigh." + side)
    BONES["foot." + side] = ((0.17 * sx, 0, 0.08), (0.2 * sx, -0.13, 0.02), "shin." + side)

arm = bpy.data.armatures.new("AnakRig")
made["armatures"].append(arm)
rig = bpy.data.objects.new("AnakRig", arm)
made["objects"].append(rig)
scene.collection.objects.link(rig)
with ctx(rig, [rig]):
    bpy.ops.object.mode_set(mode="EDIT")
    for n, (h, t, _) in BONES.items():
        b = arm.edit_bones.new(n)
        b.head, b.tail = h, t
    for n, (h, _, p) in BONES.items():
        if p:
            eb = arm.edit_bones[n]
            eb.parent = arm.edit_bones[p]
            eb.use_connect = (Vector(arm.edit_bones[p].tail) - Vector(h)).length < 1e-4
    bpy.ops.object.mode_set(mode="OBJECT")
with ctx(rig, [kid, rig]):
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")
bare = sum(1 for v in kid.data.vertices if not any(g.weight > 1e-3 for g in v.groups))
print(f"kid: {len(kid.data.polygons)} faces, {len(kid.data.vertices)} vertices, {bare} without weights")

# Team-colour mask, as a vertex colour the game reads: 1 on the shirt (bright, unsaturated
# texels between the waist and the neck), 0 elsewhere, so one model dresses either team.
img = base_color_image(kid)
w, h = img.size
px = np.empty(w * h * 4, dtype=np.float32)
img.pixels.foreach_get(px)
px = px.reshape(h, w, 4)
uv = kid.data.uv_layers.active.data
shirt = np.zeros(len(kid.data.vertices))
for poly in kid.data.polygons:
    for li in poly.loop_indices:
        vi = kid.data.loops[li].vertex_index
        z = kid.data.vertices[vi].co.z
        if not (0.58 < z < 1.2):
            continue
        u, v = uv[li].uv
        r, g, b, _ = px[min(h - 1, max(0, int(v * h))), min(w - 1, max(0, int(u * w)))]
        if min(r, g, b) > 0.55 and max(r, g, b) - min(r, g, b) < 0.09:
            shirt[vi] = max(shirt[vi], 1.0)
attr = kid.data.color_attributes.new("team", "FLOAT_COLOR", "POINT")
for i, s in enumerate(shirt):
    attr.data[i].color = (s, s, s, 1.0)
kid.data.color_attributes.active_color = attr
print(f"kid: {int(shirt.sum())} shirt vertices")
replace_material(kid, simple_material("Anak", img, 1024))


# ── kid animations ───────────────────────────────────────────────────────
def R(axis, deg):
    return Quaternion(Vector(axis), math.radians(deg))


X, Y, Z = (1, 0, 0), (0, 1, 0), (0, 0, 1)
ORDER = list(BONES)
REST = {n: rig.data.bones[n].matrix_local.to_quaternion() for n in ORDER}
last = {}


def key(frame, spec, hips=(0, 0, 0)):
    """spec: bone -> rotation in armature space, applied on top of what the bone inherits.
    Rotating an upright bone about +X leans it forward (-Y); a hanging one swings backward."""
    D = {}
    for n in ORDER:
        p = BONES[n][2]
        Dp = D[p] if p else Quaternion()
        Db = spec.get(n, Quaternion()) @ Dp
        D[n] = Db
        q = REST[n].inverted() @ Dp.inverted() @ Db @ REST[n]
        if n in last and last[n].dot(q) < 0:
            q.negate()          # stay on one hemisphere so keys interpolate the short way
        last[n] = q
        pb = rig.pose.bones[n]
        pb.rotation_mode = "QUATERNION"
        pb.rotation_quaternion = q
        pb.keyframe_insert("rotation_quaternion", frame=frame, group=n)
    pb = rig.pose.bones["hips"]
    pb.location = REST["hips"].inverted() @ Vector(hips)
    pb.keyframe_insert("location", frame=frame, group="hips")


def arms_down(spec, l=74, r=74, swing_l=0.0, swing_r=0.0):
    spec["upperarm.L"] = R(X, swing_l) @ R(Y, l)
    spec["upperarm.R"] = R(X, swing_r) @ R(Y, -r)
    return spec


def clip(name, frames, pose_at, loop=True):
    act = bpy.data.actions.new(name)
    made["actions"].append(act)
    act.use_fake_user = True
    rig.animation_data_create()
    rig.animation_data.action = act
    last.clear()
    n = frames
    for f in range(0, n + 1, 2 if n > 30 else 1):
        t = (f % n) / n if loop else f / n
        spec, hips = pose_at(t)
        key(f + 1, spec, hips)
    track = rig.animation_data.nla_tracks.new()
    track.name = name
    track.strips.new(name, 1, act)
    track.mute = False
    rig.animation_data.action = None
    return act


def idle(t):
    b = math.sin(2 * math.pi * t)
    s = arms_down({}, 74 + 2 * b, 74 + 2 * b)
    s["chest"] = R(X, 1.5 * b)
    s["head"] = R(Z, 5 * math.sin(2 * math.pi * t + 1)) @ R(X, 2 * b)
    s["forearm.L"] = s["forearm.R"] = R(X, -8)
    return s, (0, 0, 0.006 * b)


def run(t):
    p = 2 * math.pi * t
    s_ = math.sin(p)
    s = arms_down({}, 72, 72, 38 * s_, -38 * s_)
    s["thigh.L"] = R(X, -40 * s_)
    s["thigh.R"] = R(X, 40 * s_)
    s["shin.L"] = R(X, 12 + 62 * max(0.0, math.cos(p)))
    s["shin.R"] = R(X, 12 + 62 * max(0.0, -math.cos(p)))
    s["foot.L"] = R(X, -10 * s_)
    s["foot.R"] = R(X, 10 * s_)
    s["forearm.L"] = s["forearm.R"] = R(X, -72)
    s["hips"] = R(Z, -6 * s_)
    s["spine"] = R(X, 12)
    s["chest"] = R(Z, 9 * s_)
    s["head"] = R(X, -8)
    return s, (0, 0, 0.035 * math.cos(2 * p) - 0.02)


def tag(t):
    # Reach out with both hands and step in, then come back: ease in and out of the lunge.
    k = math.sin(math.pi * min(1.0, t / 0.7)) if t < 0.7 else 0.0
    k = min(1.0, k * 1.35)
    s = {}
    s["upperarm.L"] = R(Z, -90 * k) @ R(X, 10 * k) @ R(Y, 74 * (1 - k))
    s["upperarm.R"] = R(Z, 90 * k) @ R(X, 10 * k) @ R(Y, -74 * (1 - k))
    s["forearm.L"] = s["forearm.R"] = R(X, -8 * (1 - k))
    s["spine"] = R(X, 24 * k)
    s["chest"] = R(X, 8 * k)
    s["head"] = R(X, -14 * k)
    s["thigh.L"] = R(X, -42 * k)
    s["shin.L"] = R(X, 32 * k)
    s["thigh.R"] = R(X, 22 * k)
    s["shin.R"] = R(X, 14 * k)
    return s, (0, -0.1 * k, -0.07 * k)


def cheer(t):
    p = 2 * math.pi * t
    air = max(0.0, math.sin(p))
    s = arms_down({}, -62 - 12 * math.sin(2 * p), -62 - 12 * math.sin(2 * p))
    s["forearm.L"] = R(Y, -10)
    s["forearm.R"] = R(Y, 10)
    s["thigh.L"] = s["thigh.R"] = R(X, -22 * air)
    s["shin.L"] = s["shin.R"] = R(X, 44 * air)
    s["head"] = R(X, -10)
    return s, (0, 0, 0.14 * air)


def sad(t):
    b = math.sin(2 * math.pi * t)
    s = arms_down({}, 82, 82)
    s["forearm.L"] = s["forearm.R"] = R(X, -4)
    s["spine"] = R(X, 10)
    s["chest"] = R(X, 6)
    s["neck"] = R(X, 14)
    s["head"] = R(X, 16) @ R(Z, 4 * b)
    s["hips"] = R(Z, 2 * b)
    return s, (0, 0, -0.01)


clip("Idle", 60, idle)
clip("Run", 20, run)
clip("Tag", 18, tag, loop=False)
clip("Cheer", 24, cheer)
clip("Sad", 60, sad)
for pb in rig.pose.bones:
    pb.rotation_quaternion = Quaternion()
    pb.location = Vector()


def export(path, objects):
    for o in scene.objects:
        o.select_set(o in objects)
    bpy.context.view_layer.objects.active = objects[0]
    with ctx(objects[0], objects):
        bpy.ops.export_scene.gltf(
            filepath=path, export_format="GLB", use_selection=True, export_apply=False,
            export_image_format="JPEG", export_jpeg_quality=82, export_vertex_color="ACTIVE",
            export_animations=True, export_animation_mode="NLA_TRACKS", use_active_scene=True, export_force_sampling=True,
            export_optimize_animation_size=True, export_def_bones=True, export_leaf_bone=False,
            export_tangents=False, export_yup=True, export_extras=True)
    print(f"{os.path.basename(path)}: {os.path.getsize(path) / 1024:.0f} KB")


os.makedirs(OUT, exist_ok=True)
export(os.path.join(OUT, "anak.glb"), [rig, kid])

# ── the yard ─────────────────────────────────────────────────────────────
props = []
for name, (faces, height, size) in PROPS.items():
    m = import_raw(name)
    m.name = m.data.name = name
    decimate(m, faces)
    ground(m, height)
    replace_material(m, simple_material(name, base_color_image(m), size))
    m["role"] = "prop"
    props.append(m)
    print(f"{name}: {len(m.data.polygons)} faces")


def solid(name, color):
    m = bpy.data.materials.new(name)
    made["materials"].append(m)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*color, 1)
    bsdf.inputs["Roughness"].default_value = 0.8
    m.diffuse_color = (*color, 1)
    return m


# The flagpole: a pole, a finial and a two-colour flag on a chain of four bones.
POLE_H, FLAG_W, FLAG_H = 6.0, 1.5, 1.0
bm = bmesh.new()
bmesh.ops.create_cone(bm, cap_ends=True, segments=12, radius1=0.06, radius2=0.035, depth=POLE_H)
bmesh.ops.translate(bm, verts=bm.verts, vec=(0, 0, POLE_H / 2))
ball = bmesh.ops.create_icosphere(bm, subdivisions=2, radius=0.08)
bmesh.ops.translate(bm, verts=ball["verts"], vec=(0, 0, POLE_H + 0.06))
base = bmesh.ops.create_cube(bm, size=1)
bmesh.ops.scale(bm, vec=(0.7, 0.7, 0.25), verts=base["verts"])
bmesh.ops.translate(bm, verts=base["verts"], vec=(0, 0, 0.125))
pole_mesh = bpy.data.meshes.new("tiang")
made["meshes"].append(pole_mesh)
bm.to_mesh(pole_mesh)
bm.free()
pole_mesh.materials.append(solid("tiang", (0.86, 0.85, 0.8)))
pole = bpy.data.objects.new("tiang", pole_mesh)
made["objects"].append(pole)
scene.collection.objects.link(pole)

bm = bmesh.new()
COLS, ROWS = 12, 6
grid = [[bm.verts.new((FLAG_W * i / COLS + 0.05, 0, POLE_H - 0.1 - FLAG_H * (1 - j / ROWS))) for j in range(ROWS + 1)] for i in range(COLS + 1)]
for i in range(COLS):
    for j in range(ROWS):
        f = bm.faces.new((grid[i][j], grid[i + 1][j], grid[i + 1][j + 1], grid[i][j + 1]))
        f.material_index = 0 if j >= ROWS // 2 else 1   # red on top, white below
flag_mesh = bpy.data.meshes.new("bendera")
made["meshes"].append(flag_mesh)
bm.to_mesh(flag_mesh)
bm.free()
flag_mesh.materials.append(solid("merah", (0.78, 0.07, 0.09)))
flag_mesh.materials.append(solid("putih", (0.95, 0.94, 0.9)))
flag = bpy.data.objects.new("bendera", flag_mesh)
made["objects"].append(flag)
scene.collection.objects.link(flag)

farm = bpy.data.armatures.new("BenderaRig")
made["armatures"].append(farm)
frig = bpy.data.objects.new("BenderaRig", farm)
made["objects"].append(frig)
scene.collection.objects.link(frig)
FB = 4
with ctx(frig, [frig]):
    bpy.ops.object.mode_set(mode="EDIT")
    prev = None
    for i in range(FB):
        b = farm.edit_bones.new(f"kain{i}")
        b.head = (0.05 + FLAG_W * i / FB, 0, POLE_H - 0.1 - FLAG_H / 2)
        b.tail = (0.05 + FLAG_W * (i + 1) / FB, 0, POLE_H - 0.1 - FLAG_H / 2)
        if prev:
            b.parent, b.use_connect = prev, True
        prev = b
    bpy.ops.object.mode_set(mode="OBJECT")
flag.parent = frig
mod = flag.modifiers.new("rig", "ARMATURE")
mod.object = frig
groups = [flag.vertex_groups.new(name=f"kain{i}") for i in range(FB)]
for v in flag_mesh.vertices:
    u = (v.co.x - 0.05) / FLAG_W * FB      # position along the chain, in bones
    for i in range(FB):
        wgt = max(0.0, 1 - abs(u - (i + 0.5)))
        if wgt > 0:
            groups[i].add([v.index], wgt, "REPLACE")
    if u < 0.5:
        groups[0].add([v.index], 1.0, "REPLACE")
act = bpy.data.actions.new("Wave")
made["actions"].append(act)
act.use_fake_user = True
frig.animation_data_create()
frig.animation_data.action = act
FREST = {b.name: b.matrix_local.to_quaternion() for b in farm.bones}
for f in range(0, 41, 2):
    t = f / 40
    for i in range(FB):
        a = (7 + 6 * i) * math.sin(2 * math.pi * t - 0.9 * i)
        droop = 2.5 * (i + 1) * (0.6 + 0.4 * math.sin(2 * math.pi * t + 1.3))
        q = FREST[f"kain{i}"].inverted() @ (R(Z, a) @ R(Y, droop)) @ FREST[f"kain{i}"]
        pb = frig.pose.bones[f"kain{i}"]
        pb.rotation_mode = "QUATERNION"
        pb.rotation_quaternion = q
        pb.keyframe_insert("rotation_quaternion", frame=f + 1, group=pb.name)
frig.animation_data.action = None
track = frig.animation_data.nla_tracks.new()
track.name = "Wave"
track.strips.new("Wave", 1, act)
track.mute = False
for pb in frig.pose.bones:
    pb.rotation_quaternion = Quaternion()

# Every piece stays at the origin: the game places each one on the yard by its name.
export(os.path.join(OUT, "kampung.glb"), props + [pole, frig, flag])

# ── leave the user's file as it was ──────────────────────────────────────
# (set KEEP = True before exec() to look at the scene first; run cleanup() after)


def cleanup():
    if bpy.context.window:
        bpy.context.window.scene = previous_scene
    for o in made["objects"]:
        if o.name in bpy.data.objects:
            bpy.data.objects.remove(o, do_unlink=True)
    for kind in ("meshes", "materials", "images", "actions", "armatures"):
        coll = getattr(bpy.data, kind)
        for d in made[kind]:
            try:
                coll.remove(d)
            except (ReferenceError, RuntimeError):
                pass
    bpy.data.scenes.remove(scene)
    print("done")


if not globals().get("KEEP"):
    cleanup()
