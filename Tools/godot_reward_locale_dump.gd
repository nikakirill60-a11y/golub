extends SceneTree
func _initialize(): call_deferred("run")
func run():
    var data=load("res://scripts/_player_data.gd")
    data.init()
    var strings={}
    for id in data.rewards:
        var r=data.rewards[id]
        for key in [r.name_key,r.desc_key]:
            var translated={}
            for lang in ["en","ru"]:
                TranslationServer.set_locale(lang)
                translated[lang]=tr(key)
            strings[str(key)]=translated
    FileAccess.open(OS.get_cmdline_user_args()[0],FileAccess.WRITE).store_string(JSON.stringify(strings))
    quit()
