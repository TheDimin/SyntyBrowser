import argparse
import json
import math
import os
import re
import sys

import bpy
from mathutils import Vector


class UnsupportedPreviewError(RuntimeError):
    pass


def parse_args():
    separator = sys.argv.index("--") if "--" in sys.argv else len(sys.argv)
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-fbx")
    parser.add_argument("--output-png")
    parser.add_argument("--bindings-json")
    parser.add_argument("--manifest-json")
    parser.add_argument("--skipped-json")
    parser.add_argument("--resolution", type=int, default=128)
    parser.add_argument("--samples", type=int, default=8)
    return parser.parse_args(sys.argv[separator + 1 :])


def canonical_name(value):
    name = value.rsplit("|", 1)[-1]
    if len(name) > 4 and name[-4] == "." and name[-3:].isdigit():
        name = name[:-4]
    return name.casefold()


def is_preview_mesh(obj):
    name = canonical_name(obj.name)
    if "collision" in name or re.search(r"(?:^|_)(?:ucx|ubx|ucp|usp)(?:_|$)", name):
        return False
    lod = re.search(r"(?:^|_)lod(\d+)(?:_|$)", name)
    return lod is None or int(lod.group(1)) == 0


def make_material(binding):
    material = bpy.data.materials.new(
        f"SyntyPreview_{binding['mesh_name']}_{binding['slot_ordinal']}"
    )
    material.use_nodes = True
    nodes = material.node_tree.nodes
    shader = nodes.get("Principled BSDF")
    texture_path = binding.get("texture_path")
    if texture_path:
        image = bpy.data.images.load(texture_path, check_existing=True)
        texture = nodes.new("ShaderNodeTexImage")
        texture.image = image
        texture.interpolation = "Closest"
        material.node_tree.links.new(texture.outputs["Color"], shader.inputs["Base Color"])
        material.node_tree.links.new(texture.outputs["Alpha"], shader.inputs["Alpha"])
    else:
        shader.inputs["Base Color"].default_value = (0.32, 0.36, 0.42, 1.0)
    shader.inputs["Roughness"].default_value = 0.8
    material.surface_render_method = "DITHERED"
    return material


def bind_materials(bindings, mesh_objects):
    by_name = {}
    for obj in mesh_objects:
        for name in {canonical_name(obj.name), canonical_name(obj.data.name)}:
            by_name.setdefault(name, []).append(obj)

    applied = 0
    missing = []
    for binding in bindings:
        objects = by_name.get(canonical_name(binding["mesh_name"]), [])
        ordinal = binding["slot_ordinal"]
        if not objects:
            missing.append(f"{binding['mesh_name']}[{ordinal}]: mesh not found")
            continue
        for obj in objects:
            if ordinal >= len(obj.material_slots):
                missing.append(
                    f"{binding['mesh_name']}[{ordinal}]: imported mesh has "
                    f"{len(obj.material_slots)} slot(s)"
                )
                continue
            obj.material_slots[ordinal].material = make_material(binding)
            applied += 1

    if bindings and not applied:
        raise UnsupportedPreviewError(
            "No MaterialList mesh/slot bindings matched the imported FBX: "
            + "; ".join(missing)
        )
    if missing:
        print("Synty preview binding warnings: " + "; ".join(missing))


def scene_bounds(objects):
    points = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
    minimum = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    maximum = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    return (minimum + maximum) * 0.5, maximum - minimum


def render(output_path, mesh_objects, resolution, samples):
    center, size = scene_bounds(mesh_objects)
    radius = max(size.length * 0.5, 0.01)

    world = bpy.context.scene.world or bpy.data.worlds.new("SyntyPreviewWorld")
    bpy.context.scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.055, 0.065, 0.08, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.65

    camera_data = bpy.data.cameras.new("SyntyPreviewCamera")
    camera = bpy.data.objects.new("SyntyPreviewCamera", camera_data)
    bpy.context.scene.collection.objects.link(camera)
    direction = Vector((1.4, -1.7, 1.15)).normalized()
    camera.location = center + direction * radius * 3.0
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = max(size.x, size.y, size.z) * 1.35
    bpy.context.scene.camera = camera

    light_data = bpy.data.lights.new("SyntyPreviewKey", "AREA")
    light_data.energy = 900
    light_data.shape = "DISK"
    light_data.size = radius * 2.0
    light = bpy.data.objects.new("SyntyPreviewKey", light_data)
    bpy.context.scene.collection.objects.link(light)
    light.location = center + Vector((-1.5, -2.0, 3.0)).normalized() * radius * 3.0
    light.rotation_euler = (center - light.location).to_track_quat("-Z", "Y").to_euler()

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.eevee.taa_render_samples = samples
    scene.render.resolution_x = resolution
    scene.render.resolution_y = resolution
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.compression = 15
    scene.render.film_transparent = True
    scene.render.filepath = output_path
    scene.view_settings.look = "AgX - Medium High Contrast"
    bpy.ops.render.render(write_still=True)


def render_job(source_fbx, output_png, bindings, resolution, samples):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=source_fbx, use_anim=False)
    meshes = [
        obj
        for obj in bpy.context.scene.objects
        if obj.type == "MESH" and is_preview_mesh(obj)
    ]
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH" and obj not in meshes:
            obj.hide_render = True
    if not meshes:
        raise UnsupportedPreviewError("FBX contains no renderable meshes")

    bind_materials(bindings, meshes)
    os.makedirs(os.path.dirname(os.path.abspath(output_png)), exist_ok=True)
    render(output_png, meshes, resolution, samples)


def main():
    args = parse_args()
    if args.resolution < 64 or args.resolution > 512:
        raise ValueError("Resolution must be between 64 and 512 pixels")
    if args.samples < 1 or args.samples > 64:
        raise ValueError("Samples must be between 1 and 64")

    if args.manifest_json:
        with open(args.manifest_json, "r", encoding="utf-8-sig") as stream:
            jobs = json.load(stream)
        skipped = []
        for index, job in enumerate(jobs, start=1):
            print(f"Synty preview {index}/{len(jobs)}: {job['source_fbx']}")
            try:
                render_job(
                    job["source_fbx"],
                    job["output_png"],
                    job.get("bindings", []),
                    args.resolution,
                    args.samples,
                )
            except UnsupportedPreviewError as error:
                skipped.append(
                    {
                        "source_fbx": job["source_fbx"],
                        "output_png": job["output_png"],
                        "reason": str(error),
                    }
                )
                print(f"Synty preview skipped: {job['source_fbx']}: {error}")
        if args.skipped_json:
            with open(args.skipped_json, "w", encoding="utf-8") as stream:
                json.dump(skipped, stream, indent=2)
        return

    if not args.source_fbx or not args.output_png:
        raise ValueError("--source-fbx and --output-png are required without --manifest-json")
    bindings = []
    if args.bindings_json:
        with open(args.bindings_json, "r", encoding="utf-8-sig") as stream:
            bindings = json.load(stream)
    render_job(args.source_fbx, args.output_png, bindings, args.resolution, args.samples)


if __name__ == "__main__":
    main()
