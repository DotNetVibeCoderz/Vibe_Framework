# Builds the RW-63 two-stage gear reducer that Tilik opens, and exports it as
# wwwroot/models/rw63.glb. Run inside Blender 5.x (tested on 5.2):
#
#   exec(open(r"<repo>/Rustzor/samples/Tilik/assets-src/make_rw63.py").read())
#
# or headless:  blender -b -P make_rw63.py
#
# Everything is scripted with bmesh and exact booleans, in millimetres, then
# scaled to metres for glTF. It works in its own scene ("RW-63") and leaves
# whatever scene was open untouched. Each part carries glTF extras: part
# number, material, density, make/buy, and the stage it belongs to.
# Built by Gravicode Studios, led by Kang Fadhil.
import math
import os
import bmesh
import bpy
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else r"C:\experiment\VibeCoding\Vibe_Framework\Rustzor\samples\Tilik\assets-src"
OUT = os.path.normpath(os.path.join(HERE, "..", "wwwroot", "models", "rw63.glb"))
MM = 0.001

previous_scene = bpy.context.window.scene if bpy.context.window else bpy.context.scene
old = bpy.data.scenes.get("RW-63")
if old:
    for o in list(old.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.scenes.remove(old)
scene = bpy.data.scenes.new("RW-63")
scene.unit_settings.system = "METRIC"
if bpy.context.window:
    bpy.context.window.scene = scene

# ── materials ────────────────────────────────────────────────────────────
MATS = {}


def material(name, color, metallic, roughness, alpha=1.0):
    m = bpy.data.materials.get("RW63 " + name) or bpy.data.materials.new("RW63 " + name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    if alpha < 1:
        bsdf.inputs["Alpha"].default_value = alpha
        try:
            m.surface_render_method = "BLENDED"
        except (AttributeError, TypeError):
            pass
    m.diffuse_color = (*color, alpha)
    MATS[name] = m
    return m


material("Cast iron, painted", (0.20, 0.30, 0.27), 0.0, 0.55)       # machinery green
material("Steel S45C", (0.62, 0.63, 0.65), 1.0, 0.32)
material("Steel SCM440, hardened", (0.42, 0.43, 0.46), 1.0, 0.28)
material("Bearing steel SUJ2", (0.80, 0.81, 0.83), 1.0, 0.15)
material("NBR rubber", (0.04, 0.04, 0.045), 0.0, 0.7)
material("Brass", (0.78, 0.60, 0.26), 1.0, 0.3)
material("Zinc-plated steel", (0.72, 0.74, 0.70), 1.0, 0.35)
material("Glass", (0.70, 0.85, 0.95), 0.0, 0.05, alpha=0.45)

DENSITY = {
    "Cast iron, painted": 7200, "Steel S45C": 7850, "Steel SCM440, hardened": 7850,
    "Bearing steel SUJ2": 7830, "NBR rubber": 1200, "Brass": 8500,
    "Zinc-plated steel": 7850, "Glass": 2500,
}


def solver():
    items = [i.identifier for i in bpy.types.BooleanModifier.bl_rna.properties["solver"].enum_items]
    return "EXACT" if "EXACT" in items else items[0]


SOLVER = solver()

# ── mesh helpers (millimetres) ───────────────────────────────────────────


def to_object(name, bm, mat, parent=None, location=(0, 0, 0), smooth_angle=32):
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    ob.location = Vector(location)
    me.materials.append(MATS[mat])
    ob["material"] = mat
    if parent:
        ob.parent = parent
    finish(ob, smooth_angle)
    return ob


def finish(ob, smooth_angle=32):
    me = ob.data
    try:
        me.shade_smooth()
        me.set_sharp_from_angle(angle=math.radians(smooth_angle))
    except AttributeError:
        for p in me.polygons:
            p.use_smooth = True


def revolve(profile, segments=48):
    """A solid of revolution around the Y axis. `profile` is a closed loop of
    (r, y) points; r == 0 makes a pole."""
    bm = bmesh.new()
    rings = []
    for r, y in profile:
        if r < 1e-9:
            rings.append([bm.verts.new((0, y, 0))])
        else:
            rings.append([bm.verts.new((r * math.cos(2 * math.pi * i / segments), y, r * math.sin(2 * math.pi * i / segments))) for i in range(segments)])
    n = len(rings)
    for k in range(n):
        a, b = rings[k], rings[(k + 1) % n]
        if len(a) == 1 and len(b) == 1:
            continue
        for i in range(segments):
            j = (i + 1) % segments
            if len(a) == 1:
                bm.faces.new((a[0], b[j], b[i]))
            elif len(b) == 1:
                bm.faces.new((a[i], a[j], b[0]))
            else:
                bm.faces.new((a[i], a[j], b[j], b[i]))
    return bm


def box(x0, x1, y0, y1, z0, z1):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(x1 - x0, y1 - y0, z1 - z0), verts=bm.verts)
    bmesh.ops.translate(bm, vec=((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), verts=bm.verts)
    return bm


def cylinder(r, y0, y1, x=0.0, z=0.0, segments=48, axis="Y"):
    bm = revolve([(0, y0), (r, y0), (r, y1), (0, y1)], segments)
    if axis == "Z":
        bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=Matrix.Rotation(math.radians(90), 3, "X"), verts=bm.verts)
    bmesh.ops.translate(bm, vec=(x, 0, z), verts=bm.verts)
    return bm


def gear(teeth, module, width, bore, phase=0.0, pressure=20.0):
    """A spur gear around the Y axis, faces at y = 0 and y = width."""
    rp = module * teeth / 2
    ra, rf = rp + module, rp - 1.25 * module
    rb = rp * math.cos(math.radians(pressure))
    inv = lambda a: math.tan(a) - a
    psi_b = math.pi / (2 * teeth) + inv(math.radians(pressure))
    r_start = max(rb, rf)

    def half(r):
        return psi_b - inv(math.acos(min(1.0, rb / r))) if r > rb else psi_b

    flank = [r_start + (ra - r_start) * t / 4 for t in range(5)]
    pts = []
    for k in range(teeth):
        c = phase + 2 * math.pi * k / teeth
        pts.append((rf, c - psi_b))
        for r in flank:
            pts.append((r, c - half(r)))
        for r in reversed(flank):
            pts.append((r, c + half(r)))
        pts.append((rf, c + psi_b))
        nxt = phase + 2 * math.pi * (k + 1) / teeth - psi_b
        here = c + psi_b
        for t in (1, 2):
            pts.append((rf, here + (nxt - here) * t / 3))
    bm = bmesh.new()
    vs = [bm.verts.new((r * math.cos(a), 0, r * math.sin(a))) for r, a in pts]
    face = bm.faces.new(vs)
    ext = bmesh.ops.extrude_face_region(bm, geom=[face])
    moved = [e for e in ext["geom"] if isinstance(e, bmesh.types.BMVert)]
    bmesh.ops.translate(bm, vec=(0, width, 0), verts=moved)
    return bm, bore


def boolean(target, cutters, op):
    for c in cutters:
        mod = target.modifiers.new("bool", "BOOLEAN")
        mod.operation = op
        mod.solver = SOLVER
        mod.object = c
    dg = bpy.context.evaluated_depsgraph_get()
    ev = target.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ev)
    old = target.data
    target.modifiers.clear()
    target.data = me
    bpy.data.meshes.remove(old)
    for c in cutters:
        me_c = c.data
        bpy.data.objects.remove(c, do_unlink=True)
        bpy.data.meshes.remove(me_c)


