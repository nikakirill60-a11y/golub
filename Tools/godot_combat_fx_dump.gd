extends SceneTree
## Dumps the battle effects the Godot client actually draws: the six gun shot
## scenes tank_particles_manager.gd hangs on the gun, the five projectile
## impact scenes Projectile.play_sound spawns and the GPUTrail3D tracer preset
## Projectile.add_trail_emitter_to_node builds twice per shell.
##
## Every emitter is a GPUParticles3D whose numbers live in
## ParticleProcessMaterial, StandardMaterial3D and QuadMesh sub-resources, most
## of them left at Godot defaults in the scene text. Instantiating each scene
## headless is the only way to read the values the game runs, curves,
## gradients and engine defaults included.
## Tools/port_godot_combat_fx.py copies this into the source project, runs it
## and turns the JSON into Resources/Data/combat_fx_manifest.json.

const SCENES := [
	"res://assets/particles/tanks/shoot_auto.tscn",
	"res://assets/particles/tanks/shoot_small.tscn",
	"res://assets/particles/tanks/shoot_medium.tscn",
	"res://assets/particles/tanks/shoot_main.tscn",
	"res://assets/particles/tanks/shoot_large.tscn",
	"res://assets/particles/tanks/shoot_huge.tscn",
	"res://assets/particles/tanks/projectile_impact/armor_ap.tscn",
	"res://assets/particles/tanks/projectile_impact/armor_he.tscn",
	"res://assets/particles/tanks/projectile_impact/object_ap.tscn",
	"res://assets/particles/tanks/projectile_impact/object_heat.tscn",
	"res://assets/particles/tanks/projectile_impact/object_he.tscn",
]
const TRAIL_SCENE := "res://assets/tanks/base/projectile/trail_preset.tscn"
const CURVE_SAMPLES := 65

var _out := "/tmp/blitz_combat_fx"
var _warnings: Array = []

func _initialize() -> void:
	var env := OS.get_environment("BLITZ_COMBAT_FX_OUT")
	if !env.is_empty():
		_out = env
	DirAccess.make_dir_recursive_absolute(_out)

	var scenes: Array = []
	for path in SCENES:
		var packed: PackedScene = load(path)
		if packed == null:
			printerr("could not load ", path)
			quit(1)
			return
		var scene: Node3D = packed.instantiate()
		root.add_child(scene)
		var emitters: Array = []
		var others: Array = []
		for child in scene.get_children():
			if child is GPUParticles3D:
				emitters.append(_emitter(path, child))
			else:
				var script: Script = child.get_script()
				others.append({
					"name": String(child.name),
					"class": child.get_class(),
					"script": script.resource_path if script != null else "",
				})
		scenes.append({
			"scene": path,
			"name": path.get_file().get_basename(),
			"root": {
				"name": String(scene.name),
				"transform": _xform(scene.transform),
			},
			"emitters": emitters,
			"otherChildren": others,
		})
		scene.queue_free()

	_write("combat_fx.json", {
		"godotVersion": Engine.get_version_info().string,
		"renderingMethod": ProjectSettings.get_setting("rendering/renderer/rendering_method"),
		"scenes": scenes,
		"trail": _trail(),
		"bindings": _bindings(),
		"warnings": _warnings,
	})
	quit(0)

func _process(_delta: float) -> bool:
	return true

func _warn(scene: String, emitter: String, what: String) -> void:
	_warnings.append("%s/%s: %s" % [scene.get_file(), emitter, what])

