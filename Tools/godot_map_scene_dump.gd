extends SceneTree
## Dumps everything a Blitz Legacy Godot battle map draws, as Godot renders it.
##
## The older map tools read `_<map>.tscn` as text and rebuilt what they could
## understand of it. This one instantiates the scene inside the running
## engine instead, lets every @tool script, the hterrain plugin and the
## LightmapGI finish setting it up, and then records the live render state:
##
## * every GeometryInstance3D (meshes, multimeshes, sprites) with its global
##   transform, visibility, shadow/GI flags, the material Godot resolves for
##   each surface and its LightmapGI atlas rect and slice;
## * the meshes in their own space, every surface array included (UV2 is the
##   lightmap unwrap), in one binary blob;
## * every material: its class, its shader file and all its parameters;
## * the gameplay owner of each instance (destructible, tree, vegetation,
##   static) and the role the instance plays for it;
## * the WorldEnvironment, the lights, the LightmapGI slices (saved as EXR)
##   and the hterrain: its transform, scale, shader parameters and maps.
##
## Tools/port_godot_map_scene.py copies it into the Godot project, runs it and
## converts the result into the Unity resources of the map.
##
## Environment: BLITZ_MAP_SCENE (res:// path), BLITZ_SCENE_OUT (directory).

const MESH_MAGIC := "BLGS"
const MESH_VERSION := 1

# Surface stream flags of the binary blob.
const HAS_NORMAL := 1
const HAS_TANGENT := 2
const HAS_COLOR := 4
const HAS_UV := 8
const HAS_UV2 := 16
const HAS_INDEX := 32

var _out := ""
var _blob: FileAccess = null
var _mesh_keys := {}
var _meshes: Array = []
var _material_keys := {}
var _materials: Array = []
var _instances: Array = []
var _unsupported := {}
var _lightmap_users := {}

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	var scene_path := OS.get_environment("BLITZ_MAP_SCENE")
	_out = OS.get_environment("BLITZ_SCENE_OUT")
	if scene_path.is_empty() or _out.is_empty():
		push_error("BLITZ_MAP_SCENE and BLITZ_SCENE_OUT are required")
		quit(2)
		return
	DirAccess.make_dir_recursive_absolute(_out)
	var packed: PackedScene = load(scene_path)
	if packed == null:
		push_error("missing scene %s" % scene_path)
		quit(2)
		return
	var map := packed.instantiate()
	root.add_child(map)
	# @tool scripts build their meshes in _ready, hterrain its chunks and
	# shader parameters a few frames later.
	for i in 10:
		await process_frame

	var payload := {
		"format": "BLITZ_GODOT_SCENE",
		"version": 1,
		"godot": Engine.get_version_info()["string"],
		"scene": scene_path,
		"settings": _settings(),
		"environment": {},
		"lights": [],
		"lightmap": {},
		"terrains": [],
		"meshes": _meshes,
		"materials": _materials,
		"instances": _instances,
		"markers": [],
		"unsupported": _unsupported,
	}

	_blob = FileAccess.open(_out.path_join("meshes.bin"), FileAccess.WRITE)
	_blob.store_buffer(MESH_MAGIC.to_utf8_buffer())
	_blob.store_32(MESH_VERSION)

	for gi in _find(map, "LightmapGI"):
		payload["lightmap"] = _lightmap(gi, map)
	for env in _find(map, "WorldEnvironment"):
		payload["environment"] = _properties(env.environment)
		payload["environment"]["_path"] = String(map.get_path_to(env))
	_walk(map, map, payload)
	_blob.close()

	var file := FileAccess.open(_out.path_join("scene.json"), FileAccess.WRITE)
	file.store_string(JSON.stringify(payload, "\t", false))
	file.close()
	print("[scene-dump] %s: instances=%d meshes=%d materials=%d lights=%d terrains=%d lightmap_users=%d unsupported=%s" % [
		scene_path, _instances.size(), _meshes.size(), _materials.size(),
		payload["lights"].size(), payload["terrains"].size(),
		_lightmap_users.size(), str(_unsupported)])
	quit(0)