def bevel(target, width, segments=2, angle=50):
    """Rounds the edges of a casting; skipped if the bevel cannot be applied cleanly."""
    mod = target.modifiers.new("bevel", "BEVEL")
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    mod.angle_limit = math.radians(angle)
    mod.harden_normals = False
    try:
        dg = bpy.context.evaluated_depsgraph_get()
        me = bpy.data.meshes.new_from_object(target.evaluated_get(dg))
        old = target.data
        target.modifiers.clear()
        target.data = me
        bpy.data.meshes.remove(old)
    except RuntimeError:
        target.modifiers.clear()


def cutter(bm):
    me = bpy.data.meshes.new("cut")
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new("cut", me)
    scene.collection.objects.link(ob)
    ob.hide_render = True
    return ob


def empty(name, parent=None, **extras):
    e = bpy.data.objects.new(name, None)
    scene.collection.objects.link(e)
    e.empty_display_size = 0.02
    if parent:
        e.parent = parent
    for k, v in extras.items():
        e[k] = v
    return e


PARTS = []


def part(ob, number, desc, make="make", stage=""):
    ob["part_no"] = number
    ob["description"] = desc
    ob["make_buy"] = make
    ob["density"] = DENSITY[ob["material"]]
    if stage:
        ob["stage"] = stage
    PARTS.append(ob)
    return ob