func _emitter(scene: String, node: GPUParticles3D) -> Dictionary:
	var data := {
		"name": String(node.name),
		"visible": node.visible,
		"transform": _xform(node.transform),
		"emitting": node.emitting,
		"amount": node.amount,
		"amountRatio": node.amount_ratio,
		"lifetime": node.lifetime,
		"oneShot": node.one_shot,
		"preprocess": node.preprocess,
		"speedScale": node.speed_scale,
		"explosiveness": node.explosiveness,
		"randomness": node.randomness,
		"fixedFps": node.fixed_fps,
		"interpolate": node.interpolate,
		"fractDelta": node.fract_delta,
		"localCoords": node.local_coords,
		"drawOrder": int(node.draw_order),
		"transformAlign": int(node.transform_align),
		"transformAlignChannelFilter": int(node.transform_align_channel_filter),
		"transformAlignAxis": int(node.transform_align_axis),
		"useFixedSeed": node.use_fixed_seed,
		# An unpinned seed is re-rolled on every restart(); keep the dump stable.
		"seed": node.seed if node.use_fixed_seed else 0,
		"drawPasses": node.draw_passes,
		"castShadow": int(node.cast_shadow),
		"sortingOffset": node.sorting_offset,
		"sortingUseAabbCenter": node.sorting_use_aabb_center,
		"materialOverride": node.material_override != null,
		"trailEnabled": node.trail_enabled,
		"subEmitter": String(node.sub_emitter),
		"collisionBaseSize": node.collision_base_size,
	}
	if node.draw_passes > 1:
		_warn(scene, node.name, "more than one draw pass")
	if node.trail_enabled:
		_warn(scene, node.name, "particle trails")
	if node.material_override != null:
		_warn(scene, node.name, "material_override")
	if !String(node.sub_emitter).is_empty():
		_warn(scene, node.name, "sub emitter")

	var process := node.process_material
	if process is ParticleProcessMaterial:
		data["process"] = _process_material(scene, node.name, process)
	elif process != null:
		_warn(scene, node.name, "process material " + process.get_class())

	var mesh := node.draw_pass_1
	if mesh != null:
		data["mesh"] = _mesh(scene, node.name, mesh)
	return data