func _settings() -> Dictionary:
	var out := {}
	for key in [
		"rendering/renderer/rendering_method",
		"rendering/shading/overrides/force_vertex_shading",
		"rendering/shading/overrides/force_lambert_over_burley",
		"rendering/lights_and_shadows/directional_shadow/size",
		"rendering/lights_and_shadows/directional_shadow/size.mobile",
		"rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_quality",
		"rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_quality.mobile",
		"rendering/lightmapping/lightmap_gi/use_bicubic_filter",
		"rendering/anti_aliasing/quality/use_debanding",
		"rendering/textures/default_filters/anisotropic_filtering_level",
	]:
		out[key] = _value(ProjectSettings.get_setting(key))
	return out

func _find(node: Node, cls: String) -> Array:
	var out := []
	if node.is_class(cls):
		out.append(node)
	for child in node.get_children():
		out.append_array(_find(child, cls))
	return out

func _walk(node: Node, map: Node, payload: Dictionary) -> void:
	if node is Light3D:
		var light := _properties(node)
		light["_class"] = node.get_class()
		light["_path"] = String(map.get_path_to(node))
		light["_global_transform"] = _transform(node.global_transform)
		light["_visible"] = node.is_visible_in_tree()
		payload["lights"].append(light)
	elif node.get_script() != null and String(node.get_script().resource_path).ends_with("hterrain.gd"):
		payload["terrains"].append(_terrain(node, map))
	elif node is Marker3D:
		var marker := {
			"path": String(map.get_path_to(node)),
			"transform": _transform(node.global_transform),
			"script": _script_path(node),
		}
		payload["markers"].append(marker)
	elif node is GeometryInstance3D:
		_geometry(node, map)
	for child in node.get_children():
		_walk(child, map, payload)

func _script_path(node: Object) -> String:
	var script = node.get_script()
	return "" if script == null else String(script.resource_path)

## The closest ancestor that carries a gameplay script, and what this
## instance is to it (`normal`/`destroyed` for destructibles, `trunk` for a
## tree, otherwise the instance's own name).
func _owner(node: Node, map: Node) -> Dictionary:
	var cursor := node.get_parent()
	while cursor != null and cursor != map:
		var script := _script_path(cursor)
		if script.contains("map_object_") or script.contains("tank_spawner"):
			var role := String(node.name)
			if cursor.get("mesh_instance_normal") == node:
				role = "normal"
			elif cursor.get("mesh_instance_destroyed") == node:
				role = "destroyed"
			elif cursor.get("trunk") == node:
				role = "trunk"
			elif cursor.get("mesh_instance") == node:
				role = "mesh"
			return {
				"path": String(map.get_path_to(cursor)),
				"script": script,
				"scene": cursor.scene_file_path,
				"role": role,
				"relative": String(cursor.get_path_to(node)),
				# map_object_vegetation.gd measures its LOD distance from here.
				"origin": _value(cursor.global_position) if cursor is Node3D else null,
			}
		cursor = cursor.get_parent()
	return {}

