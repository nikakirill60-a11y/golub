extends SceneTree
## Dumps the source geometry and theme metrics of the hangar burger menu.
##
## Two things cannot be read out of the source text the way the rest of the
## hangar was: ui_theme_blitz.tres reaches its Button and ButtonRed styles
## through compressed .stylebox resources, and every Label, Button and TextEdit
## inside screens/main/menu is laid out by a VBoxContainer, so its rect depends
## on font metrics rather than on authored offsets. This tool loads the live
## theme and instantiates ui_hangar.tscn at the runtime UI size instead, then
## writes the exact styles, rects and animation keys.
## Tools/port_godot_hangar_menu.py copies it into the source project, runs it
## headless and turns the JSON into the port's atlas regions and geometry
## contracts.

const THEME := "res://assets/fonts/ui_theme_blitz.tres"
const SCENE := "res://assets/gfx/ui/hangar/ui_hangar.tscn"
const TYPES := ["Button", "ButtonRed", "ButtonFlat", "ButtonSmallGeneric", "TextEdit", "Label"]
const STATES := ["normal", "pressed", "hover", "disabled", "focus"]
const BOX_CONTAINERS := ["BoxContainer", "VBoxContainer", "HBoxContainer"]
const MENU_PATH := "screens/main/menu"
const BUTTON_PATH := "screens/main/btn_menu"
const ANIMATION_PLAYER := "AnimationPlayer_menu"
const SETTLE_FRAMES := 4

var _out := "/tmp/blitz_hangar_menu"
var _hangar: Control = null
var _frames := 0

func _initialize() -> void:
	var env := OS.get_environment("BLITZ_MENU_OUT")
	if !env.is_empty():
		_out = env
	DirAccess.make_dir_recursive_absolute(_out)

	_write("theme.json", _theme())

	var packed: PackedScene = load(SCENE)
	if packed == null:
		printerr("could not load ", SCENE)
		quit(1)
		return
	_hangar = packed.instantiate()
	root.add_child(_hangar)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < SETTLE_FRAMES:
		return false

	if !is_instance_valid(_hangar):
		printerr("hangar instance died before the layout settled")
		return true

	_write("menu.json", _menu())
	return true

func _theme() -> Dictionary:
	var theme: Theme = load(THEME)
	if theme == null:
		printerr("could not load ", THEME)
		return {}

	var data := {
		"theme": THEME,
		"default_font_size": theme.default_font_size,
		"types": {},
		"separations": {},
	}

	for type_name in TYPES:
		var entry := {"styles": {}}
		for state in STATES:
			if theme.has_stylebox(state, type_name):
				entry["styles"][state] = _stylebox(theme.get_stylebox(state, type_name))
		if theme.has_font_size("font_size", type_name):
			entry["font_size"] = theme.get_font_size("font_size", type_name)
		if theme.has_color("font_color", type_name):
			entry["font_color"] = _color(theme.get_color("font_color", type_name))
		if theme.has_constant("icon_max_width", type_name):
			entry["icon_max_width"] = theme.get_constant("icon_max_width", type_name)
		if theme.has_constant("h_separation", type_name):
			entry["h_separation"] = theme.get_constant("h_separation", type_name)
		data["types"][type_name] = entry

	for type_name in BOX_CONTAINERS:
		var themed: bool = theme.has_constant("separation", type_name)
		data["separations"][type_name] = {
			"themed": themed,
			"value": theme.get_constant("separation", type_name) if themed else ThemeDB.get_default_theme().get_constant("separation", type_name),
		}

	return data

func _menu() -> Dictionary:
	var menu: Control = _hangar.get_node_or_null(MENU_PATH)
	if menu == null:
		printerr("missing ", MENU_PATH)
		return {}

	var data := {
		"scene": SCENE,
		"root_size": [root.size.x, root.size.y],
		"ui_size": [_hangar.size.x, _hangar.size.y],
		"screen_main_rect": _global_rect(menu.get_parent() as Control),
		"nodes": [],
		"animation": _animation(),
	}

	var button: Control = _hangar.get_node_or_null(BUTTON_PATH)
	if button != null:
		data["nodes"].append(_node(button, menu.get_parent() as Control))

	_walk(menu, menu.get_parent() as Control, data["nodes"])
	return data