func _process_material(scene: String, emitter: String, m: ParticleProcessMaterial) -> Dictionary:
	var data := {
		"direction": _vec3(m.direction),
		"inheritVelocityRatio": m.inherit_velocity_ratio,
		"velocityPivot": _vec3(m.velocity_pivot),
		"spread": m.spread,
		"flatness": m.flatness,
		"initialVelocityMin": m.initial_velocity_min,
		"initialVelocityMax": m.initial_velocity_max,
		"angularVelocityMin": m.angular_velocity_min,
		"angularVelocityMax": m.angular_velocity_max,
		"radialVelocityMin": m.radial_velocity_min,
		"radialVelocityMax": m.radial_velocity_max,
		"orbitVelocityMin": m.orbit_velocity_min,
		"orbitVelocityMax": m.orbit_velocity_max,
		"directionalVelocityMin": m.directional_velocity_min,
		"directionalVelocityMax": m.directional_velocity_max,
		"gravity": _vec3(m.gravity),
		"linearAccelMin": m.linear_accel_min,
		"linearAccelMax": m.linear_accel_max,
		"radialAccelMin": m.radial_accel_min,
		"radialAccelMax": m.radial_accel_max,
		"tangentialAccelMin": m.tangential_accel_min,
		"tangentialAccelMax": m.tangential_accel_max,
		"dampingMin": m.damping_min,
		"dampingMax": m.damping_max,
		"angleMin": m.angle_min,
		"angleMax": m.angle_max,
		"scaleMin": m.scale_min,
		"scaleMax": m.scale_max,
		"lifetimeRandomness": m.lifetime_randomness,
		"animSpeedMin": m.anim_speed_min,
		"animSpeedMax": m.anim_speed_max,
		"animOffsetMin": m.anim_offset_min,
		"animOffsetMax": m.anim_offset_max,
		"hueVariationMin": m.hue_variation_min,
		"hueVariationMax": m.hue_variation_max,
		"emissionShape": int(m.emission_shape),
		"emissionShapeOffset": _vec3(m.emission_shape_offset),
		"emissionShapeScale": _vec3(m.emission_shape_scale),
		"emissionSphereRadius": m.emission_sphere_radius,
		"emissionBoxExtents": _vec3(m.emission_box_extents),
		"emissionRingAxis": _vec3(m.emission_ring_axis),
		"emissionRingHeight": m.emission_ring_height,
		"emissionRingRadius": m.emission_ring_radius,
		"emissionRingInnerRadius": m.emission_ring_inner_radius,
		"emissionRingConeAngle": m.emission_ring_cone_angle,
		"alignY": m.particle_flag_align_y,
		"rotateY": m.particle_flag_rotate_y,
		"disableZ": m.particle_flag_disable_z,
		"dampingAsFriction": m.particle_flag_damping_as_friction,
		"inheritEmitterScale": m.particle_flag_inherit_emitter_scale,
		"useScale3d": m.use_scale_3d,
		"scale3dMin": _vec3(m.scale_3d_min),
		"scale3dMax": _vec3(m.scale_3d_max),
		"useRotation3d": m.use_rotation_3d,
		"useRotationVelocity3d": m.use_rotation_velocity_3d,
		"color": _color(m.color),
		"subEmitterMode": int(m.sub_emitter_mode),
		"collisionMode": int(m.collision_mode),
		"turbulenceEnabled": m.turbulence_enabled,
		"attractorInteractionEnabled": m.attractor_interaction_enabled,
	}

	# Features the port does not carry: every one of them must stay unused.
	if m.turbulence_enabled:
		_warn(scene, emitter, "turbulence")
	if int(m.collision_mode) != 0:
		_warn(scene, emitter, "collision")
	if int(m.sub_emitter_mode) != 0:
		_warn(scene, emitter, "sub emitter mode")
	if m.orbit_velocity_min != 0.0 or m.orbit_velocity_max != 0.0:
		_warn(scene, emitter, "orbit velocity")
	# Directional velocity only acts through directional_velocity_curve, which
	# the curve loop below rejects.
	if m.use_scale_3d:
		_warn(scene, emitter, "scale 3d")
	if m.use_rotation_3d:
		_warn(scene, emitter, "rotation 3d")
	if m.use_rotation_velocity_3d:
		_warn(scene, emitter, "rotation velocity 3d")
	if m.particle_flag_inherit_emitter_scale:
		_warn(scene, emitter, "inherit emitter scale")
	if m.particle_flag_disable_z:
		_warn(scene, emitter, "disable z")
	if m.hue_variation_min != 0.0 or m.hue_variation_max != 0.0:
		_warn(scene, emitter, "hue variation")
	if m.inherit_velocity_ratio != 0.0:
		_warn(scene, emitter, "inherit velocity")
	if int(m.emission_shape) > 3:
		_warn(scene, emitter, "emission shape %d" % int(m.emission_shape))
	if m.color_initial_ramp != null:
		_warn(scene, emitter, "color initial ramp")
	if m.emission_curve != null:
		_warn(scene, emitter, "emission curve")
	if m.velocity_limit_curve != null:
		_warn(scene, emitter, "velocity limit curve")
	for p in ["initial_velocity", "angular_velocity", "orbit_velocity", "linear_accel", "radial_accel",
			"tangential_accel", "damping", "angle", "hue_variation", "anim_speed", "anim_offset",
			"radial_velocity", "directional_velocity", "scale_over_velocity"]:
		var prop: String = String(p) + "_curve"
		if prop in m and m.get(prop) != null:
			_warn(scene, emitter, prop)

	if m.scale_curve != null:
		data["scaleCurve"] = _curve(scene, emitter, m.scale_curve)
	if m.alpha_curve != null:
		data["alphaCurve"] = _curve(scene, emitter, m.alpha_curve)
	if m.color_ramp != null:
		data["colorRamp"] = _ramp(m.color_ramp)
	return data

## The shader samples a CurveTexture (RF float texture, linear filter). Its
## texels are curve.sample_baked(i / width) (CurveTexture::_update); headless
## servers return no image, so the texels are rebuilt the same way here.
func _curve(scene: String, emitter: String, texture: Texture2D) -> Dictionary:
	if texture is CurveXYZTexture:
		_warn(scene, emitter, "CurveXYZTexture scale curve")
	if texture is CurveTexture:
		var ct: CurveTexture = texture
		if ct.curve == null:
			# No curve means _update never ran: the material binds the
			# RenderingServer 4x4 magenta/black placeholder texture instead.
			return {"width": ct.width, "texels": [], "placeholder": true}
		var texels: Array = []
		for x in ct.width:
			texels.append(ct.curve.sample_baked(x / float(ct.width)))
		var points: Array = []
		for i in ct.curve.point_count:
			points.append({
				"position": [ct.curve.get_point_position(i).x, ct.curve.get_point_position(i).y],
				"leftTangent": ct.curve.get_point_left_tangent(i),
				"rightTangent": ct.curve.get_point_right_tangent(i),
			})
		return {"width": ct.width, "texels": texels, "points": points, "placeholder": false}
	_warn(scene, emitter, "curve texture " + texture.get_class())
	return {"width": 1, "texels": [1.0], "placeholder": false}

