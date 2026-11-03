extends SceneTree
func _initialize(): call_deferred("run")
func run():
    TranslationServer.set_locale("en")
    var account_script=load("res://scripts/account.gd");account_script.reload()
    var player_script=load("res://scripts/player.gd");player_script.reload()
    var result_script=load("res://scripts/battle_result_res.gd");result_script.reload()
    var acc=account_script.new()
    for i in range(23):
        var p=player_script.new();p.name="Amway921";p.tank_name="is-7";p.tank_info=get_root().get_node("TanksDataBase").get_tank_by_name("is-7")
        p.log_damage_dealt["target"]=100+i
        p.shells_shot[0]=10 if i%2==0 else 2;p.shells_shot[1]=0;p.shells_shot[2]=0
        p.log_hits_dmg["target"]=1 if i%2==0 else 2
        p.xp_earned_raw=200+i;p.xp_earned=1000;p.health=1 if i%2==0 else 0
        for k in range(i%7):p.kills.append("target")
        var res=result_script.new(1452931361+i,200);res.resolved=true;res.player_tank_id="is-7";res.teamA.append(p);res.status=i%3;res.credits_raw=10000+i;res.credits=100;res.xp=1000;res.gold=10
        acc.battle_results.append(res.to_dict())
    acc.rewards["first_victory"]=1;acc.rewards["warrior"]=2
    var args=OS.get_cmdline_user_args()
    var averages=acc.get_avg_battle_stats();averages["rating"]=acc.get_rating();averages["winRate"]=acc.get_wr()
    FileAccess.open(args[0]+"/native_statistics.json",FileAccess.WRITE).store_string(JSON.stringify(averages))
    var ui=load("res://assets/gfx/ui/hangar/ui_hangar.tscn").instantiate()
    var profile=ui.get_node("screens/player_stats");profile.theme=ui.theme
    ui.get_node("screens").remove_child(profile)
    var viewport=SubViewport.new();viewport.size=Vector2i(1387,733);viewport.render_target_update_mode=SubViewport.UPDATE_ALWAYS;viewport.transparent_bg=false
    get_root().add_child(viewport);viewport.add_child(profile);profile.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT);profile.show()
    profile.get_node("blur").hide()
    for c in profile.get_children():
        if str(c.name).begins_with("example"):c.queue_free()
    profile.update(acc)
    profile.get_node("tabs").current_tab=0
    await process_frame;await process_frame;await RenderingServer.frame_post_draw
    viewport.get_texture().get_image().save_png(args[0]+"/native_summary.png")
    profile.get_node("tabs").current_tab=1
    await process_frame;await RenderingServer.frame_post_draw
    viewport.get_texture().get_image().save_png(args[0]+"/native_history.png")
    ui.free();quit()
