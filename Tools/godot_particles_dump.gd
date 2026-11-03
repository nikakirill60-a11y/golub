extends SceneTree
## Dumps the tank wreck effect, assets/particles/tanks/explosion1.tscn.
##
## The scene is ten GPUParticles3D emitters whose numbers live in
## ParticleProcessMaterial, StandardMaterial3D and QuadMesh sub-resources, most
## of them left at their Godot defaults in the scene text. Instantiating the
## scene headless is the only way to read the values the game actually runs,
## curves and gradients included.
## Tools/port_godot_particles.py copies this into the source project, runs it
## and turns the JSON into the port's particle manifest.

const SCENE := "res://assets/particles/tanks/explosion1.tscn"
const CURVE_SAMPLES := 17

var _out := "/tmp/blitz_particles"
var _scene: Node3D = null

func _initialize() -> void:
	var env := OS.get_environment("BLITZ_PARTICLES_OUT")
	if !env.is_empty():
		_out = env
	DirAccess.make_dir_recursive_absolute(_out)

	var packed: PackedScene = load(SCENE)
	if packed == null:
		printerr("could not load ", SCENE)
		quit(1)
		return

	_scene = packed.instantiate()
	root.add_child(_scene)

	var emitters: Array = []
	for child in _scene.get_children():
		if child is GPUParticles3D:
			emitters.append(_emitter(child))

	_write("explosion1.json", {
		"scene": SCENE,
		"root": {
			"name": _scene.name,
			"scale": _vec3(_scene.scale),
		},
		"emitters": emitters,
	})
	quit(0)

func _process(_delta: float) -> bool:
	return true

func _emitter(node: GPUParticles3D) -> Dictionary:
	var data := {
		"name": String(node.name),
		"position": _vec3(node.position),
		"rotationDegrees": _vec3(node.rotation_degrees),
		"scale": _vec3(node.scale),
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
		"subEmitter": String(node.sub_emitter),
	}

	var process := node.process_material
	if process is ParticleProcessMaterial:
		data["process"] = _process_material(process)

	var mesh := node.draw_pass_1
	if mesh != null:
		data["mesh"] = _mesh(mesh)

	return data

func _process_material(m: ParticleProcessMaterial) -> Dictionary:
	var data := {
		"direction": _vec3(m.direction),
		"spread": m.spread,
		"flatness": m.flatness,
		"initialVelocityMin": m.initial_velocity_min,
		"initialVelocityMax": m.initial_velocity_max,
		"angularVelocityMin": m.angular_velocity_min,
		"angularVelocityMax": m.angular_velocity_max,
		"radialVelocityMin": m.radial_velocity_min,
		"radialVelocityMax": m.radial_velocity_max,
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
		"emissionSphereRadius": m.emission_sphere_radius,
		"emissionBoxExtents": _vec3(m.emission_box_extents),
		"alignY": m.particle_flag_align_y,
		"rotateY": m.particle_flag_rotate_y,
		"disableZ": m.particle_flag_disable_z,
		"color": _color(m.color),
		"subEmitterMode": int(m.sub_emitter_mode),
		"subEmitterFrequency": m.sub_emitter_frequency,
		"subEmitterAmountAtEnd": m.sub_emitter_amount_at_end,
		"collisionMode": int(m.collision_mode),
	}

	if m.scale_curve != null:
		data["scaleCurve"] = _curve(m.scale_curve)
	if m.alpha_curve != null:
		data["alphaCurve"] = _curve(m.alpha_curve)
	if m.color_ramp != null:
		data["colorRamp"] = _ramp(m.color_ramp)

	return data

func _curve(texture: Texture2D) -> Array:
	var samples: Array = []
	if texture is CurveTexture and texture.curve != null:
		var curve: Curve = texture.curve
		for i in CURVE_SAMPLES:
			var t: float = float(i) / float(CURVE_SAMPLES - 1)
			samples.append(curve.sample_baked(t))
		return samples

	# An empty CurveTexture bakes to a flat one, which is what Godot feeds the
	# shader, so record that instead of dropping the track.
	for i in CURVE_SAMPLES:
		samples.append(1.0)
	return samples

func _ramp(texture: Texture2D) -> Array:
	var stops: Array = []
	if texture is GradientTexture1D and texture.gradient != null:
		var gradient: Gradient = texture.gradient
		for i in gradient.get_point_count():
			stops.append({
				"offset": gradient.get_offset(i),
				"color": _color(gradient.get_color(i)),
			})
	return stops

func _mesh(mesh: Mesh) -> Dictionary:
	var data := {"class": mesh.get_class()}
	if mesh is QuadMesh:
		data["size"] = [mesh.size.x, mesh.size.y]
		data["centerOffset"] = _vec3(mesh.center_offset)

	var material: Material = mesh.surface_get_material(0)
	if material == null:
		material = mesh.material
	if material is StandardMaterial3D:
		data["material"] = _standard(material)
	return data

func _standard(m: StandardMaterial3D) -> Dictionary:
	var texture: String = ""
	if m.albedo_texture != null:
		texture = m.albedo_texture.resource_path
	return {
		"transparency": int(m.transparency),
		"blendMode": int(m.blend_mode),
		"cullMode": int(m.cull_mode),
		"shadingMode": int(m.shading_mode),
		"specularMode": int(m.specular_mode),
		"disableAmbientLight": m.disable_ambient_light,
		"disableFog": m.disable_fog,
		"vertexColorUseAsAlbedo": m.vertex_color_use_as_albedo,
		"albedoColor": _color(m.albedo_color),
		"albedoTexture": texture,
		"billboardMode": int(m.billboard_mode),
		"animHFrames": m.particles_anim_h_frames,
		"animVFrames": m.particles_anim_v_frames,
		"animLoop": m.particles_anim_loop,
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