func _ramp(texture: Texture2D) -> Dictionary:
	var data := {"stops": [], "texels": []}
	if texture is GradientTexture1D and texture.gradient != null:
		var gt: GradientTexture1D = texture
		var gradient: Gradient = gt.gradient
		data["width"] = gt.width
		data["useHdr"] = gt.use_hdr
		data["interpolation"] = int(gradient.interpolation_mode)
		data["colorSpace"] = int(gradient.interpolation_color_space)
		for i in gradient.get_point_count():
			data["stops"].append({
				"offset": gradient.get_offset(i),
				"color": _color(gradient.get_color(i)),
			})
		# GradientTexture1D::_update: offset i / (width - 1), stored as RGBA8
		# (Color.get_r8 rounding) unless use_hdr keeps floats.
		for x in gt.width:
			var c: Color = gradient.sample(x / float(gt.width - 1))
			if gt.use_hdr:
				data["texels"].append(_color(c))
			else:
				data["texels"].append([c.r8 / 255.0, c.g8 / 255.0, c.b8 / 255.0, c.a8 / 255.0])
	return data

func _mesh(scene: String, emitter: String, mesh: Mesh) -> Dictionary:
	var data := {"class": mesh.get_class()}
	if mesh is QuadMesh:
		data["size"] = [mesh.size.x, mesh.size.y]
		data["centerOffset"] = _vec3(mesh.center_offset)
		data["orientation"] = int(mesh.orientation)
		data["flipFaces"] = mesh.flip_faces
	else:
		_warn(scene, emitter, "draw pass mesh " + mesh.get_class())

	var material: Material = mesh.surface_get_material(0)
	if material == null and "material" in mesh:
		material = mesh.material
	if material is StandardMaterial3D:
		data["material"] = _standard(scene, emitter, material)
	elif material != null:
		_warn(scene, emitter, "material " + material.get_class())
	return data

func _standard(scene: String, emitter: String, m: StandardMaterial3D) -> Dictionary:
	var texture: String = ""
	if m.albedo_texture != null:
		texture = m.albedo_texture.resource_path
	if m.next_pass != null:
		_warn(scene, emitter, "material next_pass")
	if m.emission_enabled:
		_warn(scene, emitter, "emission")
	if m.proximity_fade_enabled:
		_warn(scene, emitter, "proximity fade")
	if int(m.distance_fade_mode) != 0:
		_warn(scene, emitter, "distance fade")
	if int(m.shading_mode) != 0:
		_warn(scene, emitter, "shaded material")
	if m.fixed_size:
		_warn(scene, emitter, "fixed size")
	return {
		"resource": m.resource_path,
		"transparency": int(m.transparency),
		"alphaScissorThreshold": m.alpha_scissor_threshold,
		"blendMode": int(m.blend_mode),
		"cullMode": int(m.cull_mode),
		"depthDrawMode": int(m.depth_draw_mode),
		"noDepthTest": m.no_depth_test,
		"shadingMode": int(m.shading_mode),
		"specularMode": int(m.specular_mode),
		"disableAmbientLight": m.disable_ambient_light,
		"disableFog": m.disable_fog,
		"vertexColorUseAsAlbedo": m.vertex_color_use_as_albedo,
		"vertexColorIsSrgb": m.vertex_color_is_srgb,
		"albedoColor": _color(m.albedo_color),
		"albedoTexture": texture,
		"albedoTextureForceSrgb": m.albedo_texture_force_srgb,
		"textureFilter": int(m.texture_filter),
		"textureRepeat": m.texture_repeat,
		"uv1Scale": _vec3(m.uv1_scale),
		"uv1Offset": _vec3(m.uv1_offset),
		"billboardMode": int(m.billboard_mode),
		"billboardKeepScale": m.billboard_keep_scale,
		"animHFrames": m.particles_anim_h_frames,
		"animVFrames": m.particles_anim_v_frames,
		"animLoop": m.particles_anim_loop,
		"renderPriority": m.render_priority,
	}

