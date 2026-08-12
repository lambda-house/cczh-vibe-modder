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
    elif shape == "ridge":
        # A box whose top collapses to a LINE, not a point — a pitched roof. `taper` cannot
        # express this: it scales both non-axis dimensions equally and so makes a pyramid.
        # This shape is an entire silhouette on its own (a shed, a carapace, a gable), which
        # is why it is a primitive rather than a modifier trick.
        bpy.ops.mesh.primitive_cube_add(size=2)
        ob = bpy.context.object
        axis = spec.get("ridgeAxis", "y")               # the ridge LINE runs along this axis
        narrow = 0 if axis == "y" else 1                # so the roof slopes in the other one
        w = spec.get("ridgeWidth", 0.0)                 # 0 = sharp ridge, 0.2 = flat-topped
        bm = bmesh.new(); bm.from_mesh(ob.data)
        for v in bm.verts:
            if v.co.z > 0:
                v.co[narrow] *= w
        if w <= 1e-6:
            # A sharp ridge leaves coincident vertex pairs along the top, and the quads between
            # them are zero-area. Left in, they survive to the W3D as degenerate triangles the
            # engine still transforms and rasterises for nothing.
            bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
        bm.to_mesh(ob.data); bm.free()
    elif shape == "dome":
        rings = spec.get("rings", 4)
        bpy.ops.mesh.primitive_uv_sphere_add(segments=segs, ring_count=rings * 2)
        ob = bpy.context.object
        bm = bmesh.new(); bm.from_mesh(ob.data)
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z < -1e-6], context="VERTS")
        edges = [e for e in bm.edges if e.is_boundary]
        if edges:
            # Cap the base. An open hemisphere is invisible from below, which is fine until the
            # camera drops or the building sits on a slope, and then it is a hole in the world.
            bmesh.ops.holes_fill(bm, edges=edges)
        # Remap z from 0..1 to -1..1, so `size` means full height and `at` means centre — the
        # same contract every other shape here has. Without it a dome is silently half as tall
        # as asked for and sits at the wrong elevation.
        for v in bm.verts:
            v.co.z = v.co.z * 2.0 - 1.0
        bm.to_mesh(ob.data); bm.free()
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

    # A part may name its OWN texture. Carried as a Blender material name, which the glTF
    # exporter writes out and `zhasset w3dfrom` turns back into that mesh's TEXTURE_NAME chunk.
    # This is what a two-tone vehicle needs: an olive hull and a rust roof are two materials on
    # two parts, not one texture with a stripe in it — a stripe in texture space becomes a band
    # at a fixed WORLD height under cube projection, and crosses everything at that height.
    # REUSED, not re-created, when two parts name the same texture. `materials.new` treats the
    # name as a hint and silently disambiguates the second one to "<name>.001" — which is then
    # exported, arrives here as the mesh's TEXTURE_NAME, and names a file nothing ever writes.
    # The two treads are the first pair of parts to share a sheet, and they found it at once.
    if spec.get("texture"):
        mat = bpy.data.materials.get(spec["texture"]) or \
            bpy.data.materials.new(name=spec["texture"])
        ob.data.materials.append(mat)

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

    # MIRROR RUNS LAST, and specifically AFTER the part has been placed. *This was a live bug:*
    # while a part is still at the origin, the mirror plane passes through the part itself, so
    # reflecting a symmetric box lands it exactly on top of itself and the modifier appears to
    # do nothing at all — a building asking for louvres down both flanks got one flank, with no
    # error and a plausible triangle count. Symmetry is about the MODEL's centreline, never the
    # part's. transform_apply has just moved the origin back to the world origin, which is what
    # makes the modifier's own axes the world's.
    mir = spec.get("mirror")
    if mir:
        m = ob.modifiers.new(name="mirror", type="MIRROR")
        m.use_axis = tuple(a in mir.lower() for a in "xyz")
        _apply(ob, m)

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


def uv_bounds(ob):
    uvs = ob.data.uv_layers.active.data
    xs = [d.uv[0] for d in uvs]; ys = [d.uv[1] for d in uvs]
    return min(xs), min(ys), max(xs), max(ys)


