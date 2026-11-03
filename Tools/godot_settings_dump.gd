extends SceneTree
## Dumps the source geometry of the settings screen.
##
## ui_settings.tscn authors its offsets, but the three option templates are
## duplicated at runtime into box containers, the tab strip is driven by
## tab_container_custom.gd and the check box falls back to Godot's built-in
## theme icons, which the port has no copy of. This tool instantiates the
## scene headless, writes the live rects and styles and saves the two check box
## glyphs as PNG so nothing has to be measured by eye or redrawn.
## Tools/port_godot_settings.py copies it into the source project, runs it and
## turns the JSON into the port's atlas regions, captions and geometry
## contract.

const THEME := "res://assets/fonts/ui_theme_blitz.tres"
const SCENE := "res://assets/gfx/ui/ui_settings.tscn"
const TYPES := ["Button", "ButtonSmallGeneric", "Label", "LabelBold"]
const STATES := ["normal", "pressed", "hover", "disabled", "focus"]
const BOX_CONTAINERS := ["BoxContainer", "VBoxContainer", "HBoxContainer", "GridContainer"]
## Everything the port draws. The debug tab is QA only and the controls tab is
## a later milestone, so both stay out of the walk.
const SUBTREES := [
	"blur",
	"btn_menu",
	"panel_main_background",
	"tabs/main",
	"tabs/matchmaker",
	"tabs/gameplay",
	"HBoxContainer",
	"tank_name",
]
const CHECKBOX := "tabs/main/ScrollContainer/VBoxContainer/templates/boolean_template/checkbox"
const SLIDER := "tabs/main/ScrollContainer/VBoxContainer/templates/slider_template/slider"
const CHECKBOX_ICONS := ["checked", "unchecked"]
const SETTLE_FRAMES := 4

var _out := "/tmp/blitz_settings"
var _ui: Control = null
var _frames := 0

func _initialize() -> void:
	var env := OS.get_environment("BLITZ_SETTINGS_OUT")
	if !env.is_empty():
		_out = env
	DirAccess.make_dir_recursive_absolute(_out)

	_write("theme.json", _theme())

	var packed: PackedScene = load(SCENE)
	if packed == null:
		printerr("could not load ", SCENE)
		quit(1)
		return
	_ui = packed.instantiate()
	# ui_settings.gd fills the columns with duplicated templates on _ready.
	# The port builds those rows itself, so the dump keeps the authored scene.
	_ui.set_script(null)
	root.add_child(_ui)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < SETTLE_FRAMES:
		return false

	if !is_instance_valid(_ui):
		printerr("the scene died before the layout settled")
		return true

	_write("settings.json", _settings())
	return true

func _settings() -> Dictionary:
	var data := {
		"scene": SCENE,
		"root_size": [root.size.x, root.size.y],
		"ui_size": [_ui.size.x, _ui.size.y],
		"nodes": [_node(_ui, _ui)],
		"tabs": _tabs(),
		"checkbox_icons": _checkbox_icons(),
		"slider_theme": _slider_theme(),
	}

	for path in SUBTREES:
		var node: Control = _ui.get_node_or_null(NodePath(path))
		if node == null:
			printerr("missing ", path)
			continue
		_walk(node, _ui, data["nodes"])

	return data

func _tabs() -> Dictionary:
	var tabs: TabContainer = _ui.get_node_or_null(^"tabs")
	if tabs == null:
		return {}
	var names := []
	for index in tabs.get_tab_count():
		names.append({
			"index": index,
			"name": tabs.get_tab_title(index),
			"node": tabs.get_tab_control(index).name,
		})
	return {
		"tabs_visible": tabs.tabs_visible,
		"current_tab": tabs.current_tab,
		"clip_contents": tabs.clip_contents,
		"list": names,
	}

