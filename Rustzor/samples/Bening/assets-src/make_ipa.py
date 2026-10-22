# Builds the 3D model of Bening's plant, IPA Leuwi Bening (a 400 L/s conventional water
# treatment plant of the fictional Perumda Air Minum Tirta Sangga), and exports it as
#   wwwroot/models/ipa.glb
# Run inside Blender 5.x (tested on 5.2):
#
#   exec(open(r"<repo>/Rustzor/samples/Bening/assets-src/make_ipa.py").read())
#
# Everything is procedural: concrete basins, pumps and motors, pipes, the chemical house,
# the reservoir and its level gauge. The app needs these to have the right sizes and to be
# named, so nothing here is generated or sculpted. What the engine reads from the file:
#   - a node with the custom property "asset" is one piece of equipment (RW-1, F-3, ...);
#     every mesh below it belongs to it, so a click anywhere on it selects it
#   - "spin" = "x" | "y" | "z" marks a part that turns about that glTF axis while the
#     equipment runs (motor fans, couplings, flocculator paddle wheels, the mixer)
#   - "water" = a stage id marks a water surface the engine tints by that stage's turbidity
#   - "level" = an asset id marks a bar the engine scales by that tank's level
# Units are metres, Z up here (Y up in the glTF). Water flows along +X: river and intake on
# the left, distribution pumps on the right.
# The script works in its own scene ("IPA Leuwi Bening") and removes it afterwards, so the
# scene that was open is left untouched.
# Built by Gravicode Studios, led by Kang Fadhil.
import math
import os

import bmesh
import bpy
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else r"C:\experiment\VibeCoding\Vibe_Framework\Rustzor\samples\Bening\assets-src"
OUT = os.path.normpath(os.path.join(HERE, "..", "wwwroot", "models", "ipa.glb"))
SCENE = "IPA Leuwi Bening"

previous_scene = bpy.context.window.scene if bpy.context.window else bpy.context.scene
if previous_scene and previous_scene.name == SCENE:  # a run with KEEP_SCENE left ours open
    previous_scene = next((s for s in bpy.data.scenes if s.name != SCENE), None)