def pack_atlas(parts, fill=0.92):
    """Give every part its own rectangle of one shared atlas, sized by SURFACE AREA.

    This is the structural change the whole texture story was waiting on. Cube projection
    produces a tiling MATERIAL — it cannot know where it is on the model, so it can only make
    generic surface everywhere. An atlas gives each part a known rectangle, and a generator
    that knows the rectangle can paint the roof AS a roof and the tread AS a tread. That is
    what retail's hand-painted sheets do and the reason they read.

    AREA-PROPORTIONAL, not a uniform grid, and that is not a refinement — a uniform grid gives
    the barrel the same texels as the roof, which is exactly the non-uniform texel density this
    project already gates against after a cylinder's side cells came out 3.1x its cap cells.
    Rect area tracks surface area, and cube projection already gives UV area proportional to
    surface area, so the fit scale lands nearly equal for every part.

    A shelf packer, deliberately: simple, deterministic, and its waste is bounded and visible.
    Anything cleverer is a bin-packing heuristic whose output would be harder to reason about
    than the thing it saves.
    """
    import math as _m
    # UV-space area from the projection, which is world surface area scaled by cube_size.
    items = []
    for ob in parts:
        x0, y0, x1, y1 = uv_bounds(ob)
        w, h = max(x1 - x0, 1e-6), max(y1 - y0, 1e-6)
        items.append({"ob": ob, "bb": (x0, y0, w, h), "area": w * h})

    total = sum(i["area"] for i in items) or 1.0
    # Scale so the parts fill `fill` of the unit square, then lay them out largest-first.
    s = _m.sqrt(fill / total)
    for i in items:
        _, _, w, h = i["bb"]
        i["rw"], i["rh"] = w * s, h * s
    items.sort(key=lambda i: -i["rh"])

    # Shelves: fill a row until it would overflow, then start the next above it.
    rects, x, y, shelf_h = {}, 0.0, 0.0, 0.0
    for i in items:
        if x + i["rw"] > 1.0 and x > 0.0:
            x, y, shelf_h = 0.0, y + shelf_h, 0.0
        rects[i["ob"].name] = (x, y, i["rw"], i["rh"])
        x += i["rw"]
        shelf_h = max(shelf_h, i["rh"])

    # RECLAIM THE SLACK. Shelf packing leaves the far column and the top row unused, and on a
    # six-part model that was half the sheet — half the resolution paid for and thrown away.
    # Scaling every rect by the same factor to fill the square is free density: it changes no
    # layout, no aspect and no relative area, so texel density stays uniform and simply gets
    # finer. The same correction shrinks an overflowing pack, which is what it was doing before.
    used_w = max((rx + rw for rx, _, rw, _ in rects.values()), default=1.0)
    used_h = y + shelf_h
    k = 1.0 / max(used_w, used_h, 1e-6)
    if abs(k - 1.0) > 1e-6:
        rects = {n: (rx * k, ry * k, rw * k, rh * k) for n, (rx, ry, rw, rh) in rects.items()}

    # Move each part's UVs into its rectangle, preserving aspect so density stays uniform.
    for i in items:
        ob = i["ob"]
        rx, ry, rw, rh = rects[ob.name]
        x0, y0, w, h = i["bb"]
        k = min(rw / w, rh / h)
        for d in ob.data.uv_layers.active.data:
            d.uv[0] = rx + (d.uv[0] - x0) * k
            d.uv[1] = ry + (d.uv[1] - y0) * k
    return rects