func _trail() -> Dictionary:
	var packed: PackedScene = load(TRAIL_SCENE)
	var node: GPUParticles3D = packed.instantiate()
	root.add_child(node)
	var draw: ShaderMaterial = (node.draw_pass_1 as QuadMesh).material
	var curve_tex: Texture2D = draw.get_shader_parameter("curve")
	var ramp_tex: Texture2D = draw.get_shader_parameter("color_ramp")
	var tex: Texture2D = draw.get_shader_parameter("tex")
	var data := {
		"scene": TRAIL_SCENE,
		"amount": node.amount,
		"lifetime": node.lifetime,
		"fixedFps": node.fixed_fps,
		"explosiveness": node.explosiveness,
		"localCoords": node.local_coords,
		"length": node.get("length"),
		"lengthSeconds": node.get("length_seconds"),
		"flags": draw.get_shader_parameter("flags"),
		"billboard": node.get("billboard"),
		"dewiggle": node.get("dewiggle"),
		"clipOverlaps": node.get("clip_overlaps"),
		"snapToTransform": node.get("snap_to_transform"),
		"verticalTexture": node.get("vertical_texture"),
		"useRedAsAlpha": node.get("use_red_as_alpha"),
		"maskStrength": draw.get_shader_parameter("mask_strength"),
		"uvOffset": [draw.get_shader_parameter("uv_offset").x, draw.get_shader_parameter("uv_offset").y],
		"texture": tex.resource_path if tex != null else "",
		"curve": _curve(TRAIL_SCENE, "GPUTrail3D", curve_tex),
		"presetColorRamp": _ramp(ramp_tex),
	}
	node.queue_free()
	return data

## Which scene each gun and each shell type spawns, read from the static
## tables of tank_particles_manager.gd and projectile.gd, plus the tracer and
## shell sprite colours those scripts paint with.
func _bindings() -> Dictionary:
	var manager: Script = load("res://assets/tanks/base/tank_particles_manager.gd")
	var projectile: Script = load("res://assets/tanks/base/projectile/projectile.gd")
	var shot_sounds := {}
	for key in ModuleGun.ShootSound:
		shot_sounds[key] = ModuleGun.ShootSound[key]
	var shell_types := {}
	for key in ShellInfo.Type:
		shell_types[key] = ShellInfo.Type[key]
	var sprite := Sprite3D.new()
	var shell_tex: Texture2D = projectile.get("shell_tex")
	var data := {
		"shootSounds": shot_sounds,
		"shellTypes": shell_types,
		"shotScenes": _scene_table(manager.get("gunsound_to_particle")),
		"penetrationScenes": _scene_table(projectile.get("shell_type_to_eff_pen")),
		"objectScenes": _scene_table(projectile.get("shell_type_to_eff_obj")),
		"trailColor": _color(projectile.get("ap_color")),
		"trailColorHeat": _color(projectile.get("heat_color")),
		"shellModulate": _color(Color.BEIGE),
		"shellModulateHeat": _color(Color.BISQUE),
		"shellTexture": shell_tex.resource_path,
		"sprite": {
			"centered": sprite.centered,
			"offset": [sprite.offset.x, sprite.offset.y],
			"textureFilter": int(sprite.texture_filter),
			"alphaCut": int(sprite.alpha_cut),
			"shaded": sprite.shaded,
			"doubleSided": sprite.double_sided,
			"transparent": sprite.transparent,
			"noDepthTest": sprite.no_depth_test,
			"renderPriority": sprite.render_priority,
		},
	}
	sprite.free()
	return data

func _scene_table(table: Dictionary) -> Dictionary:
	var out := {}
	for key in table:
		var scene: PackedScene = table[key]
		out[str(key)] = scene.resource_path.get_file().get_basename()
	return out

func _xform(t: Transform3D) -> Dictionary:
	return {
		"x": _vec3(t.basis.x),
		"y": _vec3(t.basis.y),
		"z": _vec3(t.basis.z),
		"origin": _vec3(t.origin),
	}

func _vec3(v: Vector3) -> Array:
	return [v.x, v.y, v.z]

func _color(c: Color) -> Array:
	return [c.r, c.g, c.b, c.a]

func _write(name: String, data: Variant) -> void:
	var file := FileAccess.open(_out.path_join(name), FileAccess.WRITE)
	if file == null:
		printerr("could not write ", name)
		return
	file.store_string(JSON.stringify(data, "  ", false))
	file.close()
