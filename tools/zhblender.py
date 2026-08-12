"""zhblender — the modelling backend. RUNS INSIDE BLENDER, never under system Python.

    blender --background --python tools/zhblender.py -- <recipe.json> <out.glb>

WHY THIS EXISTS
    The hand-rolled generators in `zhasset` compute vertices directly, which is fine for a box
    and a cylinder and stops there. What separates a building from a block is bevelled edges,
    cut openings, repeated panels and a sloped face — i.e. boolean, bevel, array, mirror. Those
    are a geometry KERNEL, and writing one is months of work that Blender has already done.

    Measured, the target is small: the shipped corpus is median 169 triangles, p90 619. This is
    a 2003 hard-surface budget, which is exactly why a generative 3D model is the wrong tool
    and a parametric recipe is the right one — and why `budget` below is a first-class field
    rather than an afterthought.

WHAT IT PRODUCES
    One glTF mesh per PART, named. That naming is load-bearing: `zhasset w3dfrom` turns each
    into a W3D sub-object, and the engine addresses a turret by name to rotate it. A model
    whose parts are merged into one mesh cannot animate, however good it looks.

DETERMINISM
    Same recipe must give the same bytes, because the output is hashed like any other content.
    Nothing here uses a random seed: bevel, boolean (EXACT solver), array, mirror and decimate
    are all deterministic functions of their input. Blender's VERSION is the variable that is
    not pinned by this file — record it with the output rather than assuming it cannot matter.
"""

import bpy
import bmesh
import json
import math
import sys


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def _prim(spec):
    """Create one primitive at the origin, sized to `size`, and return the object.

    Everything is built at the origin and moved afterwards, because a modifier stack applied to
    an object with a non-identity transform bakes that transform in at a different point than
    you expect — a bevel width, for instance, is in LOCAL units and silently scales with the
    object. Build at unit transform, apply, then place.
    """
    shape = spec.get("shape", "box")
    sx, sy, sz = spec.get("size", [1, 1, 1])
    segs = spec.get("segments", 12)

    if shape == "box":
        bpy.ops.mesh.primitive_cube_add(size=2)
    elif shape == "cylinder":
        bpy.ops.mesh.primitive_cylinder_add(vertices=segs, radius=1, depth=2)
    elif shape == "cone":
        # radius2 as a fraction of radius1: a truncated cone is the shape a turret or a
        # chimney actually is, and a true point is rarely what is wanted.
        bpy.ops.mesh.primitive_cone_add(vertices=segs, radius1=1,
                                        radius2=spec.get("tip", 0.0), depth=2)
    elif shape == "wedge":
        # A ramp: the sloped-front hull that makes a vehicle read as a vehicle rather than a
        # crate. Built by hand because Blender has no wedge primitive.
        bpy.ops.mesh.primitive_cube_add(size=2)
        ob = bpy.context.object
        bm = bmesh.new(); bm.from_mesh(ob.data)
        for v in bm.verts:
            if v.co.z > 0:
                v.co.y = v.co.y * 0.35 + 0.65      # pull the top face toward +y
        bm.to_mesh(ob.data); bm.free()
    else:
        raise ValueError(f"unknown shape {shape!r}")

    ob = bpy.context.object
    ob.scale = (sx / 2.0, sy / 2.0, sz / 2.0)
    _only(ob)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return ob


def _taper(ob, factor, axis="z"):
    """Scale the far face toward the near one — a slab becomes a plinth, a box a hull.

    Done in bmesh on the finished primitive rather than as a modifier, so it composes with
    everything downstream instead of fighting the modifier stack's order.
    """
    if factor is None or factor == 1.0:
        return
    i = "xyz".index(axis)
    lo = min(v.co[i] for v in ob.data.vertices)
    hi = max(v.co[i] for v in ob.data.vertices)
    if hi - lo < 1e-9:
        return
    bm = bmesh.new(); bm.from_mesh(ob.data)
    for v in bm.verts:
        t = (v.co[i] - lo) / (hi - lo)              # 0 at the near face, 1 at the far one
        s = 1.0 + (factor - 1.0) * t
        for k in range(3):
            if k != i:
                v.co[k] *= s
    bm.to_mesh(ob.data); bm.free()


def _only(ob):
    """Make `ob` the sole selected AND active object.

    `bpy.ops.object.transform_apply` acts on the SELECTION, not on the active object, and it
    reports success either way. *This was a live bug:* creating a boolean cutter left the
    cutter selected and the part not, so the part's final transform_apply silently did nothing,
    its position stayed on the glTF node instead of in its vertices, and the building came out
    with its main hall fifteen units below where the recipe put it. Every operator below that
    depends on selection goes through here for that reason.
    """
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob


def _apply(ob, mod):
    _only(ob)
    bpy.ops.object.modifier_apply(modifier=mod.name)


def _cut(ob, spec):
    """Boolean-difference a shape out of `ob`. This is what puts a doorway in a wall.

    A cut's `at` is LOCAL TO THE PART, not world: the part is still sitting at the origin at
    this point in the stack, and it has to be, because a bevel width is in local units and
    would otherwise scale with wherever the part happens to stand. So a door in the front face
    of a 68-deep hall is at y = -30, whatever the hall's own `at` turns out to be.
    """
    cutter = _prim(spec)
    cutter.location = spec.get("at", [0, 0, 0])
    cutter.rotation_euler = [math.radians(a) for a in spec.get("rot", [0, 0, 0])]
    _only(cutter)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    m = ob.modifiers.new(name="cut", type="BOOLEAN")
    m.operation = spec.get("op", "DIFFERENCE").upper()
    m.object = cutter
    # EXACT rather than FAST: FAST is a float-tolerance solver whose output depends on the
    # order faces happen to be visited, which is precisely the non-determinism this pipeline
    # cannot have.
    m.solver = "EXACT"
    _apply(ob, m)
    bpy.data.objects.remove(cutter, do_unlink=True)


def build_part(spec, uv_scale):
    ob = _prim(spec)
    ob.name = spec["name"]
    ob.data.name = spec["name"]

    _taper(ob, spec.get("taper"), spec.get("taperAxis", "z"))

    for c in spec.get("cuts", []):
        _cut(ob, c)

    arr = spec.get("array")
    if arr:
        m = ob.modifiers.new(name="array", type="ARRAY")
        m.count = arr["count"]
        m.use_relative_offset = False
        m.use_constant_offset = True
        m.constant_offset_displace = arr.get("offset", [0, 0, 0])
        _apply(ob, m)

    mir = spec.get("mirror")
    if mir:
        m = ob.modifiers.new(name="mirror", type="MIRROR")
        m.use_axis = tuple(a in mir.lower() for a in "xyz")
        _apply(ob, m)

    bev = spec.get("bevel")
    if bev:
        m = ob.modifiers.new(name="bevel", type="BEVEL")
        m.width = bev if isinstance(bev, (int, float)) else bev.get("width", 0.5)
        m.segments = 1 if isinstance(bev, (int, float)) else bev.get("segments", 1)
        # Limit by angle so flat continuations are left alone and only real edges round over.
        # Without it every coplanar seam left by a boolean gets bevelled too, which multiplies
        # the triangle count for no visible gain — expensive against a 169-triangle median.
        m.limit_method = "ANGLE"
        m.angle_limit = math.radians(30)
        m.miter_outer = "MITER_ARC"
        _apply(ob, m)

    # Place it only now — see the note in _prim about local units.
    ob.location = spec.get("at", [0, 0, 0])
    ob.rotation_euler = [math.radians(a) for a in spec.get("rot", [0, 0, 0])]
    _only(ob)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # CUBE projection, not smart-project. The house rule is that texel density must be UNIFORM
    # across surfaces — a live bug once made a cylinder's side cells 3.1x wider than its cap
    # cells, visible in game and invisible to every structural check. A cube projection at a
    # fixed world size gives that by construction; smart-project optimises for packing instead
    # and would reintroduce exactly the defect the gate exists to catch.
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.cube_project(cube_size=uv_scale)
    bpy.ops.object.mode_set(mode="OBJECT")
    return ob


def tri_count(ob):
    ob.data.calc_loop_triangles()
    return len(ob.data.loop_triangles)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    recipe = json.load(open(argv[0]))
    out = argv[1]

    clear()
    uv_scale = recipe.get("uvScale", 8.0)
    parts = [build_part(p, uv_scale) for p in recipe["parts"]]

    # BUDGET. Decimation is applied AFTER every part exists, at one ratio across all of them,
    # so the model keeps its proportions instead of whichever part was built last being the
    # one that survives intact.
    budget = recipe.get("budget")
    total = sum(tri_count(o) for o in parts)
    if budget and total > budget:
        ratio = budget / float(total)
        for ob in parts:
            m = ob.modifiers.new(name="decimate", type="DECIMATE")
            m.decimate_type = "COLLAPSE"
            m.ratio = ratio
            _apply(ob, m)
        total = sum(tri_count(o) for o in parts)

    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        export_apply=True,
        export_normals=True,
        export_texcoords=True,
        export_materials="EXPORT",
        export_yup=True,
        use_selection=False,
    )
    for ob in parts:
        print(f"ZHPART {ob.name} {len(ob.data.vertices)} {tri_count(ob)}")
    print(f"ZHTOTAL {len(parts)} {total} {bpy.app.version_string}")


main()
