extends SceneTree
## Dumps the authored lighting rig of a Blitz Legacy Godot scene as JSON.
##
## The port used hand-measured calibration factors because the source rig was
## only ever read out of the .tscn text by eye. This tool reads the live
## resources instead: every stored property of the Environment, of every
## Light3D and of every GeometryInstance3D, plus the LightmapGI atlas layout,
## its per-instance UV rects and the baked light probes. Tools/port_godot_lighting.py
## copies it into the source project, runs it headless and turns the JSON into
## the port's runtime data.

const SCENES := {
	"hangar": "res://maps/hangar_low.tscn",
	"rudniki": "res://maps/rudniki/_rudniki.tscn",
}

const MESH_MAGIC := "BLLM"
const MESH_VERSION := 1

var _mesh_file: FileAccess = null
var _mesh_keys := {}
var _meshes: Array = []
var _material_keys := {}
var _materials: Array = []

func _initialize() -> void:
	var out := OS.get_environment("BLITZ_LIGHTING_OUT")
	if out.is_empty():
		out = "/tmp/blitz_lighting"
	DirAccess.make_dir_recursive_absolute(out)
	for key in SCENES.keys():
		_dump_scene(key, SCENES[key], out)
	quit()

func _dump_scene(key: String, path: String, out: String) -> void:
	var packed: PackedScene = load(path)
	if packed == null:
		push_error("missing scene %s" % path)
		return
	var root: Node = packed.instantiate()
	var payload := {
		"scene": path,
		"environments": [],
		"lights": [],
		"lightmaps": [],
		"geometry": [],
		"meshes": [],
		"materials": [],
	}
	## Lightmap UVs live in the mesh resources, so the geometry has to travel
	## with the atlas: a slice cannot be sampled without UV2.
	_mesh_keys = {}
	_meshes = payload["meshes"]
	_material_keys = {}
	_materials = payload["materials"]
	_mesh_file = FileAccess.open("%s/%s_meshes.bin" % [out, key], FileAccess.WRITE)
	_mesh_file.store_buffer(MESH_MAGIC.to_utf8_buffer())
	_mesh_file.store_32(MESH_VERSION)
	_walk(root, root, payload, Transform3D.IDENTITY, true)
	_mesh_file.close()
	_mesh_file = null
	var file := FileAccess.open("%s/%s.json" % [out, key], FileAccess.WRITE)
	file.store_string(JSON.stringify(payload, "\t"))
	file.close()
	print("[dump] %s: lights=%d geometry=%d meshes=%d materials=%d lightmaps=%d environments=%d" % [
		key,
		payload["lights"].size(),
		payload["geometry"].size(),
		payload["meshes"].size(),
		payload["materials"].size(),
		payload["lightmaps"].size(),
		payload["environments"].size(),
	])
	root.free()

## Global transforms are composed by hand: Node3D.global_transform only works
## inside a SceneTree, and adding the scene to one would run the gameplay
## scripts this tool has no business running.
func _walk(node: Node, root: Node, payload: Dictionary, parent: Transform3D, shown: bool) -> void:
	var world := parent
	var visible := shown
	if node is Node3D:
		world = node.transform if node.top_level else parent * node.transform
		visible = shown and node.visible
	if node is WorldEnvironment and node.environment != null:
		payload["environments"].append({
			"path": String(root.get_path_to(node)),
			"resource": node.environment.resource_path,
			"environment": _properties(node.environment),
			"camera_attributes": _properties(node.camera_attributes),
		})
	elif node is Light3D:
		payload["lights"].append({
			"path": String(root.get_path_to(node)),
			"type": node.get_class(),
			"visible": visible,
			"transform": _transform(world),
			"properties": _properties(node),
		})
	elif node is LightmapGI:
		var entry := {
			"path": String(root.get_path_to(node)),
			"properties": _properties(node),
			"data": null,
			"textures": [],
			"users": [],
		}
		if node.light_data != null:
			entry["data"] = {
				"resource": node.light_data.resource_path,
				"properties": _properties(node.light_data),
			}
			entry["textures"] = _textures(node.light_data)
			entry["users"] = _lightmap_users(node, root)
		payload["lightmaps"].append(entry)
	if node is GeometryInstance3D:
		payload["geometry"].append(_geometry(node, root, world, visible))
	for child in node.get_children():
		_walk(child, root, payload, world, visible)

