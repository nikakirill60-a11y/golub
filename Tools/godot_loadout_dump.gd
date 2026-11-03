extends SceneTree
## Dumps the source geometry of the hangar equipment/consumables selection screen.
##
## ui_equipment_selection_screen.tscn lays its catalogue out with a
## ScrollContainer + GridContainer, so the real cell pitch depends on the
## runtime UI size and on the container separation rather than on authored
## offsets. The port guessed those numbers and the price labels ended up on top
## of the next row. This tool instantiates ui_hangar.tscn at the runtime UI
## size, fills both catalogue grids with real ui_selectable_list_entry
## instances and writes the exact global rects, so the port can copy them.

const SCENE := "res://assets/gfx/ui/hangar/ui_hangar.tscn"
const ENTRY := "res://assets/gfx/ui/hangar/ui_selectable_list_entry.tscn"
const CONSUMABLE_ICON := "res://assets/gfx/ui/battle/ui_consumable_icon.tscn"
const EQUIPMENT_PATH := "screens/tank_equipment"
const TABS := ["screens/consumables", "screens/shells", "screens/equipment"]
const CATALOGUES := ["screens/consumables", "screens/equipment"]
const SAMPLE_ENTRIES := 12
const SETTLE_FRAMES := 6

var _out := "/tmp/blitz_loadout"
var _ui: Control = null
var _equipment: Control = null
var _frames := 0
var _tab := 0
var _dumped := {}

func _initialize() -> void:
	var env := OS.get_environment("BLITZ_LOADOUT_OUT")
	if !env.is_empty():
		_out = env
	DirAccess.make_dir_recursive_absolute(_out)

	var packed: PackedScene = load(SCENE)
	if packed == null:
		printerr("could not load ", SCENE)
		quit(1)
		return
	_ui = packed.instantiate()
	root.add_child(_ui)

	_equipment = _ui.get_node_or_null(EQUIPMENT_PATH)
	if _equipment == null:
		printerr("missing ", EQUIPMENT_PATH)
		quit(1)
		return
	_equipment.visible = true

	var entry_scene: PackedScene = load(ENTRY)
	for path in CATALOGUES:
		var screen: Control = _equipment.get_node_or_null(path)
		if screen == null:
			printerr("missing ", path)
			continue
		screen.visible = true
		var grid: Control = screen.get_node_or_null("ScrollContainer/elements")
		if grid == null:
			printerr("missing grid under ", path)
			continue
		for i in SAMPLE_ENTRIES:
			var entry: Control = entry_scene.instantiate()
			entry.name = "sample_%d" % i
			grid.add_child(entry)

	# Containers only lay their children out while they are visible in the tree,
	# and both the hangar screen stack and the equipment screen start hidden.
	var outer: TabContainer = _ui.get_node_or_null("screens")
	if outer != null:
		outer.current_tab = outer.get_tab_idx_from_control(_equipment)
	var walk_up: Node = _equipment
	while walk_up != null and walk_up is CanvasItem:
		(walk_up as CanvasItem).visible = true
		walk_up = walk_up.get_parent()

	var tabs: TabContainer = _equipment.get_node_or_null("screens")
	if tabs != null:
		tabs.current_tab = 0

func _show_chain() -> void:
	var walk_up: Node = _equipment
	while walk_up != null:
		if walk_up is CanvasItem:
			(walk_up as CanvasItem).visible = true
		elif walk_up is Window:
			(walk_up as Window).visible = true
		walk_up = walk_up.get_parent()

func _chain() -> Array:
	var out := []
	var walk_up: Node = _equipment
	while walk_up != null:
		var shown: Variant = null
		if walk_up is CanvasItem:
			shown = (walk_up as CanvasItem).visible
		elif walk_up is Window:
			shown = (walk_up as Window).visible
		out.append([String(walk_up.name), walk_up.get_class(), shown])
		walk_up = walk_up.get_parent()
	return out

func _process(_delta: float) -> bool:
	# The hangar script keeps re-hiding screens, so the chain is forced open
	# again on every frame until the layout has settled.
	_show_chain()
	_frames += 1
	if _frames < SETTLE_FRAMES:
		return false
	if !is_instance_valid(_equipment):
		printerr("equipment screen died before the layout settled")
		return true

	# Only the visible tab is laid out, so each one is measured in its own turn.
	_dumped[String(TABS[_tab]).get_slice("/", 1)] = _tab_dump(TABS[_tab])
	_tab += 1
	if _tab < TABS.size():
		var tabs: TabContainer = _equipment.get_node_or_null("screens")
		if tabs != null:
			tabs.current_tab = _tab
		_frames = 0
		return false

	_write("loadout.json", _dump())
	return true