def bake_ao(parts, size, path, samples=16):
    """Bake ambient occlusion from the ACTUAL geometry into the packed atlas.

    The last approximation to go. Until now occlusion was inferred from distance-to-nearest-
    seam in TEXTURE space — a guess about geometry made without looking at the geometry. It
    cannot know about the underside of a roof overhang, the inside of a boolean cut-out, or
    where a dome meets the drum it stands on. Cycles traces the real mesh and knows all three.

    Comparing a retail vehicle sheet against ours settled that this is the whole difference:
    theirs is not more colourful or higher frequency, it has dark occlusion in every recess and
    a bright lip on every top edge, and that is what makes a flat polygon read as a panelled
    one.

    Requires the atlas (non-overlapping UVs) — two parts sharing a texel cannot each bake their
    own shadow into it. That is why this lands after packing and not before.

    DETERMINISM: a bake is a sampled integration, so the seed and sample count are pinned. The
    output is hashed like any other content and the same recipe must give the same bytes.
    """
    img = bpy.data.images.new("AO", width=size, height=size)
    img.filepath_raw = path
    img.file_format = "PNG"

    # Every part needs the SAME image as its bake target, so they all get a material carrying
    # one image-texture node, selected. Bake writes into whichever node is active.
    mat = bpy.data.materials.new("BAKE_AO")
    mat.use_nodes = True
    node = mat.node_tree.nodes.new("ShaderNodeTexImage")
    node.image = img
    mat.node_tree.nodes.active = node
    saved = []
    for ob in parts:
        saved.append(list(ob.data.materials))
        ob.data.materials.clear()
        ob.data.materials.append(mat)

    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = samples
    sc.cycles.seed = 0
    sc.cycles.use_animated_seed = False
    sc.render.bake.use_clear = True
    # A margin bleeds each island's edge outward, so bilinear filtering and the mip chain do
    # not sample the empty gutter and draw a dark line around every part.
    sc.render.bake.margin = max(2, size // 64)

    bpy.ops.object.select_all(action="DESELECT")
    for ob in parts:
        ob.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.bake(type="AO")
    img.save()

    for ob, mats in zip(parts, saved):
        ob.data.materials.clear()
        for m in mats:
            ob.data.materials.append(m)
    return True


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    recipe = json.load(open(argv[0]))
    out = argv[1]

    clear()
    uv_scale = recipe.get("uvScale", 8.0)
    parts = [build_part(p, uv_scale) for p in recipe["parts"]]

    # BUDGET. Over-budget REPORTS by default and does not quietly degrade the model.
    #
    # Collapse decimation is an organic-mesh tool: run on hard surface it eats exactly the
    # bevel loops that made the thing read as built rather than blocked out, and it does it
    # silently — the triangle count lands on target and the model quietly gets worse. Retail
    # models are hand-built to budget, not decimated to it, and the honest response to an
    # over-budget recipe is to tell the author which parts are expensive so they can drop a
    # segment or a wheel. Opt in with "decimate": true if the degradation is genuinely wanted.
    budget = recipe.get("budget")
    total = sum(tri_count(o) for o in parts)
    if budget and total > budget:
        heavy = sorted(parts, key=tri_count, reverse=True)[:3]
        print(f"ZHOVER {total} {budget} "
              + " ".join(f"{o.name}={tri_count(o)}" for o in heavy))
        if recipe.get("decimate"):
            ratio = budget / float(total)
            for ob in parts:
                m = ob.modifiers.new(name="decimate", type="DECIMATE")
                m.decimate_type = "COLLAPSE"
                m.ratio = ratio
                _apply(ob, m)
            total = sum(tri_count(o) for o in parts)

    # ATLAS. Opt-in per recipe, because a tiling material is still the right answer for a
    # large flat surface and switching every existing recipe at once would be a change nobody
    # asked for. When on, each part owns a rectangle and the generator paints into it.
    if recipe.get("atlas"):
        # A TREAD IS EXCLUDED FROM THE PACK, and it has to be excluded from the BAKE for the
        # same reason it is excluded from the pack. W3DTankDraw animates a tread by adding to
        # its U offset, so its UVs must stay a full tiling material rather than a rectangle of
        # a shared sheet — and a part whose UVs run well past 0..1 cannot be a bake target
        # either: every repeat would write into the same texels, over the top of whatever part
        # legitimately owns them. It stays in the SCENE, so it still casts occlusion onto the
        # hull above it; it simply is not a surface the bake writes to.
        atlased = [o for o in parts if not o.name.upper().startswith("TREADS")]
        rects = pack_atlas(atlased)
        for ob in atlased:
            rx, ry, rw, rh = rects[ob.name]
            print(f"ZHUV {ob.name} {rx:.6f} {ry:.6f} {rw:.6f} {rh:.6f}")
        if recipe.get("ao", True) and len(argv) > 2 and atlased:
            size = recipe.get("textureSize", 256)
            try:
                bake_ao(atlased, size, argv[2], recipe.get("aoSamples", 16))
                print(f"ZHAO {argv[2]} {size}")
            except Exception as e:
                # A failed bake must not lose the model. The material still carries its own
                # approximated occlusion, so the result is the previous quality, not nothing.
                print(f"ZHAOFAIL {e}")

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