func _walk(node: Node, origin: Control, into: Array) -> void:
	if node is Control:
		into.append(_node(node as Control, origin))
	for child in node.get_children():
		_walk(child, origin, into)

func _node(node: Control, origin: Control) -> Dictionary:
	var entry := {
		"path": String(origin.get_path_to(node)),
		"name": node.name,
		"class": node.get_class(),
		"visible": node.visible,
		"position": [node.position.x, node.position.y],
		"size": [node.size.x, node.size.y],
		"scale": [node.scale.x, node.scale.y],
		"pivot_offset": [node.pivot_offset.x, node.pivot_offset.y],
		"rect": _relative_rect(node, origin),
		"anchors": [node.anchor_left, node.anchor_top, node.anchor_right, node.anchor_bottom],
		"offsets": [node.offset_left, node.offset_top, node.offset_right, node.offset_bottom],
		"custom_minimum_size": [node.custom_minimum_size.x, node.custom_minimum_size.y],
		"combined_minimum_size": [node.get_combined_minimum_size().x, node.get_combined_minimum_size().y],
		"mouse_filter": node.mouse_filter,
		"size_flags": [node.size_flags_horizontal, node.size_flags_vertical],
	}

	if node is Button:
		var button := node as Button
		entry["text"] = button.text
		entry["theme_type_variation"] = String(button.theme_type_variation)
		entry["toggle_mode"] = button.toggle_mode
		entry["font_size"] = button.get_theme_font_size("font_size")
		entry["font_color"] = _color(button.get_theme_color("font_color"))
		entry["style_normal"] = _stylebox(button.get_theme_stylebox("normal"))
		entry["icon"] = _texture(button.icon)
		entry["icon_max_width"] = button.get_theme_constant("icon_max_width")
		if button.get_script() != null:
			entry["snd_var"] = button.get("snd_var")
			entry["snd_vol"] = button.get("snd_vol")
			entry["toggle_sound"] = button.get("toggle_sound")
	elif node is Label:
		var label := node as Label
		entry["text"] = label.text
		entry["font_size"] = label.get_theme_font_size("font_size")
		entry["font_color"] = _color(label.get_theme_color("font_color"))
		entry["horizontal_alignment"] = label.horizontal_alignment
		entry["vertical_alignment"] = label.vertical_alignment
	elif node is TextEdit:
		var edit := node as TextEdit
		entry["text"] = edit.text
		entry["placeholder_text"] = edit.placeholder_text
		entry["font_size"] = edit.get_theme_font_size("font_size")
		entry["font_color"] = _color(edit.get_theme_color("font_color"))
		entry["style_normal"] = _stylebox(edit.get_theme_stylebox("normal"))
	elif node is ColorRect:
		entry["color"] = _color((node as ColorRect).color)
	elif node is TextureRect:
		var rect := node as TextureRect
		entry["texture"] = _texture(rect.texture)
		entry["stretch_mode"] = rect.stretch_mode
		entry["expand_mode"] = rect.expand_mode

	var material := node.material
	if material is ShaderMaterial:
		var shader_material := material as ShaderMaterial
		var shader := shader_material.shader
		var parameters := {}
		if shader != null:
			for info in shader.get_shader_uniform_list():
				var value = shader_material.get_shader_parameter(info["name"])
				if value is Color:
					value = _color(value)
				elif value is Object:
					value = str(value)
				parameters[info["name"]] = value
		entry["shader"] = {
			"path": shader.resource_path if shader != null else "",
			"parameters": parameters,
		}

	return entry