func _tab_dump(path: String) -> Array:
	var screen: Control = _equipment.get_node_or_null(path)
	if screen == null:
		printerr("missing tab ", path)
		return []
	var nodes := []
	_walk(screen, _equipment, nodes)
	return nodes

func _dump() -> Dictionary:
	var data := {
		"scene": SCENE,
		"root_size": [root.size.x, root.size.y],
		"ui_size": [_ui.size.x, _ui.size.y],
		"equipment_rect": _global_rect(_equipment),
		"sample_entries": SAMPLE_ENTRIES,
		"tabs": _dumped,
		"chain": _chain(),
		"fonts": _fonts(),
		"consumable_icon": _consumable_icon(),
	}
	return data

func _consumable_icon() -> Array:
	var packed: PackedScene = load(CONSUMABLE_ICON)
	if packed == null:
		return []
	var instance: Control = packed.instantiate()
	_ui.add_child(instance)
	var nodes := []
	_walk(instance, instance, nodes)
	instance.queue_free()
	return nodes

func _fonts() -> Dictionary:
	var label := Label.new()
	_ui.add_child(label)
	var data := {}
	for size in [21, 22, 25, 28, 31, 33, 60]:
		var font: Font = label.get_theme_font("font")
		if font == null:
			continue
		var measured := font.get_string_size("35 000", HORIZONTAL_ALIGNMENT_LEFT, -1, size)
		data[str(size)] = {
			"ascent": font.get_ascent(size),
			"descent": font.get_descent(size),
			"height": font.get_height(size),
			"sample": [measured.x, measured.y],
		}
	label.queue_free()
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
		"visible_in_tree": node.is_visible_in_tree(),
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
	if node is Label:
		var label := node as Label
		entry["text"] = label.text
		entry["font_size"] = label.get_theme_font_size("font_size")
		entry["font_color"] = _color(label.get_theme_color("font_color"))
		entry["horizontal_alignment"] = label.horizontal_alignment
		entry["vertical_alignment"] = label.vertical_alignment
	elif node is Button:
		var button := node as Button
		entry["text"] = button.text
		entry["theme_type_variation"] = String(button.theme_type_variation)
		entry["font_size"] = button.get_theme_font_size("font_size")
	elif node is ColorRect:
		entry["color"] = _color((node as ColorRect).color)
	elif node is TextureRect:
		var rect := node as TextureRect
		entry["texture"] = _texture(rect.texture)
		entry["stretch_mode"] = rect.stretch_mode
		entry["expand_mode"] = rect.expand_mode
	elif node is NinePatchRect:
		var patch := node as NinePatchRect
		entry["texture"] = _texture(patch.texture)
		entry["patch_margins"] = [
			patch.patch_margin_left,
			patch.patch_margin_top,
			patch.patch_margin_right,
			patch.patch_margin_bottom,
		]
	elif node is ScrollContainer:
		var scroll := node as ScrollContainer
		entry["horizontal_scroll_mode"] = scroll.horizontal_scroll_mode
		entry["vertical_scroll_mode"] = scroll.vertical_scroll_mode
	elif node is GridContainer:
		var grid := node as GridContainer
		entry["columns"] = grid.columns
		entry["h_separation"] = grid.get_theme_constant("h_separation")
		entry["v_separation"] = grid.get_theme_constant("v_separation")
	return entry

func _relative_rect(node: Control, origin: Control) -> Array:
	var rect := node.get_global_rect()
	var base := origin.get_global_rect().position
	return [rect.position.x - base.x, rect.position.y - base.y, rect.size.x, rect.size.y]

func _global_rect(node: Control) -> Array:
	if node == null:
		return []
	var rect := node.get_global_rect()
	return [rect.position.x, rect.position.y, rect.size.x, rect.size.y]

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
		entry["region"] = [atlas.region.position.x, atlas.region.position.y, atlas.region.size.x, atlas.region.size.y]
		entry["atlas"] = _texture(atlas.atlas)
	return entry

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
