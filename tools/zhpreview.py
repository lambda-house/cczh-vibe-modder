"""Render one model from several angles at once, so a recipe change can be JUDGED IN SECONDS.

Runs inside Blender, never under system Python.

WHY THIS EXISTS
    `CLAUDE.md` already says a transform that silently did nothing is this pipeline's
    characteristic bug and that no structural check catches it — render it and LOOK. That rule
    has been paid for repeatedly: a part collapsed to the origin, a mirror that reflected a box
    onto itself, an atlas region sampling its own empty band, road wheels that turned out to be
    the atlas seen through tiling UVs. Every one was invisible to counts, byte-identical
    round-trips and an independent reader, and obvious in one picture.

    But "render it and look" was a manual ritual — export glTF, write a camera script, pick an
    angle, look, realise the angle hid the defect, pick another. That loop is slow enough that
    the temptation is to skip it and boot the game instead, which is slower still and shows one
    angle at a distance.

    So: ONE COMMAND, SEVERAL ANGLES, ONE IMAGE. The four are not decorative. Three-quarter is
    what the game shows; SIDE is where proportion lives and where every silhouette correction
    in this project has come from; FRONT is where an opening either reads as a hole or does
    not; and a LOW close pass on the running gear is where texture-space bugs surface, because
    that is the part small enough to be wrong without the silhouette changing.

    This is the preview half of the authoring loop. The game remains the only authority on
    whether the ENGINE agrees — a clean boot validates every enum literal in a file and no
    field name — but it is a poor way to ask whether a model is the right shape.
"""

import os
import sys

import bpy
import mathutils


def frame(objs):
    mn = mathutils.Vector((1e9,) * 3)
    mx = mathutils.Vector((-1e9,) * 3)
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ mathutils.Vector(c)
            for i in range(3):
                mn[i] = min(mn[i], w[i])
                mx[i] = max(mx[i], w[i])
    return (mn + mx) / 2, (mx - mn).length / 2


def main():
    import math
    argv = sys.argv[sys.argv.index('--') + 1:]
    glb, outdir, res = argv[0], argv[1], int(argv[2])

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=glb)
    sc = bpy.context.scene
    objs = [o for o in bpy.data.objects if o.type == 'MESH']
    if not objs:
        print('ZHPFAIL no meshes')
        return
    ctr, rad = frame(objs)

    cam_d = bpy.data.cameras.new('c')
    cam = bpy.data.objects.new('c', cam_d)
    sc.collection.objects.link(cam)
    sc.camera = cam
    cam_d.type = 'ORTHO'

    # Two suns, fixed. A preview must be COMPARABLE between runs — the point is to see what
    # changed in the model, and a light that moves with the subject hides exactly that.
    key = bpy.data.lights.new('k', type='SUN'); key.energy = 4.5
    ko = bpy.data.objects.new('k', key); sc.collection.objects.link(ko)
    ko.rotation_euler = (math.radians(52), 0, math.radians(35))
    fill = bpy.data.lights.new('f', type='SUN'); fill.energy = 1.6
    fo = bpy.data.objects.new('f', fill); sc.collection.objects.link(fo)
    fo.rotation_euler = (math.radians(64), 0, math.radians(210))

    sc.render.engine = 'BLENDER_EEVEE'          # not BLENDER_EEVEE_NEXT; the enum is the old one
    sc.render.resolution_x = sc.render.resolution_y = res
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGBA'

    d = rad * 4
    # name, camera direction (multiples of the framing distance), zoom, vertical aim offset as
    # a fraction of the radius. The last two are what make the close pass useful rather than
    # just smaller: it drops the aim to the running gear instead of shrinking the whole model.
    views = [
        ('quarter', (0.62, -0.66, 0.34), 2.05, 0.0),
        ('side', (0.0, -1.0, 0.06), 2.05, 0.0),
        ('front', (1.0, -0.30, 0.30), 2.05, 0.0),
        ('gear', (0.0, -1.0, -0.16), 1.05, -0.42),
    ]
    for name, dir3, zoom, aim in views:
        target = ctr + mathutils.Vector((0, 0, aim * rad))
        cam_d.ortho_scale = rad * zoom
        cam.location = target + mathutils.Vector(dir3) * d
        cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
        sc.render.filepath = os.path.join(outdir, f'{name}.png')
        bpy.ops.render.render(write_still=True)
        print(f'ZHP {name}')

    print('ZHP ok')


main()