func _animation() -> Dictionary:
	var player: AnimationPlayer = _hangar.get_node_or_null(ANIMATION_PLAYER)
	if player == null:
		return {}

	var data := {"animations": {}}
	for name in player.get_animation_list():
		var animation := player.get_animation(name)
		var tracks := []
		for track in animation.get_track_count():
			var keys := []
			for key in animation.track_get_key_count(track):
				var value = animation.track_get_key_value(track, key)
				if value is Vector2:
					value = [value.x, value.y]
				elif value is Color:
					value = _color(value)
				elif value is Object:
					value = str(value)
				keys.append({
					"time": animation.track_get_key_time(track, key),
					"transition": animation.track_get_key_transition(track, key),
					"value": value,
				})
			tracks.append({
				"path": String(animation.track_get_path(track)),
				"type": animation.track_get_type(track),
				"interpolation": animation.track_get_interpolation_type(track),
				"keys": keys,
			})
		data["animations"][name] = {
			"length": animation.length,
			"loop_mode": animation.loop_mode,
			"step": animation.step,
			"tracks": tracks,
		}
	return data

func _relative_rect(node: Control, origin: Control) -> Array:
	var rect := node.get_global_rect()
	var base := origin.get_global_rect().position
	return [rect.position.x - base.x, rect.position.y - base.y, rect.size.x, rect.size.y]

func _global_rect(node: Control) -> Array:
	if node == null:
		return []
	var rect := node.get_global_rect()
	return [rect.position.x, rect.position.y, rect.size.x, rect.size.y]

func _stylebox(box: StyleBox) -> Dictionary:
	if box == null:
		return {}

	var entry := {
		"class": box.get_class(),
		"resource_path": box.resource_path,
		"content_margins": [
			box.content_margin_left,
			box.content_margin_top,
			box.content_margin_right,
			box.content_margin_bottom,
		],
	}

	if box is StyleBoxTexture:
		var textured := box as StyleBoxTexture
		entry["texture_margins"] = [
			textured.texture_margin_left,
			textured.texture_margin_top,
			textured.texture_margin_right,
			textured.texture_margin_bottom,
		]
		entry["expand_margins"] = [
			textured.expand_margin_left,
			textured.expand_margin_top,
			textured.expand_margin_right,
			textured.expand_margin_bottom,
		]
		entry["region_rect"] = _rect(textured.region_rect)
		entry["modulate_color"] = _color(textured.modulate_color)
		entry["axis_stretch"] = [textured.axis_stretch_horizontal, textured.axis_stretch_vertical]
		entry["texture"] = _texture(textured.texture)
	elif box is StyleBoxFlat:
		var flat := box as StyleBoxFlat
		entry["bg_color"] = _color(flat.bg_color)
		entry["border_color"] = _color(flat.border_color)
		entry["border_width"] = [
			flat.border_width_left,
			flat.border_width_top,
			flat.border_width_right,
			flat.border_width_bottom,
		]
		entry["corner_radius"] = [
			flat.corner_radius_top_left,
			flat.corner_radius_top_right,
			flat.corner_radius_bottom_right,
			flat.corner_radius_bottom_left,
		]

	return entry

func _texture(texture: Texture2D) -> Dictionary:
	if texture == null:
		return {}

	var entry := {
		"class": texture.get_class(),
		"resource_path": texture.resource_path,
		"size": [texture.get_width(), texture.get_height()],
	}

	if texture is AtlasTexture:
		var atlas := texture as AtlasTexture
		entry["region"] = _rect(atlas.region)
		entry["margin"] = _rect(atlas.margin)
		entry["filter_clip"] = atlas.filter_clip
		entry["atlas"] = _texture(atlas.atlas)

	return entry

func _rect(rect: Rect2) -> Array:
	return [rect.position.x, rect.position.y, rect.size.x, rect.size.y]

func _color(color: Color) -> Array:
	return [color.r, color.g, color.b, color.a]

func _write(name: String, data: Dictionary) -> void:
	var text := JSON.stringify(data, "\t", true, true)
	var path := _out.path_join(name)
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		printerr("could not write ", path)
		return
	file.store_string(text + "\n")
	file.close()
	print("wrote ", path, " (", text.length(), " chars)")