func _geometry(node: GeometryInstance3D, root: Node, world: Transform3D, visible: bool) -> Dictionary:
	var entry := {
		"path": String(root.get_path_to(node)),
		"name": String(node.name),
		"type": node.get_class(),
		"visible": visible,
		"transform": _transform(world),
		"cast_shadow": node.cast_shadow,
		"gi_mode": node.gi_mode,
		"gi_lightmap_scale": node.gi_lightmap_scale,
		"layers": node.layers,
		"material_override": (
			node.material_override.resource_path if node.material_override != null else ""
		),
		"mesh": null,
	}
	if node is MeshInstance3D and node.mesh != null:
		var instance: MeshInstance3D = node
		var mesh: Mesh = instance.mesh
		var surfaces := []
		for i in mesh.get_surface_count():
			var format := 0
			if mesh is ArrayMesh:
				format = mesh.surface_get_format(i)
			## The batched hangar renders with material_override, not with the
			## blank materials embedded in the ArrayMesh, so ask the instance
			## which material it actually draws with.
			var material := instance.get_active_material(i)
			var embedded := mesh.surface_get_material(i)
			surfaces.append({
				"index": i,
				"format": format,
				"mesh_key": _store_surface(mesh, i),
				"has_uv2": (format & Mesh.ARRAY_FORMAT_TEX_UV2) != 0,
				"material": material.resource_path if material != null else "",
				"material_name": material.resource_name if material != null else "",
				"material_key": _store_material(material),
				"embedded_material": embedded.resource_path if embedded != null else "",
			})
		entry["mesh"] = {
			"resource": mesh.resource_path,
			"name": mesh.resource_name,
			"class": mesh.get_class(),
			"surfaces": surfaces,
		}
		if mesh is ArrayMesh:
			entry["mesh"]["lightmap_size_hint"] = [
				mesh.lightmap_size_hint.x,
				mesh.lightmap_size_hint.y,
			]
	elif node is MultiMeshInstance3D and node.multimesh != null:
		var multi_instance: MultiMeshInstance3D = node
		var multi: MultiMesh = multi_instance.multimesh
		var mesh: Mesh = multi.mesh
		var surfaces := []
		if mesh != null:
			for i in mesh.get_surface_count():
				var format := 0
				if mesh is ArrayMesh:
					format = mesh.surface_get_format(i)
				var material := multi_instance.material_override
				if material == null:
					material = mesh.surface_get_material(i)
				surfaces.append({
					"index": i,
					"format": format,
					"mesh_key": _store_surface(mesh, i),
					"has_uv2": (format & Mesh.ARRAY_FORMAT_TEX_UV2) != 0,
					"material": material.resource_path if material != null else "",
					"material_name": material.resource_name if material != null else "",
					"material_key": _store_material(material),
					"embedded_material": "",
				})
		## In Godot 4.7, get_instance_transform() returns identity for binary
		## .multimesh resources even though their packed buffer is intact. Read
		## the documented 3x4 transform rows directly so every authored leaf and
		## bush card keeps its scale and offset.
		var transforms := []
		var stride := 12
		if multi.use_colors:
			stride += 4
		if multi.use_custom_data:
			stride += 4
		var buffer := multi.buffer
		if buffer.size() != multi.instance_count * stride:
			push_error("invalid MultiMesh buffer for %s" % multi.resource_path)
		else:
			for i in multi.instance_count:
				var offset := i * stride
				transforms.append({
					"basis": [
						buffer[offset], buffer[offset + 1], buffer[offset + 2],
						buffer[offset + 4], buffer[offset + 5], buffer[offset + 6],
						buffer[offset + 8], buffer[offset + 9], buffer[offset + 10],
					],
					"origin": [
						buffer[offset + 3], buffer[offset + 7], buffer[offset + 11],
					],
				})
		entry["multimesh"] = {
			"resource": multi.resource_path,
			"instance_count": multi.instance_count,
			"visible_instance_count": multi.visible_instance_count,
			"transform_format": multi.transform_format,
			"use_colors": multi.use_colors,
			"use_custom_data": multi.use_custom_data,
			"transforms": transforms,
			"quad_size": (
				[multi.mesh.size.x, multi.mesh.size.y]
				if multi.mesh is QuadMesh else []
			),
			"mesh": {
				"resource": mesh.resource_path if mesh != null else "",
				"name": mesh.resource_name if mesh != null else "",
				"class": mesh.get_class() if mesh != null else "",
				"surfaces": surfaces,
			},
		}
	return entry

