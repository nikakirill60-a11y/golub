extends SceneTree
## Reference frames of the Godot combat effects for the Unity port.
##
## Every case is rendered into its own 960x540 SubViewport by the Mobile
## renderer with a fixed camera, a flat background, no fog and fixed seeds on
## every emitter, so Tools/combat_fx_reference.py can render the same cases
## through BlitzLegacy.EditorTools.CombatFxRenderCli and compare frame by
## frame. Run with --fixed-fps 60 so every frame advances exactly 1/60 s.
## Output: $BLITZ_COMBAT_FX_RENDER_OUT/<case>_<frame>.png

const SIZE := Vector2i(960, 540)
const CAMERA_POS := Vector3(0, 1.5, 7)
const CAMERA_TARGET := Vector3(0, 0.8, 0)
const CAMERA_FOV := 50.0
const BACKGROUND := Color(0.35, 0.4, 0.45)
const SEED_BASE := 1000

## name, kind, scene/arguments, captured frames
const CASES := [
	{"name": "armor_ap", "kind": "impact", "scene": "res://assets/particles/tanks/projectile_impact/armor_ap.tscn", "caliber": 100, "normal": Vector3.UP, "frames": [2, 6, 15, 40]},
	{"name": "armor_he", "kind": "impact", "scene": "res://assets/particles/tanks/projectile_impact/armor_he.tscn", "caliber": 100, "normal": Vector3(0, 0, 1), "frames": [2, 6, 15, 40]},
	{"name": "armor_he_tilt", "kind": "impact", "scene": "res://assets/particles/tanks/projectile_impact/armor_he.tscn", "caliber": 100, "normal": Vector3(0, 0.3, 1), "frames": [2, 6, 15, 40]},
	{"name": "object_heat", "kind": "impact", "scene": "res://assets/particles/tanks/projectile_impact/object_heat.tscn", "caliber": 100, "normal": Vector3.UP, "frames": [2, 6, 15, 40]},
	{"name": "object_ap", "kind": "impact", "scene": "res://assets/particles/tanks/projectile_impact/object_ap.tscn", "caliber": 85, "normal": Vector3(0.6, 0.8, 0), "frames": [2, 6, 15, 40]},
	{"name": "shoot_main", "kind": "shot", "scene": "res://assets/particles/tanks/shoot_main.tscn", "frames": [1, 3, 8, 30]},
	{"name": "shoot_medium", "kind": "shot", "scene": "res://assets/particles/tanks/shoot_medium.tscn", "frames": [1, 3, 8, 30]},
	{"name": "shoot_huge", "kind": "shot", "scene": "res://assets/particles/tanks/shoot_huge.tscn", "frames": [1, 3, 8, 30]},
	{"name": "trail_ap", "kind": "trail", "caliber": 100, "heat": false, "speed": 150.0, "frames": [6, 12, 20, 30]},
	{"name": "trail_heat", "kind": "trail", "caliber": 122, "heat": true, "speed": 150.0, "frames": [6, 12, 20, 30]},
]

## Godot-space pose shared with the Unity renderer.
const GUN_POS := Vector3(-1.5, 0.8, 0)
const GUN_DIR := Vector3(1, 0.05, -0.2)
const TRAIL_START := Vector3(-6, 0.8, 0)
const TRAIL_DIR := Vector3(1, 0, -0.15)

var _out := "/tmp/blitz_combat_fx_render"
var _viewport: SubViewport
var _moving: Node3D
var _moving_step := Vector3.ZERO

func _initialize() -> void:
	var env := OS.get_environment("BLITZ_COMBAT_FX_RENDER_OUT")
	if !env.is_empty():
		_out = env
	DirAccess.make_dir_recursive_absolute(_out)
	physics_frame.connect(_on_physics_frame)
	_run.call_deferred()

func _on_physics_frame() -> void:
	if is_instance_valid(_moving):
		_moving.global_position += _moving_step

func _make_viewport() -> void:
	_viewport = SubViewport.new()
	_viewport.size = SIZE
	_viewport.own_world_3d = true
	_viewport.transparent_bg = false
	_viewport.msaa_3d = Viewport.MSAA_DISABLED
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)

	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = BACKGROUND
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.BLACK
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.fog_enabled = false
	environment.glow_enabled = false
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	_viewport.add_child(world_environment)

	var camera := Camera3D.new()
	camera.fov = CAMERA_FOV
	camera.near = 0.05
	camera.far = 500.0
	_viewport.add_child(camera)
	camera.look_at_from_position(CAMERA_POS, CAMERA_TARGET)
	camera.current = true

func _fix_seeds(node: Node) -> void:
	var index := 0
	for child in node.get_children():
		if child is GPUParticles3D:
			(child as GPUParticles3D).use_fixed_seed = true
			(child as GPUParticles3D).seed = SEED_BASE + index
			index += 1

func _start_case(case: Dictionary) -> Node3D:
	var holder := Node3D.new()
	_viewport.add_child(holder)
	match case.kind:
		"impact":
			var particle = load(case.scene).instantiate()
			holder.add_child(particle)
			particle.top_level = true
			particle.global_position = Vector3.ZERO
			particle.scale = Vector3.ONE * case.caliber * 0.05
			_fix_seeds(particle)
			particle.start(false, case.normal)
		"shot":
			var gun := Node3D.new()
			holder.add_child(gun)
			gun.look_at_from_position(GUN_POS, GUN_POS + GUN_DIR)
			var particle = load(case.scene).instantiate()
			gun.add_child(particle)
			particle.position = Vector3.ZERO
			_fix_seeds(particle)
			particle.start()
		"trail":
			# Loaded at run time: the autoloads these scripts name do not exist
			# yet when a --script main loop is compiled.
			var shell_info_script = load("res://assets/data/tank_info/shell_info.gd")
			var info = shell_info_script.new()
			info.caliber = case.caliber
			info.type = shell_info_script.Type.HEAT if case.heat else shell_info_script.Type.AP
			var trail := Node3D.new()
			trail.top_level = true
			holder.add_child(trail)
			trail.global_position = TRAIL_START
			trail.look_at(TRAIL_START + TRAIL_DIR.normalized() * 10)
			load("res://assets/tanks/base/projectile/projectile.gd").add_trail_emitter_to_node(trail, info, clampi(info.caliber, 20, 150) * 0.0075)
			_moving = trail
			_moving_step = TRAIL_DIR.normalized() * case.speed / 60.0
	return holder

func _run() -> void:
	_make_viewport()
	await process_frame
	for case in CASES:
		var holder := _start_case(case)
		var last: int = case.frames.max()
		for frame in range(1, last + 1):
			await RenderingServer.frame_post_draw
			if case.frames.has(frame):
				var image := _viewport.get_texture().get_image()
				image.save_png("%s/%s_%02d.png" % [_out, case.name, frame])
		_moving = null
		holder.queue_free()
		await process_frame
		await process_frame
	print("[combat-fx-render] done ", CASES.size(), " cases -> ", _out)
	quit(0)