func _geometry(node: GeometryInstance3D, map: Node) -> void:
	var entry := {
		"path": String(map.get_path_to(node)),
		"class": node.get_class(),
		"visible": node.is_visible_in_tree(),
		"transform": _transform(node.global_transform),
		"cast_shadow": node.cast_shadow,
		"gi_mode": node.gi_mode,
		"layers": node.layers,
		"transparency": node.transparency,
		"visibility_range_end": node.visibility_range_end,
		"sorting_offset": node.sorting_offset,
		"material_override": _store_material(node.material_override),
		"owner": _owner(node, map),
		"lightmap": null,
	}
	var user = _lightmap_users.get(entry["path"])
	if user != null:
		entry["lightmap"] = user

	if node is MeshInstance3D:
		var mesh: Mesh = node.mesh
		if mesh == null:
			return
		entry["mesh"] = _store_mesh(mesh)
		var surfaces := []
		for i in mesh.get_surface_count():
			surfaces.append(_store_material(node.get_active_material(i)))
		entry["surface_materials"] = surfaces
	elif node is MultiMeshInstance3D:
		var mm: MultiMesh = node.multimesh
		if mm == null or mm.mesh == null:
			return
		entry["mesh"] = _store_mesh(mm.mesh)
		var surfaces := []
		for i in mm.mesh.get_surface_count():
			var m: Material = node.material_override
			if m == null:
				m = mm.mesh.surface_get_material(i)
			surfaces.append(_store_material(m))
		entry["surface_materials"] = surfaces
		var items := []
		for i in mm.visible_instance_count if mm.visible_instance_count >= 0 else mm.instance_count:
			var item := {"transform": _transform(mm.get_instance_transform(i))}
			if mm.use_colors:
				item["color"] = _value(mm.get_instance_color(i))
			if mm.use_custom_data:
				item["custom"] = _value(mm.get_instance_custom_data(i))
			items.append(item)
		entry["multimesh"] = items
	elif node is SpriteBase3D:
		var sprite := {
			"pixel_size": node.pixel_size,
			"offset": _value(node.offset),
			"centered": node.centered,
			"flip_h": node.flip_h,
			"flip_v": node.flip_v,
			"axis": node.axis,
			"billboard": node.billboard,
			"modulate": _value(node.modulate),
			"shaded": node.shaded,
			"double_sided": node.double_sided,
			"transparent": node.transparent,
			"alpha_cut": node.alpha_cut,
			"alpha_scissor_threshold": node.alpha_scissor_threshold,
			"texture_filter": node.texture_filter,
			"render_priority": node.render_priority,
			"no_depth_test": node.no_depth_test,
			"fixed_size": node.fixed_size,
			"item_rect": _value(node.get_item_rect()),
		}
		if node is Sprite3D:
			sprite["texture"] = _resource_path(node.texture)
			sprite["region_enabled"] = node.region_enabled
			sprite["region_rect"] = _value(node.region_rect)
			sprite["hframes"] = node.hframes
			sprite["vframes"] = node.vframes
			sprite["frame"] = node.frame
			if node.texture != null:
				sprite["texture_size"] = _value(node.texture.get_size())
			if node.texture is AtlasTexture:
				# The tree LOD planes are regions of one shared sheet.
				var atlas: AtlasTexture = node.texture
				sprite["atlas"] = _resource_path(atlas.atlas)
				sprite["atlas_region"] = _value(atlas.region)
				sprite["atlas_size"] = _value(atlas.atlas.get_size())
		entry["sprite"] = sprite
	else:
		_unsupported[node.get_class()] = int(_unsupported.get(node.get_class(), 0)) + 1
		return
	_instances.append(entry)

func _store_mesh(mesh: Mesh) -> String:
	var key := "%s#%d" % [mesh.resource_path, mesh.get_instance_id()]
	if _mesh_keys.has(key):
		return key
	_mesh_keys[key] = true
	var surfaces := []
	for i in mesh.get_surface_count():
		var arrays := mesh.surface_get_arrays(i)
		var positions: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
		var flags := 0
		var offset := _blob.get_position()
		var count := positions.size()
		_blob.store_buffer(positions.to_byte_array())
		if arrays[Mesh.ARRAY_NORMAL] != null and arrays[Mesh.ARRAY_NORMAL].size() == count:
			flags |= HAS_NORMAL
			_blob.store_buffer(PackedVector3Array(arrays[Mesh.ARRAY_NORMAL]).to_byte_array())
		if arrays[Mesh.ARRAY_TANGENT] != null and arrays[Mesh.ARRAY_TANGENT].size() == count * 4:
			flags |= HAS_TANGENT
			_blob.store_buffer(PackedFloat32Array(arrays[Mesh.ARRAY_TANGENT]).to_byte_array())
		if arrays[Mesh.ARRAY_COLOR] != null and arrays[Mesh.ARRAY_COLOR].size() == count:
			flags |= HAS_COLOR
			_blob.store_buffer(PackedColorArray(arrays[Mesh.ARRAY_COLOR]).to_byte_array())
		if arrays[Mesh.ARRAY_TEX_UV] != null and arrays[Mesh.ARRAY_TEX_UV].size() == count:
			flags |= HAS_UV
			_blob.store_buffer(PackedVector2Array(arrays[Mesh.ARRAY_TEX_UV]).to_byte_array())
		if arrays[Mesh.ARRAY_TEX_UV2] != null and arrays[Mesh.ARRAY_TEX_UV2].size() == count:
			flags |= HAS_UV2
			_blob.store_buffer(PackedVector2Array(arrays[Mesh.ARRAY_TEX_UV2]).to_byte_array())
		var index_count := 0
		if arrays[Mesh.ARRAY_INDEX] != null and arrays[Mesh.ARRAY_INDEX].size() > 0:
			flags |= HAS_INDEX
			var indices := PackedInt32Array(arrays[Mesh.ARRAY_INDEX])
			index_count = indices.size()
			_blob.store_buffer(indices.to_byte_array())
		surfaces.append({
			"offset": offset,
			"vertex_count": count,
			"index_count": index_count,
			"flags": flags,
			"primitive": mesh.surface_get_primitive_type(i) if mesh is ArrayMesh else Mesh.PRIMITIVE_TRIANGLES,
			"material": _store_material(mesh.surface_get_material(i)),
			"aabb": _value(_surface_aabb(positions)),
		})
	var entry := {
		"key": key,
		"resource": mesh.resource_path,
		"name": mesh.resource_name,
		"class": mesh.get_class(),
		"surfaces": surfaces,
		"aabb": _value(mesh.get_aabb()),
	}
	if mesh is ArrayMesh:
		entry["lightmap_size_hint"] = _value(mesh.lightmap_size_hint)
	_meshes.append(entry)
	return key