## Writes a surface's geometry into the binary payload once and returns its
## key. Instances that share a mesh share the vertices; the atlas rect and
## slice are per-instance and come from the LightmapGI user data.
func _store_surface(mesh: Mesh, index: int) -> String:
	var key := "%s::%s#%d" % [mesh.resource_path, mesh.resource_name, index]
	if _mesh_file == null or _mesh_keys.has(key):
		return key
	_mesh_keys[key] = true
	var arrays := mesh.surface_get_arrays(index)
	var positions := PackedVector3Array()
	var normals := PackedVector3Array()
	var uv := PackedVector2Array()
	var uv2 := PackedVector2Array()
	var indices := PackedInt32Array()
	if arrays[Mesh.ARRAY_VERTEX] != null:
		positions = arrays[Mesh.ARRAY_VERTEX]
	if arrays[Mesh.ARRAY_NORMAL] != null:
		normals = arrays[Mesh.ARRAY_NORMAL]
	if arrays[Mesh.ARRAY_TEX_UV] != null:
		uv = arrays[Mesh.ARRAY_TEX_UV]
	if arrays[Mesh.ARRAY_TEX_UV2] != null:
		uv2 = arrays[Mesh.ARRAY_TEX_UV2]
	if arrays[Mesh.ARRAY_INDEX] != null:
		indices = arrays[Mesh.ARRAY_INDEX]
	var offset := _mesh_file.get_position()
	_mesh_file.store_buffer(positions.to_byte_array())
	_mesh_file.store_buffer(normals.to_byte_array())
	_mesh_file.store_buffer(uv.to_byte_array())
	_mesh_file.store_buffer(uv2.to_byte_array())
	_mesh_file.store_buffer(indices.to_byte_array())
	_meshes.append({
		"key": key,
		"resource": mesh.resource_path,
		"name": mesh.resource_name,
		"class": mesh.get_class(),
		"surface": index,
		"offset": offset,
		"vertex_count": positions.size(),
		"normal_count": normals.size(),
		"uv_count": uv.size(),
		"uv2_count": uv2.size(),
		"index_count": indices.size(),
	})
	return key

## Resolves where each lightmapped instance sits in the atlas: the raw user
## data addresses nodes relative to the LightmapGI node.
## Records a surface material once and returns its key. The atlas only
## carries the light, so a prop still needs its authored albedo and texture
## to be rebuilt, and the hangar keeps one material per batched surface
## rather than the four the port guessed from an .mtl.
func _store_material(material: Material) -> String:
	if material == null:
		return ""
	var key := material.resource_path
	if key.is_empty():
		key = "%s::%d" % [material.get_class(), material.get_instance_id()]
	if not _material_keys.has(key):
		_material_keys[key] = true
		_materials.append({
			"key": key,
			"class": material.get_class(),
			"resource": material.resource_path,
			"name": material.resource_name,
			"properties": _properties(material),
		})
	return key

func _lightmap_users(gi: LightmapGI, root: Node) -> Array:
	var out := []
	var data = gi.light_data.get("user_data")
	if data == null:
		return out
	for i in range(0, data.size(), 4):
		var node_path: NodePath = data[i]
		var rect: Rect2 = data[i + 1]
		var target := gi.get_node_or_null(node_path)
		out.append({
			"node_path": String(node_path),
			"path": String(root.get_path_to(target)) if target != null else "",
			"rect": [rect.position.x, rect.position.y, rect.size.x, rect.size.y],
			"slice": int(data[i + 2]),
			"sub": int(data[i + 3]),
		})
	return out