# ── layout (mm). Shafts run along Y; the housing splits at z = 0. ────────
X_IN, X_MID, X_OUT = -60.0, 0.0, 79.5
root = empty("RW-63 reducer", product="RW-63 two-stage gear reducer", revision="C", maker="PT Sumber Tani Teknik, Surabaya", ratio="6.15 : 1")
g_housing = empty("Housing", root)
g_fast = empty("Fasteners", g_housing)
g_in = empty("Input shaft assembly", root)
g_mid = empty("Intermediate shaft assembly", root)
g_out = empty("Output shaft assembly", root)
g_acc = empty("Accessories", root)

BRG = {X_IN: (47.0, 20.0, 14.0), X_MID: (47.0, 20.0, 14.0), X_OUT: (62.0, 30.0, 16.0)}


def housing_half(name, lower):
    z0, z1 = (-75.0, 0.0) if lower else (0.0, 75.0)
    shell = box(-95, 150, -50, 50, z0, z1)
    ob = cutter(shell)
    ob.name = name
    adds = [cutter(box(-103, 158, -70, 70, -8 if lower else 0, 0 if lower else 8))]
    if lower:
        adds.append(cutter(box(-110, 165, -70, 70, -75, -63)))
    for x, (od, _, w) in BRG.items():
        r = od / 2 + 9
        adds.append(cutter(cylinder(r, -56, 56, x=x, segments=64)))
    boolean(ob, adds, "UNION")
    keep = cutter(box(-200, 250, -200, 200, z0, z1))
    boolean(ob, [keep], "INTERSECT")
    cuts = [cutter(box(-87, 142, -40, 40, -67 if lower else -1, 1 if lower else 67))]
    for x, (od, _, _) in BRG.items():
        cuts.append(cutter(cylinder(od / 2, -60, 60, x=x, segments=64)))
    for bx in (-80, 0, 80, 140):
        for by in (-62, 62):
            cuts.append(cutter(cylinder(5.5, -12, 12, x=bx, z=0, segments=24, axis="Z")))
            cuts[-1].location = (0, by, 0)
    if lower:
        for fx in (-100, 155):
            for fy in (-58, 58):
                cuts.append(cutter(cylinder(7, -80, -58, x=fx, segments=24, axis="Z")))
                cuts[-1].location = (0, fy, 0)
        cuts.append(cutter(cylinder(9, 140, 160, x=0, z=-58, segments=32, axis="Y")))  # drain
        cuts[-1].rotation_euler = (0, 0, math.radians(-90))
        cuts.append(cutter(cylinder(13, -60, -38, x=105, z=-35, segments=40)))  # sight glass
    else:
        cuts.append(cutter(cylinder(8, 60, 90, x=30, z=0, segments=32, axis="Z")))  # breather
    boolean(ob, cuts, "DIFFERENCE")
    bpy.context.view_layer.update()
    bevel(ob, 1.6)
    # scale the finished mesh to metres
    ob.data.transform(Matrix.Scale(MM, 4))
    ob.data.materials.clear()
    ob.data.materials.append(MATS["Cast iron, painted"])
    ob["material"] = "Cast iron, painted"
    ob.parent = g_housing
    ob.hide_render = False
    finish(ob, 30)
    return ob


def place(bm, name, mat, parent, x=0.0, y=0.0, z=0.0, rot=None, smooth=32):
    if rot is not None:
        bmesh.ops.rotate(bm, cent=(0, 0, 0), matrix=rot, verts=bm.verts)
    bmesh.ops.scale(bm, vec=(MM, MM, MM), verts=bm.verts)
    return to_object(name, bm, mat, parent, (x * MM, y * MM, z * MM), smooth)


def bored_gear(name, teeth, module, width, bore, x, y, phase, parent, mat):
    bm, _ = gear(teeth, module, width, bore, phase)
    ob = cutter(bm)
    ob.name = name
    hole = cutter(cylinder(bore, -5, width + 5, segments=48))
    boolean(ob, [hole], "DIFFERENCE")
    ob.data.transform(Matrix.Scale(MM, 4))
    ob.data.materials.clear()
    ob.data.materials.append(MATS[mat])
    ob["material"] = mat
    ob.parent = parent
    ob.location = (x * MM, y * MM, 0)
    finish(ob, 25)
    return ob