func _surface_aabb(points: PackedVector3Array) -> AABB:
	if points.is_empty():
		return AABB()
	var box := AABB(points[0], Vector3.ZERO)
	for p in points:
		box = box.expand(p)
	return box

func _store_material(material: Material) -> String:
	if material == null:
		return ""
	var key := "%s#%d" % [material.resource_path, material.get_instance_id()]
	if _material_keys.has(key):
		return key
	_material_keys[key] = true
	var entry := {
		"key": key,
		"class": material.get_class(),
		"resource": material.resource_path,
		"name": material.resource_name,
		"render_priority": material.render_priority,
		"next_pass": _store_material(material.next_pass),
	}
	if material is ShaderMaterial:
		var shader: Shader = material.shader
		entry["shader"] = "" if shader == null else shader.resource_path
		entry["shader_code_sha256"] = "" if shader == null else shader.code.sha256_text()
		var params := {}
		if shader != null:
			for uniform in shader.get_shader_uniform_list(true):
				var name: String = uniform["name"]
				var value = material.get_shader_parameter(name)
				if value == null:
					value = RenderingServer.shader_get_parameter_default(shader.get_rid(), name)
				params[name] = _value(value)
		entry["params"] = params
	else:
		entry["properties"] = _properties(material)
	_materials.append(entry)
	return key

func _lightmap(gi: LightmapGI, map: Node) -> Dictionary:
	var data: LightmapGIData = gi.light_data
	var out := _properties(gi)
	out["_path"] = String(map.get_path_to(gi))
	if data == null:
		return out
	out["uses_spherical_harmonics"] = data.is_using_spherical_harmonics()
	var users := []
	var raw = data.get("user_data")
	if raw != null:
		for i in range(0, raw.size(), 4):
			var target := gi.get_node_or_null(raw[i])
			var rect: Rect2 = raw[i + 1]
			var user := {
				"node_path": String(raw[i]),
				"path": "" if target == null else String(map.get_path_to(target)),
				"rect": [rect.position.x, rect.position.y, rect.size.x, rect.size.y],
				"slice": int(raw[i + 2]),
				"sub_instance": int(raw[i + 3]),
			}
			users.append(user)
			if user["sub_instance"] < 0 and not user["path"].is_empty():
				_lightmap_users[user["path"]] = user
	out["users"] = users
	var slices := []
	var textures = data.get("lightmap_textures")
	if textures != null:
		for t in textures.size():
			var layered: TextureLayered = textures[t]
			if layered == null:
				continue
			for layer in layered.get_layers():
				var image := layered.get_layer_data(layer)
				if image.is_compressed():
					image.decompress()
				var file := "lightmap_%d_%d.exr" % [t, layer]
				image.save_exr(_out.path_join(file), false)
				slices.append({
					"texture": t,
					"layer": layer,
					"file": file,
					"resource": layered.resource_path,
					"width": image.get_width(),
					"height": image.get_height(),
					"format": image.get_format(),
				})
	out["slices"] = slices
	return out