func _textures(data: LightmapGIData) -> Array:
	var out := []
	var textures = data.get("lightmap_textures")
	if textures == null:
		return out
	for texture in textures:
		if texture == null:
			continue
		var entry := {
			"resource": texture.resource_path,
			"class": texture.get_class(),
			"width": texture.get_width(),
			"height": texture.get_height(),
			"layers": 1,
		}
		if texture.has_method("get_layers"):
			entry["layers"] = texture.get_layers()
		out.append(entry)
	return out

func _properties(target: Object) -> Variant:
	if target == null:
		return null
	var out := {}
	for property in target.get_property_list():
		if (int(property["usage"]) & PROPERTY_USAGE_STORAGE) == 0:
			continue
		var name := String(property["name"])
		if name == "script" or name.begins_with("metadata/"):
			continue
		out[name] = _value(target.get(name))
	return out

## Basis rows, not axes. Godot keeps a Basis as three rows and writes those
## rows into the Transform3D(...) text of a .tscn, which is the layout
## Tools/port_godot_map.py parses and the port mirrors in GodotSpace.
## basis.x is the X axis, that is a column, so dumping the axes would hand
## the port a transposed matrix and quietly rotate every light and prop.
func _transform(value: Transform3D) -> Dictionary:
	return {
		"basis": [
			value.basis.x.x, value.basis.y.x, value.basis.z.x,
			value.basis.x.y, value.basis.y.y, value.basis.z.y,
			value.basis.x.z, value.basis.y.z, value.basis.z.z,
		],
		"origin": [value.origin.x, value.origin.y, value.origin.z],
	}

func _value(value: Variant) -> Variant:
	match typeof(value):
		TYPE_COLOR:
			return [value.r, value.g, value.b, value.a]
		TYPE_VECTOR2, TYPE_VECTOR2I:
			return [value.x, value.y]
		TYPE_VECTOR3, TYPE_VECTOR3I:
			return [value.x, value.y, value.z]
		TYPE_VECTOR4, TYPE_VECTOR4I, TYPE_QUATERNION:
			return [value.x, value.y, value.z, value.w]
		TYPE_PLANE:
			return [value.normal.x, value.normal.y, value.normal.z, value.d]
		TYPE_RECT2, TYPE_RECT2I:
			return [value.position.x, value.position.y, value.size.x, value.size.y]
		TYPE_AABB:
			return [
				value.position.x, value.position.y, value.position.z,
				value.size.x, value.size.y, value.size.z,
			]
		TYPE_BASIS:
			## Rows, for the same reason as _transform.
			return [
				value.x.x, value.y.x, value.z.x,
				value.x.y, value.y.y, value.z.y,
				value.x.z, value.y.z, value.z.z,
			]
		TYPE_TRANSFORM3D:
			return _transform(value)
		TYPE_TRANSFORM2D:
			return [value.x.x, value.x.y, value.y.x, value.y.y, value.origin.x, value.origin.y]
		TYPE_NODE_PATH, TYPE_STRING_NAME:
			return String(value)
		TYPE_OBJECT:
			if value == null:
				return null
			if value is Resource:
				return {
					"resource": value.resource_path,
					"class": value.get_class(),
					"name": value.resource_name,
				}
			return {"class": value.get_class()}
		TYPE_ARRAY:
			var items := []
			for item in value:
				items.append(_value(item))
			return items
		TYPE_DICTIONARY:
			var dict := {}
			for dict_key in value:
				dict[String(dict_key)] = _value(value[dict_key])
			return dict
		TYPE_PACKED_BYTE_ARRAY:
			return {"bytes": value.size()}
		TYPE_PACKED_INT32_ARRAY, TYPE_PACKED_INT64_ARRAY:
			return Array(value)
		TYPE_PACKED_FLOAT32_ARRAY, TYPE_PACKED_FLOAT64_ARRAY:
			return Array(value)
		TYPE_PACKED_VECTOR2_ARRAY, TYPE_PACKED_VECTOR3_ARRAY, TYPE_PACKED_COLOR_ARRAY:
			var packed := []
			for item in value:
				packed.append(_value(item))
			return packed
		_:
			return value
