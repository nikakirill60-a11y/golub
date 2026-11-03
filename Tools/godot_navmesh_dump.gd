extends SceneTree
## Dumps the baked navigation meshes of a Blitz Legacy Godot map as JSON.
##
## The port shipped no navigation data at all, so AiTankController clamped its
## goals to the play area and steered straight at them: bots drove into the
## lake and then stalled against slopes the authored navmesh never contained.
## This reads the real NavigationRegion3D resources - vertices, polygons and
## the bake settings - and Tools/port_godot_navmesh.py turns the JSON into the
## port's runtime navmesh resource.

const MAPS := {
	"rudniki": "res://maps/rudniki/_rudniki.tscn",
}

func _initialize() -> void:
	var out := OS.get_environment("BLITZ_NAVMESH_OUT")
	if out.is_empty():
		out = "/tmp/blitz_navmesh"
	DirAccess.make_dir_recursive_absolute(out)
	for key in MAPS.keys():
		_dump_map(key, MAPS[key], out)
	quit()

func _dump_map(key: String, path: String, out: String) -> void:
	var packed: PackedScene = load(path)
	if packed == null:
		push_error("missing scene %s" % path)
		return
	var root: Node = packed.instantiate()
	var regions: Array = []
	_collect(root, Transform3D.IDENTITY, regions)
	var payload := {
		"map": key,
		"scene": path,
		"regions": regions,
	}
	var file := FileAccess.open("%s/navmesh_%s.json" % [out, key], FileAccess.WRITE)
	if file == null:
		push_error("cannot write navmesh json for %s" % key)
		root.free()
		return
	file.store_string(JSON.stringify(payload))
	file.close()
	var total_polys := 0
	for region in regions:
		total_polys += (region["polygons"] as Array).size()
	print("navmesh %s: %d region(s), %d polygon(s)" % [key, regions.size(), total_polys])
	root.free()

func _collect(node: Node, parent_xform: Transform3D, regions: Array) -> void:
	var xform := parent_xform
	if node is Node3D:
		xform = parent_xform * (node as Node3D).transform
	if node is NavigationRegion3D:
		var region := node as NavigationRegion3D
		var mesh: NavigationMesh = region.navigation_mesh
		if mesh != null:
			regions.append(_region_payload(region, mesh, xform))
	for child in node.get_children():
		_collect(child, xform, regions)

func _region_payload(
		region: NavigationRegion3D,
		mesh: NavigationMesh,
		xform: Transform3D) -> Dictionary:
	var verts := mesh.get_vertices()
	var flat := []
	flat.resize(verts.size() * 3)
	for i in verts.size():
		# Bake the region transform in: the navmesh is authored in region space
		# and rudniki lifts its region by 1.1526184 on Y.
		var v: Vector3 = xform * verts[i]
		flat[i * 3 + 0] = v.x
		flat[i * 3 + 1] = v.y
		flat[i * 3 + 2] = v.z
	var polys := []
	for i in mesh.get_polygon_count():
		var poly := mesh.get_polygon(i)
		var indices := []
		indices.resize(poly.size())
		for j in poly.size():
			indices[j] = poly[j]
		polys.append(indices)
	return {
		"name": String(region.name),
		"enabled": region.enabled,
		"origin": [xform.origin.x, xform.origin.y, xform.origin.z],
		"cell_size": mesh.cell_size,
		"cell_height": mesh.cell_height,
		"agent_radius": mesh.agent_radius,
		"agent_height": mesh.agent_height,
		"agent_max_climb": mesh.agent_max_climb,
		"agent_max_slope": mesh.agent_max_slope,
		"vertices": flat,
		"polygons": polys,
	}