func _terrain(node: Node, map: Node) -> Dictionary:
	var out := {
		"path": String(map.get_path_to(node)),
		"transform": _transform(node.global_transform),
		"map_scale": _value(node.get("map_scale")),
		"cast_shadow": node.get("cast_shadow"),
		"custom_shader": _resource_path(node.get("custom_shader")),
		"shader_params": {},
		"maps": {},
	}
	for property in node.get_property_list():
		var name: String = property["name"]
		if name.begins_with("shader_params/"):
			out["shader_params"][name.trim_prefix("shader_params/")] = _value(node.get(name))
	var data = node.get_data()
	if data != null:
		out["resolution"] = data.get_resolution()
		out["data"] = data.resource_path
		# CHANNEL_HEIGHT 0, NORMAL 1, SPLAT 2, COLOR 3, DETAIL 4, GLOBAL_ALBEDO 5.
		for channel in [[0, "height"], [1, "normal"], [2, "splat"], [3, "color"]]:
			var image: Image = data.get_image(channel[0])
			if image == null:
				# Outside the editor hterrain keeps only the GPU textures.
				var texture: Texture2D = data.get_texture(channel[0])
				if texture != null:
					image = texture.get_image()
			if image == null:
				continue
			if image.is_compressed():
				image.decompress()
			var file := "terrain_%s" % channel[1]
			if channel[0] == 0:
				# Raw float32 rows: <i width> <i height> then width*height floats.
				image.convert(Image.FORMAT_RF)
				var f := FileAccess.open(_out.path_join(file + ".bin"), FileAccess.WRITE)
				f.store_32(image.get_width())
				f.store_32(image.get_height())
				f.store_buffer(image.get_data())
				f.close()
				file += ".bin"
			else:
				image.save_png(_out.path_join(file + ".png"))
				file += ".png"
			out["maps"][channel[1]] = {
				"file": file,
				"width": image.get_width(),
				"height": image.get_height(),
				"format": image.get_format(),
			}
	return out

func _resource_path(value) -> String:
	if value is Resource:
		return String(value.resource_path)
	return ""

func _properties(target: Object) -> Dictionary:
	var out := {}
	if target == null:
		return out
	for property in target.get_property_list():
		if not (property["usage"] & PROPERTY_USAGE_STORAGE):
			continue
		var name: String = property["name"]
		if name in ["script", "resource_local_to_scene", "resource_path", "resource_name", "user_data", "light_textures", "lightmap_textures", "shadowmask_textures", "probe_data"]:
			continue
		var value = target.get(name)
		if value is Object and not value is Resource:
			continue
		out[name] = _value(value)
	return out

func _transform(value: Transform3D) -> Array:
	# Basis columns x, y, z then the origin.
	return [
		value.basis.x.x, value.basis.x.y, value.basis.x.z,
		value.basis.y.x, value.basis.y.y, value.basis.y.z,
		value.basis.z.x, value.basis.z.y, value.basis.z.z,
		value.origin.x, value.origin.y, value.origin.z,
	]

func _value(value: Variant) -> Variant:
	match typeof(value):
		TYPE_NIL, TYPE_BOOL, TYPE_INT, TYPE_FLOAT, TYPE_STRING:
			return value
		TYPE_STRING_NAME, TYPE_NODE_PATH:
			return String(value)
		TYPE_VECTOR2, TYPE_VECTOR2I:
			return [value.x, value.y]
		TYPE_VECTOR3, TYPE_VECTOR3I:
			return [value.x, value.y, value.z]
		TYPE_VECTOR4, TYPE_VECTOR4I:
			return [value.x, value.y, value.z, value.w]
		TYPE_QUATERNION:
			return [value.x, value.y, value.z, value.w]
		TYPE_COLOR:
			return [value.r, value.g, value.b, value.a]
		TYPE_RECT2, TYPE_RECT2I:
			return [value.position.x, value.position.y, value.size.x, value.size.y]
		TYPE_AABB:
			return [value.position.x, value.position.y, value.position.z, value.size.x, value.size.y, value.size.z]
		TYPE_TRANSFORM3D:
			return _transform(value)
		TYPE_BASIS:
			return [value.x.x, value.x.y, value.x.z, value.y.x, value.y.y, value.y.z, value.z.x, value.z.y, value.z.z]
		TYPE_OBJECT:
			if value == null:
				return null
			if value is Resource:
				var entry := {"class": value.get_class(), "resource": value.resource_path}
				if value is Texture2D:
					entry["size"] = [value.get_width(), value.get_height()]
					if value.resource_path.is_empty() or value.resource_path.contains("::"):
						# Embedded texture: keep its pixels next to the dump.
						var image: Image = value.get_image()
						if image != null:
							if image.is_compressed():
								image.decompress()
							var file := "embedded_%d.png" % value.get_instance_id()
							image.save_png(_out.path_join(file))
							entry["file"] = file
				return entry
			return null
		_:
			if value is Array or value is PackedFloat32Array or value is PackedInt32Array:
				var out := []
				for item in value:
					out.append(_value(item))
				return out
			if value is Dictionary:
				var out := {}
				for k in value.keys():
					out[str(k)] = _value(value[k])
				return out
			return str(value)
