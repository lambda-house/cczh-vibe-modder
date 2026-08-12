"""Grade an authored `.w3d` with a SECOND, INDEPENDENT implementation of the format.

Runs inside Blender, never under system Python — like `zhblender.py`, and for the same reason.

WHY THIS EXISTS
    Everything that has graded our W3D writer so far shares its assumptions. `zhasset w3d`
    reads the file with the same chunk table that wrote it; `w3dround` proves our reader and
    our writer agree with each other; and the glTF round-trip goes out through our own
    exporter before Blender ever sees it. Three checks, one opinion. A writer checked only by
    its own reader proves the two agree, not that either is right — which is exactly the
    argument `CLAUDE.md` already makes for authored MAPS, where `tools/zhasset map` earns the
    right to grade our output by first decoding all 150 shipped ones.

    OpenSAGE's Blender plugin is that second opinion for meshes. It was written by people who
    reverse-engineered the same format from the same files and share none of our code, our
    chunk table, or our misconceptions. If it can open what we wrote and find the sub-objects,
    the geometry, the materials and the skeleton we think are in there, the file is not merely
    self-consistent.

    It is NOT vendored and NOT a dependency. Like Blender itself it is an author-time tool:
    clone it beside the other reference trees and this works, omit it and the gate skips.

        git clone https://github.com/OpenSAGE/OpenSAGE.BlenderPlugin ~/work/oss/OpenSAGE.BlenderPlugin

OUTPUT
    One `ZHG <key> <value>` line per fact, for the caller to compare against its own reading.
    Anything else on stdout is Blender's or the plugin's own noise.
"""

import os
import sys

import bpy


def _fail(msg):
    print(f'ZHGFAIL {msg}')
    sys.exit(0)                 # not a crash: the caller decides whether absence is an error


def main():
    argv = sys.argv[sys.argv.index('--') + 1:]
    plugin_dir, w3d = argv[0], argv[1]

    if not os.path.isdir(os.path.join(plugin_dir, 'io_mesh_w3d')):
        _fail(f'no io_mesh_w3d package under {plugin_dir}')

    # Put the plugin on the path as a top-level package rather than installing it into the
    # user's Blender profile. Installing would make a machine-wide change to run a check, and
    # would silently pin whatever version happened to be installed the day it was set up.
    if plugin_dir not in sys.path:
        sys.path.insert(0, plugin_dir)

    bpy.ops.wm.read_factory_settings(use_empty=True)

    try:
        import io_mesh_w3d
        io_mesh_w3d.register()
    except Exception as e:                                  # noqa: BLE001 - report, never raise
        # The plugin declares Blender 2.90 and we run far newer. A registration failure is a
        # real answer — it means this grader cannot run here — and must not read as a bad mesh.
        _fail(f'plugin will not register on Blender {bpy.app.version_string}: {e}')

    try:
        bpy.ops.import_mesh.westwood_w3d(filepath=os.path.abspath(w3d))
    except Exception as e:                                  # noqa: BLE001
        # THIS one is a genuine verdict: the file was reached and refused.
        print(f'ZHG refused 1')
        print(f'ZHG error {e}')
        return

    meshes = [o for o in bpy.data.objects if o.type == 'MESH']
    tris = 0
    for o in meshes:
        o.data.calc_loop_triangles()
        tris += len(o.data.loop_triangles)

    # Names are the load-bearing part of a W3D and the reason this check matters most: the
    # engine finds a tread, a house-colour submesh and a turret by string-matching them. A
    # reader that returns the geometry but mangles the names would pass every count.
    print(f'ZHG plugin {".".join(str(v) for v in io_mesh_w3d.VERSION)}')
    print(f'ZHG blender {bpy.app.version_string}')
    print(f'ZHG meshes {len(meshes)}')
    print(f'ZHG vertices {sum(len(o.data.vertices) for o in meshes)}')
    print(f'ZHG triangles {tris}')
    for o in sorted(meshes, key=lambda o: o.name):
        print(f'ZHG mesh {o.name}')

    for arm in [o for o in bpy.data.objects if o.type == 'ARMATURE']:
        print(f'ZHG armature {arm.name} {len(arm.data.bones)}')
        for b in sorted(arm.data.bones, key=lambda b: b.name):
            print(f'ZHG bone {b.name}')

    for m in sorted(bpy.data.materials, key=lambda m: m.name):
        print(f'ZHG material {m.name}')
    for i in sorted(bpy.data.images, key=lambda i: i.name):
        print(f'ZHG image {os.path.basename(i.name)}')

    print('ZHG ok 1')


main()