func _checkbox_icons() -> Array:
	## The theme styles every check box state empty, so the only thing a row
	## draws is Godot's built-in glyph. The port has no copy, so save both.
	var box: CheckBox = _ui.get_node_or_null(NodePath(CHECKBOX))
	var icons := []
	if box == null:
		printerr("missing ", CHECKBOX)
		return icons
	for name in CHECKBOX_ICONS:
		var texture: Texture2D = box.get_theme_icon(name)
		if texture == null:
			printerr("the theme has no check box icon ", name)
			continue
		var file := "checkbox_%s.png" % name
		var image := texture.get_image()
		image.save_png(_out.path_join(file))
		icons.append({
			"state": name,
			"file": file,
			"size": [texture.get_width(), texture.get_height()],
			"resource_path": texture.resource_path,
		})
	return icons

func _slider_theme() -> Dictionary:
	var slider: HSlider = _ui.get_node_or_null(NodePath(SLIDER))
	if slider == null:
		printerr("missing ", SLIDER)
		return {}
	return {
		"min_value": slider.min_value,
		"max_value": slider.max_value,
		"step": slider.step,
		"rounded": slider.rounded,
		"grabber": _texture(slider.get_theme_icon("grabber")),
		"grabber_highlight": _texture(slider.get_theme_icon("grabber_highlight")),
		"styles": {
			"slider": _stylebox(slider.get_theme_stylebox("slider")),
			"grabber_area": _stylebox(slider.get_theme_stylebox("grabber_area")),
			"grabber_area_highlight": _stylebox(slider.get_theme_stylebox("grabber_area_highlight")),
		},
	}

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
		data["types"][type_name] = entry

	for type_name in BOX_CONTAINERS:
		var themed: bool = theme.has_constant("separation", type_name)
		data["separations"][type_name] = {
			"themed": themed,
			"value": theme.get_constant("separation", type_name) if themed else ThemeDB.get_default_theme().get_constant("separation", type_name),
		}

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

	if node is BoxContainer:
		entry["alignment"] = (node as BoxContainer).alignment
		entry["separation"] = node.get_theme_constant("separation")
	elif node is GridContainer:
		entry["columns"] = (node as GridContainer).columns
		entry["separation"] = node.get_theme_constant("v_separation")

	if node is Button:
		var button := node as Button
		entry["text"] = button.text
		entry["theme_type_variation"] = String(button.theme_type_variation)
		entry["toggle_mode"] = button.toggle_mode
		entry["font_size"] = button.get_theme_font_size("font_size")
		entry["font_color"] = _color(button.get_theme_color("font_color"))
		entry["style_normal"] = _stylebox(button.get_theme_stylebox("normal"))
		entry["style_pressed"] = _stylebox(button.get_theme_stylebox("pressed"))
		entry["icon"] = _texture(button.icon)
		entry["icon_max_width"] = button.get_theme_constant("icon_max_width")
		if button.get_script() != null:
			entry["snd_var"] = button.get("snd_var")
			entry["snd_vol"] = button.get("snd_vol")
			entry["toggle_sound"] = button.get("toggle_sound")
	elif node is Label:
		var label := node as Label
		entry["text"] = label.text
		entry["theme_type_variation"] = String(label.theme_type_variation)
		entry["font_size"] = label.get_theme_font_size("font_size")
		entry["font_color"] = _color(label.get_theme_color("font_color"))
		entry["horizontal_alignment"] = label.horizontal_alignment
		entry["vertical_alignment"] = label.vertical_alignment
	elif node is NinePatchRect:
		var patch := node as NinePatchRect
		entry["texture"] = _texture(patch.texture)
		entry["region_rect"] = _rect(patch.region_rect)
		entry["patch_margins"] = [
			patch.patch_margin_left,
			patch.patch_margin_top,
			patch.patch_margin_right,
			patch.patch_margin_bottom,
		]
		entry["axis_stretch"] = [patch.axis_stretch_horizontal, patch.axis_stretch_vertical]
	elif node is TextureRect:
		var rect := node as TextureRect
		entry["texture"] = _texture(rect.texture)
		entry["stretch_mode"] = rect.stretch_mode
		entry["expand_mode"] = rect.expand_mode
	elif node is ColorRect:
		entry["color"] = _color((node as ColorRect).color)

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

func _relative_rect(node: Control, origin: Control) -> Array:
	var rect := node.get_global_rect()
	var base := origin.get_global_rect().position
	return [rect.position.x - base.x, rect.position.y - base.y, rect.size.x, rect.size.y]

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
