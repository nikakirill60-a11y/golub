extends SceneTree
# Read after autoloads exist: loading PlayerData during script compilation would
# resolve its Global dependency before the autoload was registered.
func _initialize(): call_deferred("run")
func run():
    var data=load("res://scripts/_player_data.gd")
    data.reload()
    data.init()
    var rewards={}
    for id in data.rewards:
        var r=data.rewards[id]
        var tex=r.icon
        if tex is AtlasTexture:
            rewards[id]={"sheet":tex.atlas.resource_path,"rect":[tex.region.position.x,tex.region.position.y,tex.region.size.x,tex.region.size.y],"rare":r.rare,"gold":r.gold_reward,"nameKey":str(r.name_key),"descKey":str(r.desc_key),"oneTime":r.is_onetimer()}
    var args=OS.get_cmdline_user_args()
    if args.is_empty(): quit(1); return
    var f=FileAccess.open(args[0],FileAccess.WRITE)
    f.store_string(JSON.stringify(rewards))
    quit()
