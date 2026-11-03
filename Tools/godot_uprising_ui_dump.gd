extends SceneTree
# Run ONLY in the isolated temporary project made by port_uprising.py.
# No source game's autoloads or account/save services are started.
func _initialize():
 var folder = OS.get_environment("BLITZ_UPRISING_UI_OUT")
 var normal = load("res://assets/gfx/ui/common/style_button_generic.stylebox")
 var selected = load("res://assets/gfx/ui/battle/uprising_selected_style.tres")
 for entry in [["card", normal], ["selected", selected]]:
  var style = entry[1]
  var texture = style.texture
  var image = texture.get_image()
  if texture is AtlasTexture:
   image = texture.atlas.get_image().get_region(texture.region)
  var result = image.save_png(folder + "/" + entry[0] + ".png")
  if result != OK:
   push_error("UI style export failed: " + entry[0])
   quit(1)
   return
 var manifest = {}
 for entry in [["card", normal], ["selected", selected]]:
  var style = entry[1]
  manifest[entry[0]] = {"borders": [style.texture_margin_left, style.texture_margin_top, style.texture_margin_right, style.texture_margin_bottom]}
 var theme = load("res://assets/fonts/ui_theme_blitz.tres")
 var default_font = theme.default_font
 if default_font is FontFile:
  var font_file = FileAccess.open(folder + "/../menu_default.ttf", FileAccess.WRITE)
  font_file.store_buffer(default_font.data)
  font_file.close()
 else:
  push_error("Source default font is not exportable")
  quit(1)
  return
 manifest["close"] = {}
 for state in ["normal", "hover", "pressed", "focus"]:
  var box = theme.get_stylebox(state, "Button")
  if box is StyleBoxTexture:
   var texture = box.texture
   var image = texture.get_image()
   if texture is AtlasTexture:
    image = texture.atlas.get_image().get_region(texture.region)
   image.save_png(folder + "/menu_close_" + state + ".png")
   manifest["close"][state] = {"type":"texture", "borders":[box.texture_margin_left,box.texture_margin_top,box.texture_margin_right,box.texture_margin_bottom]}
  else:
   push_error("Unexpected source menu button style")
   quit(1)
   return
 var file = FileAccess.open(folder + "/style_manifest.json", FileAccess.WRITE)
 file.store_string(JSON.stringify(manifest, "\t") + "\n")
 print("UPRISING_UI_STYLES_EXPORTED")
 quit(0)
