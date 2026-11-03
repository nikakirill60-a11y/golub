"""Stable, lossless ordering of split glTF primitives despite Trimesh random node suffixes."""
import hashlib


def install(exporter):
    original_parts = exporter.part_nodes
    def stable_parts(scene, node_name):
        # Trimesh gives split glTF primitives random six-hex node suffixes.
        # Sorting those names permutes otherwise identical OBJ exports. Sort
        # exact primitive attributes instead: no welding, normal recomputation,
        # decimation or texture resampling, and no changes to legacy exporters.
        import numpy as np
        def key(node):
            transform, geometry_name = scene.graph.get(node)
            mesh = scene.geometry[geometry_name]
            digest = hashlib.sha256()
            uv = getattr(mesh.visual, 'uv', None)
            for label, array, dtype in (
                ('transform', transform, np.float64),
                ('vertices', mesh.vertices, np.float64),
                ('faces', mesh.faces, np.int64),
                ('normals', mesh.vertex_normals, np.float64),
                ('uv', uv if uv is not None else [], np.float64),
            ):
                values = np.asarray(array, dtype=dtype)
                digest.update(label.encode())
                digest.update(str(values.shape).encode())
                digest.update(values.tobytes(order='C'))
            return digest.hexdigest()
        return sorted(original_parts(scene, node_name), key=key)
    exporter.part_nodes = stable_parts