def bearing(name, od, idia, w, x, yc, parent, number):
    ro, ri = od / 2, idia / 2
    t = (ro - ri) * 0.28
    y0, y1 = -w / 2, w / 2
    bm = revolve([(ro - t, y0), (ro, y0 + 0.6), (ro, y1 - 0.6), (ro - t, y1)], 64)
    inner = revolve([(ri, y0), (ri + t, y0), (ri + t, y1), (ri, y1)], 64)
    me = bpy.data.meshes.new("tmp")
    inner.to_mesh(me)
    inner.free()
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)
    rb = (ro - ri) * 0.22
    rpitch = (ro + ri) / 2
    for i in range(9):
        a = 2 * math.pi * i / 9
        ball = bmesh.new()
        bmesh.ops.create_uvsphere(ball, u_segments=14, v_segments=8, radius=rb)
        bmesh.ops.translate(ball, vec=(rpitch * math.cos(a), 0, rpitch * math.sin(a)), verts=ball.verts)
        me = bpy.data.meshes.new("tmp")
        ball.to_mesh(me)
        ball.free()
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)
    ob = place(bm, name, "Bearing steel SUJ2", parent, x, yc)
    sizes = {47: "6204-2RS", 62: "6206-2RS"}
    return part(ob, number, f"Deep-groove ball bearing {sizes[int(od)]}", "buy")


def cap(name, od, x, side, parent, number, through=None, thick=4):
    """End cap on the +Y (side=1) or -Y (side=-1) wall; `through` = shaft radius for a through cap."""
    ro = od / 2
    flange = ro + 9
    hole = (through + 0.6) if through else 0
    face0 = 56.0
    prof = [(hole, face0 - 3), (ro - 0.5, face0 - 3), (ro - 0.5, face0), (flange, face0), (flange, face0 + thick), (hole, face0 + thick)]
    if not through:
        prof = [(0, face0 - 3), (ro - 0.5, face0 - 3), (ro - 0.5, face0), (flange, face0), (flange - 1.5, face0 + thick), (0, face0 + thick)]
    bm = revolve(prof, 64)
    if side < 0:
        bmesh.ops.scale(bm, vec=(1, -1, 1), verts=bm.verts)
    ob = place(bm, name, "Cast iron, painted", parent, x)
    return part(ob, number, ("Through cap" if through else "Blind cap") + f", Ø{flange * 2:.0f}")


def seal(name, r_shaft, x, side, parent, number):
    ro = r_shaft + 6
    prof = [(r_shaft, 57), (r_shaft + 1.5, 57), (ro, 57.5), (ro, 62.5), (r_shaft + 2.5, 63), (r_shaft, 62)]
    bm = revolve(prof, 48)
    if side < 0:
        bmesh.ops.scale(bm, vec=(1, -1, 1), verts=bm.verts)
    ob = place(bm, name, "NBR rubber", parent, x)
    return part(ob, number, f"Oil seal TC {r_shaft * 2:.0f}×{ro * 2:.0f}×6", "buy")


def shaft(name, steps, x, parent, number, desc):
    prof = [(0, steps[0][0])]
    for y0, y1, r in steps:
        prof += [(r, y0), (r, y1)]
    prof.append((0, steps[-1][1]))
    # chamfer the two ends a little
    bm = revolve(prof, 56)
    ob = place(bm, name, "Steel S45C", parent, x)
    return part(ob, number, desc)


def key(name, w, h, y0, y1, x, r, parent, number):
    bm = box(-w / 2, w / 2, y0, y1, r - h / 2, r + h / 2)
    ob = place(bm, name, "Steel S45C", parent, x)
    return part(ob, number, f"Parallel key {w:.0f}×{h:.0f}×{y1 - y0:.0f}", "buy")


def bolt(name, x, y, parent, number):
    head = bmesh.new()
    bmesh.ops.create_cone(head, cap_ends=True, segments=6, radius1=9.2, radius2=9.2, depth=7)
    bmesh.ops.translate(head, vec=(0, 0, 8 + 3.5), verts=head.verts)
    shank = cylinder(5, -14, 8, segments=24, axis="Z")
    me = bpy.data.meshes.new("tmp")
    shank.to_mesh(me)
    shank.free()
    head.from_mesh(me)
    bpy.data.meshes.remove(me)
    bmesh.ops.remove_doubles(head, verts=head.verts, dist=0.01)
    ob = place(head, name, "Zinc-plated steel", parent, x, y, 0, smooth=40)
    return part(ob, number, "Hex bolt M10×22, 8.8", "buy")


# ── build ────────────────────────────────────────────────────────────────
base = part(housing_half("Housing base", True), "RW63-101", "Housing base, FC250 casting")
cover = part(housing_half("Housing cover", False), "RW63-102", "Housing cover, FC250 casting")
n = 0
for bx in (-80, 0, 80, 140):
    for by in (-62, 62):
        n += 1
        bolt(f"Cover bolt {n}", bx, by, g_fast, "ISO 4017-M10x22")