old = bpy.data.scenes.get(SCENE)
if old:
    for o in list(old.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.scenes.remove(old)
scene = bpy.data.scenes.new(SCENE)
scene.unit_settings.system = "METRIC"
if bpy.context.window:
    bpy.context.window.scene = scene

# ── materials ────────────────────────────────────────────────────────────
MATS = {}


def material(name, color, metallic=0.0, roughness=0.7):
    m = bpy.data.materials.new("IPA " + name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    m.diffuse_color = (*color, 1.0)
    MATS[name] = m
    return m


material("concrete", (0.62, 0.62, 0.58), 0.0, 0.9)
material("concrete dark", (0.40, 0.41, 0.39), 0.0, 0.9)
material("grass", (0.30, 0.42, 0.20), 0.0, 1.0)
material("soil", (0.42, 0.33, 0.22), 0.0, 1.0)
material("asphalt", (0.16, 0.17, 0.17), 0.0, 0.9)
material("pump blue", (0.07, 0.25, 0.52), 0.3, 0.45)       # PDAM pumps are painted blue
material("motor grey", (0.36, 0.40, 0.43), 0.4, 0.5)
material("steel", (0.62, 0.63, 0.65), 1.0, 0.35)
material("raw pipe", (0.48, 0.36, 0.17), 0.2, 0.55)        # raw water: ochre
material("clear pipe", (0.12, 0.42, 0.68), 0.2, 0.5)        # treated water: blue
material("chem yellow", (0.86, 0.66, 0.08), 0.1, 0.5)       # PAC (coagulant) lines and tanks
material("chlorine", (0.58, 0.72, 0.16), 0.1, 0.5)          # chlorine: yellow-green
material("rail", (0.93, 0.74, 0.07), 0.2, 0.5)              # safety yellow handrails
material("roof", (0.55, 0.20, 0.13), 0.1, 0.7)              # clay-red metal roof
material("wall", (0.86, 0.84, 0.78), 0.0, 0.85)
material("glass", (0.30, 0.42, 0.48), 0.6, 0.15)
material("water", (0.20, 0.45, 0.55), 0.0, 0.1)             # tinted by the engine
material("sand", (0.75, 0.66, 0.48), 0.0, 1.0)
material("leaf", (0.18, 0.36, 0.17), 0.0, 0.9)
material("trunk", (0.33, 0.24, 0.16), 0.0, 0.9)
material("level", (0.12, 0.55, 0.85), 0.0, 0.3)
material("black", (0.05, 0.05, 0.06), 0.0, 0.6)


# ── mesh helpers ─────────────────────────────────────────────────────────


def link(ob, parent=None, props=None):
    scene.collection.objects.link(ob)
    if parent:
        ob.parent = parent
    for k, v in (props or {}).items():
        ob[k] = v
    return ob


def group(name, location=(0, 0, 0), parent=None, props=None):
    e = bpy.data.objects.new(name, None)
    e.location = Vector(location)
    return link(e, parent, props)


def mesh_object(name, bm, mat, parent=None, location=(0, 0, 0), props=None, smooth=False):
    me = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    me.materials.append(MATS[mat])
    if smooth:
        try:
            me.shade_smooth()
            me.set_sharp_from_angle(angle=math.radians(40))
        except AttributeError:
            for p in me.polygons:
                p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    ob.location = Vector(location)
    return link(ob, parent, props)


def box(name, center, size, mat, parent=None, props=None, origin=None):
    """An axis-aligned box. `center` is in the parent's space; `origin`, when given, is where the
    object's own origin sits (the mesh is offset so the box still lands at `center`)."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
    o = Vector(origin) if origin else Vector(center)
    bmesh.ops.translate(bm, vec=Vector(center) - o, verts=bm.verts)
    return mesh_object(name, bm, mat, parent, o, props)


def cyl(name, center, r, h, mat, axis="Z", segs=20, parent=None, props=None, r2=None, smooth=True):
    """A capped cylinder (a cone when r2 differs) centred on `center`, along `axis`."""
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=segs, radius1=r, radius2=r if r2 is None else r2, depth=h)
    if axis == "X":
        bmesh.ops.rotate(bm, verts=bm.verts, matrix=Matrix.Rotation(math.radians(90), 3, "Y"))
    elif axis == "Y":
        bmesh.ops.rotate(bm, verts=bm.verts, matrix=Matrix.Rotation(math.radians(90), 3, "X"))
    return mesh_object(name, bm, mat, parent, center, props, smooth)


def basin(name, x0, x1, y0, y1, depth, wall, parent, water_stage=None, water_z=None, mat="concrete"):
    """An open concrete basin with its rim at z = depth (it stands on the ground)."""
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    lx, ly = x1 - x0, y1 - y0
    box(name + " floor", (cx, cy, 0.15), (lx, ly, 0.3), "concrete dark", parent)
    box(name + " wall S", (cx, y0 + wall / 2, depth / 2), (lx, wall, depth), mat, parent)
    box(name + " wall N", (cx, y1 - wall / 2, depth / 2), (lx, wall, depth), mat, parent)
    box(name + " wall W", (x0 + wall / 2, cy, depth / 2), (wall, ly - 2 * wall, depth), mat, parent)
    box(name + " wall E", (x1 - wall / 2, cy, depth / 2), (wall, ly - 2 * wall, depth), mat, parent)
    if water_stage:
        z = water_z if water_z is not None else depth - 0.35
        box(name + " water", (cx, cy, z - 0.05), (lx - 2 * wall, ly - 2 * wall, 0.1), "water", parent, {"water": water_stage})


def rail(name, a, b, parent, h=1.05):
    """A handrail from a to b (both at deck height): two rails and posts every 2 m."""
    a, b = Vector(a), Vector(b)
    d = b - a
    n = max(1, int(d.length / 2))
    for i in range(n + 1):
        p = a + d * (i / n)
        box(f"{name} post{i}", (p.x, p.y, p.z + h / 2), (0.06, 0.06, h), "rail", parent)
    axis = "X" if abs(d.x) > abs(d.y) else "Y"
    for k, z in enumerate((h, h * 0.5)):
        m = (a + b) / 2
        cyl(f"{name} bar{k}", (m.x, m.y, m.z + z), 0.03, d.length, "rail", axis, 6, parent, smooth=False)


def pipe(name, pts, r, mat, parent):
    """A run of straight pipe segments through `pts` (axis-aligned), with a ball at each bend."""
    for i in range(len(pts) - 1):
        a, b = Vector(pts[i]), Vector(pts[i + 1])
        d = b - a
        axis = "X" if abs(d.x) > 1e-6 else ("Y" if abs(d.y) > 1e-6 else "Z")
        cyl(f"{name} {i}", (a + b) / 2, r, d.length, mat, axis, 14, parent)
    for i in range(1, len(pts) - 1):
        bm = bmesh.new()
        bmesh.ops.create_uvsphere(bm, u_segments=12, v_segments=8, radius=r * 1.08)
        mesh_object(f"{name} bend{i}", bm, mat, parent, pts[i], smooth=True)


def fan(name, center, r, blades, mat, parent, axis="Z", spin_axis="y", props=None):
    """A turning part: a hub and flat blades in a ring; its origin is the centre, so the engine
    can spin it in place."""
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=16, radius1=r * 0.28, radius2=r * 0.28, depth=r * 0.3)
    for i in range(blades):
        a = 2 * math.pi * i / blades
        res = bmesh.ops.create_cube(bm, size=1.0)
        vs = res["verts"]
        bmesh.ops.scale(bm, vec=Vector((r * 0.72, r * 0.2, r * 0.06)), verts=vs)
        bmesh.ops.translate(bm, vec=Vector((r * 0.62, 0, 0)), verts=vs)
        bmesh.ops.rotate(bm, verts=vs, matrix=Matrix.Rotation(math.radians(18), 3, "X"))
        bmesh.ops.rotate(bm, verts=vs, matrix=Matrix.Rotation(a, 3, "Z"))
    if axis == "X":
        bmesh.ops.rotate(bm, verts=bm.verts, matrix=Matrix.Rotation(math.radians(90), 3, "Y"))
    elif axis == "Y":
        bmesh.ops.rotate(bm, verts=bm.verts, matrix=Matrix.Rotation(math.radians(90), 3, "X"))
    p = {"spin": spin_axis}
    p.update(props or {})
    return mesh_object(name, bm, mat, parent, center, p)


def paddle_wheel(name, center, r, width, parent):
    """A flocculator paddle wheel on an axle across the basin (Blender Y = glTF -Z)."""
    bm = bmesh.new()
    res = bmesh.ops.create_cone(bm, cap_ends=True, segments=10, radius1=0.09, radius2=0.09, depth=width + 0.6)
    bmesh.ops.rotate(bm, verts=res["verts"], matrix=Matrix.Rotation(math.radians(90), 3, "X"))
    for i in range(4):
        a = math.pi * i / 2
        for side in (-1, 1):
            res = bmesh.ops.create_cube(bm, size=1.0)
            vs = res["verts"]
            bmesh.ops.scale(bm, vec=Vector((0.08, width * 0.42, 0.42)), verts=vs)
            bmesh.ops.translate(bm, vec=Vector((0, side * width * 0.24, r - 0.25)), verts=vs)
            bmesh.ops.rotate(bm, verts=vs, matrix=Matrix.Rotation(a, 3, "Y"))
        res = bmesh.ops.create_cube(bm, size=1.0)
        vs = res["verts"]
        bmesh.ops.scale(bm, vec=Vector((0.06, 0.06, r)), verts=vs)
        bmesh.ops.translate(bm, vec=Vector((0, 0, r / 2)), verts=vs)
        bmesh.ops.rotate(bm, verts=vs, matrix=Matrix.Rotation(a, 3, "Y"))
    return mesh_object(name, bm, "steel", parent, center, {"spin": "z"})


# ── the site ─────────────────────────────────────────────────────────────
site = group("Site", props={"asset": "SITE"})
box("Ground", (14, 18.6, -0.15), (240, 123, 0.3), "grass", site)      # north of the river
box("Ground far bank", (14, -67.5, -0.15), (240, 5, 0.3), "grass", site)
# each surface a few centimetres above the one under it, so none of them fight for the same depth
box("Yard", (18, 0, 0.0), (176, 76, 0.06), "concrete", site)
box("Road", (18, 44, 0.02), (196, 8, 0.06), "asphalt", site)
box("Access road", (-34, 30, 0.03), (8, 22, 0.06), "asphalt", site)
# river Ci Sangga along the south edge, with its banks
box("River bed", (14, -54, -1.6), (240, 20, 0.2), "soil", site)
box("River bank S", (14, -64.5, -0.8), (240, 1.5, 1.6), "soil", site)
box("River bank N", (14, -43.5, -0.8), (240, 1.5, 1.6), "soil", site)
river = group("River", props={"asset": "RIVER"})
box("River water", (14, -54, -0.9), (240, 19, 0.1), "water", river, {"water": "raw"})

# fence around the yard
for (a, b) in [((-70, -39), (106, -39)), ((106, -39), (106, 39)), ((106, 39), (-70, 39)), ((-70, 39), (-70, -39))]:
    a, b = Vector((*a, 0)), Vector((*b, 0))
    d = b - a
    n = int(d.length / 4)
    for i in range(n + 1):
        p = a + d * (i / n)
        box(f"Fence post {a.x:.0f},{a.y:.0f} {i}", (p.x, p.y, 1.0), (0.08, 0.08, 2.0), "motor grey", site)
    m = (a + b) / 2
    for z in (0.5, 1.9):
        box(f"Fence rail {a.x:.0f},{a.y:.0f} {z}", (m.x, m.y, z), (abs(d.x) + 0.05 if abs(d.x) > 0 else 0.05, abs(d.y) + 0.05 if abs(d.y) > 0 else 0.05, 0.05), "motor grey", site)

# trees: a few low-poly canopies along the fence and the river
TREES = [(-66, 34), (-56, 36), (-20, 35), (-8, 33), (8, 35), (52, 34), (66, 36), (82, 33), (100, 35),
         (-66, -35), (-36, -36), (-20, -35), (30, -36), (60, -35), (92, -36), (102, 20), (102, -20), (-66, 10)]
for i, (x, y) in enumerate(TREES):
    s = 0.85 + 0.3 * ((i * 37) % 10) / 10
    cyl(f"Tree trunk {i}", (x, y, 1.6 * s), 0.28 * s, 3.2 * s, "trunk", "Z", 7, site, smooth=False)
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=2.6 * s)
    bmesh.ops.scale(bm, vec=Vector((1.0, 1.0, 0.8)), verts=bm.verts)
    mesh_object(f"Tree canopy {i}", bm, "leaf", site, (x, y, 4.4 * s))

# ── intake: raw water pumps on a deck over the river ─────────────────────
intake = group("Intake", props={"asset": "INTAKE"})
box("Intake deck", (-53, -40, 0.3), (24, 12, 0.6), "concrete", intake)
for i, x in enumerate((-63, -43)):
    box(f"Intake pier {i}", (x, -44, -0.6), (1.4, 1.4, 2.0), "concrete dark", intake)
box("Intake screen", (-53, -46.2, -0.4), (20, 0.25, 1.6), "steel", intake)
rail("Intake rail S", (-65, -45.8, 0.6), (-41, -45.8, 0.6), intake)
rail("Intake rail W", (-65, -45.8, 0.6), (-65, -34.2, 0.6), intake)
rail("Intake rail E", (-41, -45.8, 0.6), (-41, -34.2, 0.6), intake)
# the canopy frame (no sheet, so the pumps stay in view)
for x in (-64, -53, -42):
    for y in (-45, -35):
        box(f"Canopy column {x},{y}", (x, y, 3.6), (0.3, 0.3, 6.0), "concrete", intake)
box("Canopy beam S", (-53, -45, 6.6), (22.3, 0.3, 0.5), "concrete", intake)
box("Canopy beam N", (-53, -35, 6.6), (22.3, 0.3, 0.5), "concrete", intake)
box("Crane rail", (-53, -40, 6.4), (22, 0.25, 0.3), "rail", intake)

for k, x in enumerate((-60, -53, -46)):
    tag = f"RW-{k + 1}"
    g = group(tag, (x, -40, 0.6), props={"asset": tag})
    box(f"{tag} plinth", (0, 0, 0.15), (1.8, 1.8, 0.3), "concrete dark", g)
    cyl(f"{tag} column", (0, 0, -1.6), 0.35, 3.0, "pump blue", "Z", 16, g)
    cyl(f"{tag} discharge head", (0, 0, 0.75), 0.55, 0.9, "pump blue", "Z", 20, g)
    cyl(f"{tag} discharge", (0, 0.75, 0.75), 0.22, 0.9, "pump blue", "Y", 16, g)
    cyl(f"{tag} lantern", (0, 0, 1.45), 0.42, 0.5, "pump blue", "Z", 16, g)
    cyl(f"{tag} motor", (0, 0, 2.45), 0.6, 1.5, "motor grey", "Z", 24, g)
    for f in range(12):
        a = 2 * math.pi * f / 12
        box(f"{tag} fin{f}", (math.cos(a) * 0.62, math.sin(a) * 0.62, 2.4), (0.08, 0.08, 1.3), "motor grey", g)
    cyl(f"{tag} fan cowl", (0, 0, 3.32), 0.62, 0.24, "motor grey", "Z", 24, g, r2=0.5)
    fan(f"{tag} fan", (0, 0, 3.55), 0.46, 6, "black", g, "Z", "y")
    box(f"{tag} terminal box", (0.68, 0, 2.6), (0.3, 0.42, 0.5), "motor grey", g)
    cyl(f"{tag} valve", (0, 1.55, 0.75), 0.3, 0.3, "pump blue", "Y", 16, g)
    cyl(f"{tag} valve wheel", (0, 1.55, 1.25), 0.25, 0.05, "rail", "Z", 16, g)

# raw water main: from the three pumps along the deck, then north to the mixer
for k, x in enumerate((-60, -53, -46)):
    pipe(f"Raw riser {k}", [(x, -37.9, 1.35), (x, -36.5, 1.35)], 0.22, "raw pipe", intake)
pipe("Raw main", [(-62, -36, 1.35), (-38, -36, 1.35), (-38, -6, 1.35), (-33, -6, 1.35)], 0.4, "raw pipe", intake)

# ── coagulation: the flash mixer ─────────────────────────────────────────
fm = group("FM-1", (-30, -6, 0), props={"asset": "FM-1"})
basin("FM-1 tank", -2.5, 2.5, -2.5, 2.5, 3.6, 0.3, fm, "mix")
box("FM-1 bridge", (0, 0, 3.75), (5.4, 1.0, 0.25), "concrete", fm)
cyl("FM-1 gearbox", (0, 0, 4.2), 0.42, 0.7, "pump blue", "Z", 16, fm)
cyl("FM-1 motor", (0, 0, 4.95), 0.32, 0.8, "motor grey", "Z", 16, fm)
fan("FM-1 mixer", (0, 0, 3.0), 1.2, 3, "steel", fm, "Z", "y")
rail("FM-1 rail", (-2.6, 0.5, 3.87), (2.6, 0.5, 3.87), fm)

# ── flocculation, sedimentation, filtration: two streams side by side ────
for s, y0 in ((1, -16), (2, 4)):
    y1 = y0 + 12
    cy = (y0 + y1) / 2
    ft = f"FL-{s}"
    fl = group(ft, (0, 0, 0), props={"asset": ft})
    basin(ft, -25, -9, y0, y1, 3.8, 0.35, fl, "floc")
    for w, x in enumerate((-21, -17, -13)):
        cyl(f"{ft} bearing {w}a", (x, y0 + 0.2, 3.6), 0.18, 0.4, "pump blue", "Y", 10, fl)
        cyl(f"{ft} bearing {w}b", (x, y1 - 0.2, 3.6), 0.18, 0.4, "pump blue", "Y", 10, fl)
        paddle_wheel(f"{ft} paddles {w}", (x, cy, 3.25), 1.9, y1 - y0 - 1.2, fl)
    cyl(f"{ft} drive", (-25.6, cy, 3.4), 0.35, 0.9, "motor grey", "X", 14, fl)
    box(f"{ft} walkway", (-17, y0 - 0.6, 3.9), (16, 1.0, 0.2), "concrete", fl)
    rail(f"{ft} rail", (-25, y0 - 1.05, 4.0), (-9, y0 - 1.05, 4.0), fl)

    sd = f"SD-{s}"
    g = group(sd, props={"asset": sd})
    basin(sd, -8, 22, y0, y1, 4.2, 0.35, g, "settle")
    for row in range(6):
        for col in range(2):
            box(f"{sd} tube settler {row}{col}", (2 + row * 3.1, cy - 2.7 + col * 5.4, 3.6), (2.8, 4.8, 0.5), "wall", g)
    for k in range(5):
        box(f"{sd} launder {k}", (3.5 + k * 3.8, cy, 4.05), (0.5, 11.2, 0.3), "concrete", g)
    box(f"{sd} effluent channel", (21.2, cy, 4.0), (1.0, 11.3, 0.5), "concrete", g)
    box(f"{sd} walkway", (7, y0 - 0.6, 4.3), (30, 1.0, 0.2), "concrete", g)
    rail(f"{sd} rail", (-8, y0 - 1.05, 4.4), (22, y0 - 1.05, 4.4), g)

    # two filters per stream, with the sand bed showing under the water
    for f in range(2):
        ft2 = f"F-{(s - 1) * 2 + f + 1}"
        fy0 = y0 + f * 6
        g = group(ft2, props={"asset": ft2})
        basin(ft2, 26, 38, fy0, fy0 + 6, 4.0, 0.3, g, ft2, 3.4)
        box(f"{ft2} sand", (32, fy0 + 3, 1.2), (11.4, 5.4, 1.6), "sand", g)
        box(f"{ft2} gullet", (32, fy0 + 3, 2.6), (11.4, 0.8, 1.2), "concrete dark", g)
        cyl(f"{ft2} valve", (39.2, fy0 + 3, 1.0), 0.35, 0.5, "pump blue", "X", 16, g)
        cyl(f"{ft2} actuator", (39.2, fy0 + 3, 1.7), 0.18, 0.9, "motor grey", "Z", 12, g)

    pipe(f"Settled water {s}", [(22.5, cy, 3.0), (25.5, cy, 3.0)], 0.35, "raw pipe", site)

# mixer to the flocculators
pipe("Coagulated water", [(-27.4, -6, 2.8), (-26.5, -6, 2.8), (-26.5, -10, 2.8), (-25.4, -10, 2.8)], 0.35, "raw pipe", site)
pipe("Coagulated water B", [(-26.5, -6, 2.8), (-26.5, 10, 2.8), (-25.4, 10, 2.8)], 0.35, "raw pipe", site)
# filter gallery and filtered water to the reservoir
box("Filter gallery", (32, -6, 4.1), (12.4, 1.0, 0.2), "concrete", site)
box("Filter gallery B", (32, 2.5, 4.1), (12.4, 1.4, 0.2), "concrete", site)
pipe("Filtered water", [(39.5, -10, 0.8), (42, -10, 0.8), (42, 10, 0.8)], 0.4, "clear pipe", site)
pipe("Filtered main", [(42, 0, 0.8), (47.5, 0, 0.8)], 0.45, "clear pipe", site)

# ── chemical house: PAC tanks and the chlorinator ────────────────────────
ch = group("Chemical house", props={"asset": "SITE"})
box("Chem house walls", (34, 24, 2.0), (16, 8, 4.0), "wall", ch)
bm = bmesh.new()
bmesh.ops.create_cube(bm, size=1.0)
bmesh.ops.scale(bm, vec=Vector((17, 9, 0.25)), verts=bm.verts)
mesh_object("Chem house roof", bm, "roof", ch, (34, 24, 4.15))
for i in range(4):
    box(f"Chem house window {i}", (28 + i * 3.6, 19.95, 2.6), (1.6, 0.1, 1.0), "glass", ch)
box("Chem house door", (40.5, 19.95, 1.1), (1.8, 0.1, 2.2), "motor grey", ch)

pac = group("PAC", props={"asset": "PAC"})
for i, x in enumerate((15, 19)):
    cyl(f"PAC tank {i}", (x, 24, 2.0), 1.4, 4.0, "chem yellow", "Z", 24, pac)
    cyl(f"PAC tank {i} lid", (x, 24, 4.1), 1.45, 0.2, "chem yellow", "Z", 24, pac, r2=0.6)
    box(f"PAC tank {i} gauge", (x, 22.55, 2.0), (0.12, 0.08, 3.4), "glass", pac)
box("PAC bund", (17, 24, 0.2), (9, 4.4, 0.4), "concrete dark", pac)
box("PAC dosing skid", (23, 22, 0.6), (2.2, 1.2, 1.2), "pump blue", pac)
fan("PAC dosing pump", (23, 21.3, 0.9), 0.22, 4, "steel", pac, "Y", "z")
pipe("PAC line", [(23, 21, 1.2), (23, 18, 1.2), (-30, 18, 1.2), (-30, -3, 1.2), (-30, -3, 4.0)], 0.06, "chem yellow", pac)

cl = group("CL-1", props={"asset": "CL-1"})
for i in range(3):
    cyl(f"CL-1 drum {i}", (29.5 + i * 2.4, 25.5, 0.6), 0.4, 2.0, "chlorine", "Y", 16, cl)
box("CL-1 chlorinator", (38, 26.5, 1.0), (1.4, 0.6, 2.0), "chlorine", cl)
fan("CL-1 booster", (38, 25.7, 0.8), 0.2, 4, "steel", cl, "Y", "z")
pipe("Chlorine line", [(38, 22, 1.0), (38, 18, 1.0), (45, 18, 1.0), (45, 1, 1.0)], 0.06, "chlorine", cl)

# ── reservoir: a covered ground tank with a level gauge on its face ──────
res = group("RES-1", props={"asset": "RES-1"})
box("RES-1 walls", (63, 0, 2.5), (30, 28, 5.0), "concrete", res)
box("RES-1 roof", (63, 0, 5.1), (30.6, 28.6, 0.2), "concrete dark", res)
for i, (x, y) in enumerate([(54, -8), (54, 8), (63, -8), (63, 8), (72, -8), (72, 8)]):
    cyl(f"RES-1 vent {i}", (x, y, 5.6), 0.15, 0.8, "steel", "Z", 8, res)
    cyl(f"RES-1 vent cap {i}", (x, y, 6.05), 0.45, 0.25, "steel", "Z", 12, res, r2=0.1)
box("RES-1 hatch", (70, 10, 5.3), (1.6, 1.6, 0.25), "steel", res)
rail("RES-1 rail", (48.2, -14.2, 5.2), (77.8, -14.2, 5.2), res)
# the gauge: a glass column with a bar inside that the engine scales by the level
box("RES-1 gauge frame", (56, -14.75, 2.6), (1.6, 0.4, 5.2), "steel", res)
box("RES-1 gauge glass", (56, -14.98, 2.6), (1.2, 0.06, 4.8), "glass", res)
box("RES-1 level", (56, -15.05, 2.6), (1.0, 0.08, 4.8), "level", res, {"level": "RES-1"}, origin=(56, -15.05, 0.2))
for i in range(6):
    box(f"RES-1 gauge tick {i}", (55.1, -15.0, 0.2 + i * 0.96), (0.3, 0.05, 0.05), "black", res)

# ── distribution pumps ───────────────────────────────────────────────────
ds = group("Distribution house", props={"asset": "SITE"})
box("DS deck", (88, 0, 0.25), (16, 22, 0.5), "concrete", ds)
for x in (81, 95):
    for y in (-10.5, 0, 10.5):
        box(f"DS column {x},{y}", (x, y, 3.25), (0.35, 0.35, 5.5), "concrete", ds)
for y in (-10.5, 0, 10.5):
    box(f"DS beam {y}", (88, y, 6.1), (14.6, 0.3, 0.5), "concrete", ds)
for x in (81, 95):
    box(f"DS girder {x}", (x, 0, 6.1), (0.3, 21.3, 0.5), "concrete", ds)
box("DS crane rail", (88, 0, 5.8), (0.25, 21, 0.3), "rail", ds)
pipe("Suction header", [(78.5, 0, 0.9), (82, 0, 0.9)], 0.45, "clear pipe", ds)
pipe("Suction manifold", [(82, -7, 0.9), (82, 7, 0.9)], 0.4, "clear pipe", ds)
pipe("Delivery manifold", [(94, -7, 1.5), (94, 7, 1.5)], 0.38, "clear pipe", ds)
pipe("City main", [(94, 7, 1.5), (94, 9, 1.5), (110, 9, 1.5)], 0.42, "clear pipe", ds)

for k, y in enumerate((-6, 0, 6)):
    tag = f"DS-{k + 1}"
    g = group(tag, (88, y, 0.5), props={"asset": tag})
    box(f"{tag} base", (0, 0, 0.15), (5.4, 1.4, 0.3), "concrete dark", g)
    cyl(f"{tag} suction", (-4.2, 0, 0.4), 0.26, 3.6, "pump blue", "X", 14, g)
    cyl(f"{tag} volute", (-1.6, 0, 0.9), 0.62, 0.55, "pump blue", "X", 24, g)
    cyl(f"{tag} discharge", (-1.6, 0, 1.4), 0.2, 1.0, "pump blue", "Z", 14, g)
    pipe(f"{tag} delivery", [(-1.6, 0, 1.9), (6.0, 0, 1.9), (6.0, 0, 1.0)], 0.2, "clear pipe", g)
    cyl(f"{tag} bearing frame", (-0.7, 0, 0.9), 0.28, 0.9, "pump blue", "X", 14, g)
    cyl(f"{tag} coupling guard", (0.15, 0, 0.9), 0.32, 0.5, "rail", "X", 14, g)
    fan(f"{tag} coupling", (0.15, 0, 0.9), 0.3, 4, "steel", g, "X", "x")
    cyl(f"{tag} motor", (1.6, 0, 0.9), 0.55, 2.0, "motor grey", "X", 24, g)
    for f in range(10):
        a = 2 * math.pi * f / 10
        box(f"{tag} fin{f}", (1.6, math.cos(a) * 0.57, 0.9 + math.sin(a) * 0.57), (1.8, 0.07, 0.07), "motor grey", g)
    cyl(f"{tag} fan cowl", (2.8, 0, 0.9), 0.56, 0.35, "motor grey", "X", 24, g, r2=0.4)
    fan(f"{tag} fan", (3.02, 0, 0.9), 0.36, 6, "black", g, "X", "x")
    box(f"{tag} terminal box", (1.6, 0.6, 1.4), (0.5, 0.3, 0.4), "motor grey", g)

# ── the operations building ──────────────────────────────────────────────
ops_b = group("Operations building", props={"asset": "SITE"})
box("Ops walls", (-48, 24, 3.2), (18, 10, 6.4), "wall", ops_b)
bm = bmesh.new()
bmesh.ops.create_cube(bm, size=1.0)
bmesh.ops.scale(bm, vec=Vector((19, 11, 0.3)), verts=bm.verts)
mesh_object("Ops roof", bm, "roof", ops_b, (-48, 24, 6.55))
for floor in (0, 1):
    for i in range(5):
        box(f"Ops window {floor}{i}", (-55 + i * 3.4, 18.95, 1.6 + floor * 3.0), (2.2, 0.1, 1.3), "glass", ops_b)
box("Ops door", (-48, 18.95, 1.1), (2.0, 0.1, 2.2), "motor grey", ops_b)
box("Ops sign", (-48, 18.9, 5.6), (8, 0.1, 0.9), "clear pipe", ops_b)
# a small genset by the building
box("Genset", (-34, 24, 1.1), (4.0, 1.8, 2.2), "chlorine", ops_b)

# ── merge: one mesh per piece of equipment and material ─────────────────
# Hundreds of small boxes would be hundreds of draw calls. The parts the engine moves or
# tints (spin, water, level) stay separate; everything else under an asset is merged into
# one mesh per material, in the asset's own space.
def merge():
    keep = ("spin", "water", "level")
    scene.view_layers[0].update()  # world matrices of the objects made above
    for g in [o for o in scene.objects if o.type == "EMPTY" and "asset" in o.keys()]:
        inv = g.matrix_world.inverted()
        by_mat = {}
        for o in [c for c in g.children_recursive if c.type == "MESH" and not any(k in c.keys() for k in keep)]:
            by_mat.setdefault(o.data.materials[0].name, []).append(o)
        for mat_name, obs in by_mat.items():
            bm = bmesh.new()
            for o in obs:
                m = inv @ o.matrix_world
                vs = [bm.verts.new(m @ v.co) for v in o.data.vertices]
                for p in o.data.polygons:
                    f = bm.faces.new([vs[i] for i in p.vertices])
                    f.smooth = p.use_smooth
            for o in obs:
                m = o.data
                bpy.data.objects.remove(o, do_unlink=True)
                if m.users == 0:
                    bpy.data.meshes.remove(m)
            name = f"{g.name} · {mat_name[4:]}"
            me = bpy.data.meshes.new(name)
            bm.to_mesh(me)
            bm.free()
            try:
                me.set_sharp_from_angle(angle=math.radians(40))
            except AttributeError:
                pass
            me.materials.append(bpy.data.materials[mat_name])
            ob = bpy.data.objects.new(name, me)
            scene.collection.objects.link(ob)
            ob.parent = g
            ob.matrix_parent_inverse = Matrix.Identity(4)


merge()
for ob in scene.objects:
    if ob.type == "MESH":
        ob.data.name = ob.name

# ── export ───────────────────────────────────────────────────────────────
os.makedirs(os.path.dirname(OUT), exist_ok=True)
props = {p.identifier for p in bpy.ops.export_scene.gltf.get_rna_type().properties}
want = dict(filepath=OUT, export_format="GLB", export_extras=True, export_apply=True, export_yup=True,
            export_cameras=False, export_lights=False, export_materials="EXPORT", export_texcoords=False,
            export_normals=True, export_animations=False, use_active_scene=True, export_draco_mesh_compression_enable=False)
bpy.ops.export_scene.gltf(**{k: v for k, v in want.items() if k in props})
tris = sum(len(o.data.polygons) for o in scene.objects if o.type == "MESH")
print("exported", OUT, os.path.getsize(OUT), "bytes,", len([o for o in scene.objects if o.type == "MESH"]), "meshes,", tris, "faces")

if not globals().get("KEEP_SCENE"):
    # leave the open file as it was: back to the previous scene, and nothing of ours left behind
    if bpy.context.window and previous_scene:
        bpy.context.window.scene = previous_scene
    meshes = {o.data for o in scene.objects if o.type == "MESH"}
    for o in list(scene.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for m in meshes:
        if m.users == 0:
            bpy.data.meshes.remove(m)
    bpy.data.scenes.remove(scene)
    for m in MATS.values():
        if m.users == 0:
            bpy.data.materials.remove(m)
