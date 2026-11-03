extends SceneTree
var scenes = {}
func _initialize(): call_deferred("run")
func strip(n):
    for c in n.get_children(): strip(c)
    if n.get_script(): n.set_script(null)
func color(c): return [c.r,c.g,c.b,c.a]
func texture(t):
    if t == null: return {}
    if t is AtlasTexture:
        return {"path":t.atlas.resource_path,"rect":[t.region.position.x,t.region.position.y,t.region.size.x,t.region.size.y]}
    return {"path":t.resource_path,"rect":[0,0,t.get_width(),t.get_height()]}
func dump(n, root):
    var d={"path":str(root.get_path_to(n)),"type":n.get_class(),"name":str(n.name)}
    if n is Control:
        d["anchors"]=[n.anchor_left,n.anchor_top,n.anchor_right,n.anchor_bottom]
        d["offsets"]=[n.offset_left,n.offset_top,n.offset_right,n.offset_bottom]
        d["position"]=[n.position.x,n.position.y]; d["size"]=[n.size.x,n.size.y]
        d["scale"]=[n.scale.x,n.scale.y]; d["pivot"]=[n.pivot_offset.x + n.pivot_offset_ratio.x*n.size.x,n.pivot_offset.y + n.pivot_offset_ratio.y*n.size.y]
        d["minimum"]=[n.get_combined_minimum_size().x,n.get_combined_minimum_size().y]
        d["visible"]=n.visible;d["layoutMode"]=n.layout_mode
        d["grow"]=[n.grow_horizontal,n.grow_vertical]
        d["variation"]=str(n.theme_type_variation)
        d["modulate"]=color(n.modulate*n.self_modulate)
        d["mouseFilter"]=n.mouse_filter
    if n is Label:
        d["text"]=n.text;d["fontSize"]=n.get_theme_font_size("font_size")
        d["font"]=n.get_theme_font("font").resource_path
        d["color"]=color(n.get_theme_color("font_color"))
        d["horizontal"]=n.horizontal_alignment;d["vertical"]=n.vertical_alignment
        d["wrap"]=n.autowrap_mode != 0
        d["shadow"]=color(n.get_theme_color("font_shadow_color"))
        d["shadowOffset"]=[n.get_theme_constant("shadow_offset_x"),n.get_theme_constant("shadow_offset_y")]
    if n is TextureRect:
        d["texture"]=texture(n.texture);d["stretch"]=n.stretch_mode;d["expand"]=n.expand_mode
    if n is ScrollContainer:
        var bar=n.get_v_scroll_bar()
        var styles={}
        for style_name in ["scroll","grabber","grabber_highlight","grabber_pressed"]:
            var style=bar.get_theme_stylebox(style_name)
            if style is StyleBoxFlat:
                styles[style_name]={"color":color(style.bg_color),"radius":style.corner_radius_top_left,"border":style.border_width_left,"borderColor":color(style.border_color)}
        d["scrollbar"]={"width":bar.get_combined_minimum_size().x,"styles":styles}
    if n is ColorRect: d["color"]=color(n.color)
    if n is BaseButton:
        d["disabled"]=n.disabled
        if n is Button:
            d["icon"]=texture(n.icon);d["iconMaxWidth"]=n.get_theme_constant("icon_max_width")
            var style=n.get_theme_stylebox("normal")
            if style is StyleBoxFlat: d["buttonColor"]=color(style.bg_color)
    if n is BoxContainer: d["separation"]=n.get_theme_constant("separation")
    if n is GridContainer: d["columns"]=n.columns;d["hSeparation"]=n.get_theme_constant("h_separation");d["vSeparation"]=n.get_theme_constant("v_separation")
    var kids=[]
    for c in n.get_children():
        if c is Control: kids.append(dump(c,root))
    d["children"]=kids
    return d
func prepare(root):
    strip(root)
    for c in root.get_children():
        if str(c.name).begins_with("example"): c.free()
    root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
    root.show()
    var viewport=SubViewport.new();viewport.size=Vector2i(1387,733)
    get_root().add_child(viewport);viewport.add_child(root)
    return viewport
func run():
    var ui=load("res://assets/gfx/ui/hangar/ui_hangar.tscn").instantiate()
    var profile=ui.get_node("screens/player_stats")
    profile.theme=ui.theme
    ui.get_node("screens").remove_child(profile);ui.free()
    var viewport=prepare(profile)
    var reward=load("res://assets/gfx/ui/hangar/ui_reward_info.tscn").instantiate()
    var rv=prepare(reward)
    var history=load("res://assets/gfx/ui/hangar/ui_battle_notif.tscn").instantiate()
    strip(history);viewport.add_child(history)
    await process_frame;await process_frame;await process_frame
    scenes["profile"]=dump(profile,profile)
    # TabContainer only lays out the currently visible page. Resolve each page
    # before exporting it; hidden-page zeros are NOT valid native geometry.
    var tabs=profile.get_node("tabs")
    var exported_tabs
    for c in scenes["profile"]["children"]:
        if c["name"] == "tabs": exported_tabs=c
    for index in range(1,tabs.get_child_count()):
        tabs.current_tab=index
        await process_frame;await process_frame;await process_frame
        var child=dump(tabs.get_child(index),profile)
        child["visible"]=false
        exported_tabs["children"][index]=child
    tabs.current_tab=0
    scenes["reward"]=dump(reward,reward)
    scenes["history"]=dump(history,history)
    var args=OS.get_cmdline_user_args()
    var f=FileAccess.open(args[0],FileAccess.WRITE);f.store_string(JSON.stringify(scenes))
    quit()