# input: pinion z14 m2.5 at y -28..-8
shaft("Input shaft", [(-110, -60, 9), (-60, -36, 10), (-36, -30, 12.5), (-30, 36, 11), (36, 53, 10)], X_IN, g_in, "RW63-201", "Input shaft Ø20")
p1 = part(bored_gear("Pinion z14", 14, 2.5, 20, 11, X_IN, -28, 0.0, g_in, "Steel SCM440, hardened"), "RW63-202", "Pinion z14 m2.5, 20 wide", stage="1")
bearing("Input bearing, front", 47, 20, 14, X_IN, -46, g_in, "6204-2RS")
bearing("Input bearing, rear", 47, 20, 14, X_IN, 46, g_in, "6204-2RS")
cap("Input through cap", 47, X_IN, -1, g_in, "RW63-203", through=10, thick=8)
seal("Input oil seal", 10, X_IN, -1, g_in, "TC-20x32x6")
cap("Input blind cap", 47, X_IN, 1, g_in, "RW63-204")
key("Input key", 6, 6, -106, -70, X_IN, 9, g_in, "DIN6885-A6x6x36")

# intermediate: gear z34 m2.5 at y -28..-8, pinion z15 m3 at y 0..28
shaft("Intermediate shaft", [(-53, -38, 10), (-38, -8, 14), (-8, 0, 16), (0, 38, 14), (38, 53, 10)], X_MID, g_mid, "RW63-301", "Intermediate shaft Ø20/Ø28")
part(bored_gear("Gear z34", 34, 2.5, 20, 14, X_MID, -28, math.pi + math.pi / 34, g_mid, "Steel SCM440, hardened"), "RW63-302", "Gear z34 m2.5, 20 wide", stage="1")
part(bored_gear("Pinion z15", 15, 3.0, 28, 14, X_MID, 0, 0.0, g_mid, "Steel SCM440, hardened"), "RW63-303", "Pinion z15 m3, 28 wide", stage="2")
bearing("Intermediate bearing, front", 47, 20, 14, X_MID, -46, g_mid, "6204-2RS")
bearing("Intermediate bearing, rear", 47, 20, 14, X_MID, 46, g_mid, "6204-2RS")
cap("Intermediate cap, front", 47, X_MID, -1, g_mid, "RW63-204")
cap("Intermediate cap, rear", 47, X_MID, 1, g_mid, "RW63-204")

# output: gear z38 m3 at y 0..28
shaft("Output shaft", [(-54, -38, 15), (-38, -2, 17), (-2, 0, 21), (0, 32, 19), (32, 38, 16), (38, 62, 15), (62, 110, 14)], X_OUT, g_out, "RW63-401", "Output shaft Ø30")
part(bored_gear("Gear z38", 38, 3.0, 28, 19, X_OUT, 0, math.pi + math.pi / 38, g_out, "Steel SCM440, hardened"), "RW63-402", "Gear z38 m3, 28 wide", stage="2")
bearing("Output bearing, front", 62, 30, 16, X_OUT, -46, g_out, "6206-2RS")
bearing("Output bearing, rear", 62, 30, 16, X_OUT, 46, g_out, "6206-2RS")
cap("Output blind cap", 62, X_OUT, -1, g_out, "RW63-404")
cap("Output through cap", 62, X_OUT, 1, g_out, "RW63-403", through=15, thick=8)
seal("Output oil seal", 15, X_OUT, 1, g_out, "TC-30x42x6")
key("Output key", 8, 7, 66, 106, X_OUT, 14, g_out, "DIN6885-A8x7x40")

# accessories: profiles run along +Y, then turn to face where they sit
bm = revolve([(0, 0), (7.5, 0), (7.5, 7), (11, 7), (11, 13), (6, 17), (0, 18)], 40)
br = place(bm, "Breather", "Brass", g_acc, 30, 0, 75, rot=Matrix.Rotation(math.radians(90), 3, "X"))
part(br, "RW63-501", "Breather plug M16, brass", "buy")
bm = revolve([(0, 0), (8.5, 0), (8.5, 8), (12, 8), (12, 14), (0, 14)], 6)
dr = place(bm, "Drain plug", "Zinc-plated steel", g_acc, 150, 0, -58, rot=Matrix.Rotation(math.radians(-90), 3, "Z"), smooth=40)
part(dr, "RW63-502", "Drain plug M16 with sealing ring", "buy")
bm = revolve([(0, -50), (16, -50), (16, -53), (0, -53)], 48)
sg = place(bm, "Oil sight glass", "Glass", g_acc, 105, 0, -35)
part(sg, "RW63-503", "Oil level sight glass G1/2", "buy")

for ob in PARTS:
    ob["qty"] = 1
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
print("exported", OUT, os.path.getsize(OUT), "bytes,", len(PARTS), "parts")

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
