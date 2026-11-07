"""Build an editable Blender scene from the geometry emitted by bake-suburban-town.cjs."""
import json
from collections import defaultdict
from pathlib import Path
import sys

import bpy
from mathutils import Vector


source, output, *preview = sys.argv[sys.argv.index("--") + 1:]
data = json.loads(Path(source).read_text(encoding="utf-8"))
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = "METRIC"
voxel_size = 0.165

materials = {}


def linear(channel):
    value = channel / 255
    return value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4


def material(color):
    if color not in materials:
        rgba = data["palette"][(color - 1) * 4:color * 4]
        item = bpy.data.materials.new(f"VOX {color:03d}")
        item.diffuse_color = (*map(linear, rgba[:3]), 1)
        item.use_nodes = True
        shader = item.node_tree.nodes.get("Principled BSDF")
        shader.inputs["Base Color"].default_value = item.diffuse_color
        shader.inputs["Roughness"].default_value = 0.9
        materials[color] = item
    return materials[color]


objects = defaultdict(list)
for face in data["faces"]:
    objects[face[-1]].append(face[:-1])

for object_index, quads in objects.items():
    vertices, polygons, colors = [], [], []
    vertex_indices = {}
    for axis, sign, plane, u, v, width, height, color in quads:
        corners = [(u, v), (u + width, v), (u + width, v + height), (u, v + height)]
        polygon = []
        for a, b in corners:
            coords = [0, 0, 0]
            coords[axis] = plane
            coords[(axis + 1) % 3] = a
            coords[(axis + 2) % 3] = b
            x, y, z = coords
            key = (x, y, z)
            if key not in vertex_indices:
                vertex_indices[key] = len(vertices)
                vertices.append((x * voxel_size, y * voxel_size, (z - 1) * voxel_size))
            polygon.append(vertex_indices[key])
        polygons.append(polygon if sign > 0 else polygon[::-1])
        colors.append(color)

    name = data["names"][object_index]
    bounds = list(zip(*vertices))
    pivot = Vector(((min(bounds[0]) + max(bounds[0])) / 2,
                    (min(bounds[1]) + max(bounds[1])) / 2, min(bounds[2])))
    vertices = [Vector(vertex) - pivot for vertex in vertices]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], polygons)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    scene.collection.objects.link(obj)
    obj.location = pivot
    slots = {}
    for polygon, color in zip(mesh.polygons, colors):
        if color not in slots:
            slots[color] = len(mesh.materials)
            mesh.materials.append(material(color))
        polygon.material_index = slots[color]

if data.get("panes"):
    windows = bpy.data.collections.new("Breakable windows")
    scene.collection.children.link(windows)
    glass = bpy.data.materials.new("Window glass")
    glass.diffuse_color = (0.38, 0.69, 0.86, 0.25)
    glass.use_nodes = True
    shader = glass.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = glass.diffuse_color
    shader.inputs["Roughness"].default_value = 0.24
    shader.inputs["Alpha"].default_value = glass.diffuse_color[3]

    bpy.ops.mesh.primitive_cube_add(size=1)
    template = bpy.context.object
    pane_mesh = template.data
    pane_mesh.name = "Window glass prop"
    pane_mesh.materials.append(glass)

    for building, x, y, z, width, height, axis in data["panes"]:
        pane = bpy.data.objects.new(f"Window {building}/{x}/{y}/{z}", pane_mesh)
        windows.objects.link(pane)
        size = width * voxel_size
        pane.location = ((x + (width / 2 if axis == 1 else 0)) * voxel_size,
                         (y + (width / 2 if axis == 0 else 0)) * voxel_size,
                         (z - 1 + height / 2) * voxel_size)
        pane.scale = (0.025 if axis == 0 else size,
                      0.025 if axis == 1 else size, height * voxel_size)
    bpy.data.objects.remove(template, do_unlink=True)

shading = scene.display.shading
shading.color_type = "MATERIAL"
shading.light = "STUDIO"
shading.show_shadows = True
shading.show_cavity = True
scene.view_settings.view_transform = "Standard"
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type == "VIEW_3D":
            space = area.spaces.active
            space.shading.color_type = "MATERIAL"
            space.region_3d.view_location = (1, 8, 0)
            space.region_3d.view_distance = 74
            space.region_3d.view_rotation = Vector((33, -46, 37)).to_track_quat("Z", "Y")
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=str(Path(output).resolve()), compress=True)

if preview:
    camera = bpy.data.objects.new("Inspection camera", bpy.data.cameras.new("Inspection camera"))
    scene.collection.objects.link(camera)
    camera.location = (34, -38, 37)
    camera.rotation_euler = (Vector((1, 8, 0)) - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = 78
    scene.camera = camera
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = 960
    scene.render.resolution_y = 720
    scene.render.filepath = preview[0]
    bpy.ops.render.render(write_still=True)

