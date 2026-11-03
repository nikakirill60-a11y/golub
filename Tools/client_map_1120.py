"""Read a World of Tanks Blitz 11.20 client pack for the map porter.

port_blitz_client_map.py was written against the 4.8.0 SmartDLC, where every
file of a map lies unpacked under `3d/Maps/<client>` and the data tables sit
in `from_apk/Data`. An 11.20 pack differs in ways that are all handled here,
so the shared exporters keep reading an ordinary directory:

* nearly every file ships as `.dvpl` (an LZ4 block plus a footer). ClientPack
  unpacks what a port reads into a scratch mirror with the same relative
  paths, which the exporters then treat as a pack; the manifest's `sources`
  still name the shipped files and hash them as shipped;
* the data tables moved from `from_apk/Data` up to `from_apk`;
* the scene is split into entities (`.sc2`) and geometry (`.scg`);
* fog and the sun are scene components instead of a global material;
* the play area is a MapBorderComponent rather than landscape properties;
* the minimap and the loading screen are `.packed.webp` sprites without a
  frame descriptor;
* material effects are `.material` YAML documents with presets;
* skinned props play `.anim` clips (MaterialTemplates, clips below), and the
  small stones are a VegetationRenderObject whose clusters the client only
  generates at load time (vegetation helpers below).
"""
from __future__ import annotations

import atexit
import hashlib
import json
import math
import posixpath
import random
import shutil
import struct
import sys
import tempfile
from pathlib import Path

import numpy as np
import yaml

VERSION = "11.20"

# Where 11.20 keeps Strings/, maps.yaml and Materials/.
DATA_ROOT = "from_apk"

# vp-fog-props.slh of 11.20 declares these defaults for a scene that authors
# no linear range. An exponential scene never reads them; the manifest keeps
# them so its fog block has the shape every other map's has.
VP_FOG_START = 10.0
VP_FOG_END = 100.0

# DAVA Joint::INVALID_JOINT_INDEX, the parent of a skeleton's root.
NO_PARENT = 16777215


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def items(value):
    if value is None:
        return []
    if isinstance(value, list):
        return value
    if isinstance(value, dict):
        return [value[key] for key in sorted(value)]
    return [value]


# ------------------------------------------------------------------- the pack
class ClientPack:
    """A shipped 11.20 pack, unpacked on demand into a scratch mirror."""

    def __init__(self, root: Path):
        self.root = Path(root)
        if not self.root.is_dir():
            raise SystemExit(f"no Blitz 11.20 pack at {self.root}")
        self.scratch = Path(tempfile.mkdtemp(prefix="blitz_1120_"))
        atexit.register(shutil.rmtree, self.scratch, True)

    def shipped(self, relative: str) -> Path:
        """The file as the pack ships it: `<relative>.dvpl` or plain."""
        for candidate in (self.root / f"{relative}.dvpl", self.root / relative):
            if candidate.is_file():
                return candidate
        raise SystemExit(f"the 11.20 pack ships no {relative}")

    def has(self, relative: str) -> bool:
        return (self.root / f"{relative}.dvpl").is_file() or (self.root / relative).is_file()

    def local(self, relative: str) -> Path:
        """The unpacked file in the scratch mirror."""
        target = self.scratch / relative
        if not target.is_file():
            source = self.shipped(relative)
            data = source.read_bytes()
            if source.name.endswith(".dvpl"):
                import dvpl
                data = dvpl.unpack_bytes(data)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        return target

    def materialize_tree(self, relative: str, suffix: str = "") -> None:
        base = self.root / relative
        if not base.is_dir():
            raise SystemExit(f"the 11.20 pack has no {relative}")
        for path in sorted(base.rglob("*")):
            if not path.is_file():
                continue
            name = path.relative_to(self.root).as_posix()
            if name.endswith(".dvpl"):
                name = name[:-len(".dvpl")]
            if name.endswith(suffix):
                self.local(name)

    def materialize_textures(self, scene, client: str) -> list[str]:
        """Unpack the textures a scene's materials reference outside its map.

        A `.tex` reference names a texture descriptor the pack does not ship;
        the image beside it is `<stem>.astc.pvr` (or `.mali.pvr`/`.pvr`).
        """
        found = []
        for node in scene.get("dataNodes") or []:
            for reference in (node.get("textures") or {}).values():
                reference = str(reference)
                if not reference or reference.startswith("~res:"):
                    continue
                relative = posixpath.normpath(f"3d/Maps/{client}/{reference}")
                stem = relative[:-len(".tex")] if relative.endswith(".tex") else relative
                for suffix in (".astc.pvr", ".mali.pvr", ".pvr", ""):
                    if self.has(stem + suffix):
                        self.local(stem + suffix)
                        if not relative.startswith(f"3d/Maps/{client}/"):
                            found.append(stem + suffix)
                        break
        return sorted(set(found))

    def source(self, relative: str) -> dict:
        path = self.shipped(relative)
        return {"path": path.relative_to(self.root).as_posix(), "sha256": sha256(path)}


def load_scene(pack: ClientPack, client: str) -> dict:
    """The `.sc2` entities with the `.scg` geometry, shaped like a 4.8 scene."""
    import dava
    base = f"3d/Maps/{client}/{client}"
    scene = dava.read_sc2(pack.local(base + ".sc2").read_bytes())
    scene["geometry"] = dava.read_scg(pack.local(base + ".scg").read_bytes())
    scene["dataNodes"] = list(items(scene["scene"].get("#dataNodes")))
    return scene


def scene_component(scene, typename: str):
    """One component of the scene itself (not of an entity), or None."""
    holder = scene["scene"]
    sets = holder.get("#sceneComponentSets") or {}
    pools = [items(sets[name]) for name in sorted(sets)]
    pools.append(items(holder.get("#sceneComponents")))
    for pool in pools:
        for component in pool:
            if isinstance(component, dict) and component.get("comp.typename") == typename:
                return component
    return None


# ------------------------------------------------------------------ rotations
def quaternion_matrix(q) -> np.ndarray:
    """The column-vector rotation of a DAVA quaternion (x, y, z, w)."""
    x, y, z, w = (float(v) for v in q)
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def matrix_quaternion(m: np.ndarray) -> tuple[float, float, float, float]:
    """The unit quaternion (x, y, z, w) of a column-vector rotation."""
    trace = m[0, 0] + m[1, 1] + m[2, 2]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        w, x = 0.25 * s, (m[2, 1] - m[1, 2]) / s
        y, z = (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x = (m[2, 1] - m[1, 2]) / s, 0.25 * s
        y, z = (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s
        y, z = 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s
        y, z = (m[1, 2] + m[2, 1]) / s, 0.25 * s
    length = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    return (x / length, y / length, z / length, w / length)


def unity_point(p) -> list[float]:
    """DAVA (x, y, z) is Unity (x, z, y): a reflection."""
    return [float(p[0]), float(p[2]), float(p[1])]


def unity_quaternion(q) -> list[float]:
    """The same rotation seen through the (x, z, y) reflection.

    Mirroring the space mirrors the rotation axis and reverses the sense of
    the turn, so (x, y, z, w) becomes (-x, -z, -y, w). The map keeps dot
    products, so slerp and lerp commute with it.
    """
    return [-float(q[0]), -float(q[2]), -float(q[1]), float(q[3])]


class JointTransform:
    """DAVA JointTransform: a position, an orientation and one scale."""

    def __init__(self, position, orientation, scale: float):
        self.position = np.asarray(position, dtype=np.float64)
        self.orientation = tuple(float(v) for v in orientation)
        self.scale = float(scale)

    @classmethod
    def from_matrix(cls, values) -> "JointTransform":
        """JointTransform::Construct: Matrix4::Decomposition, scale = scale.x.

        DAVA matrices are row-vector, so the basis vectors are the rows and
        the translation is the last row.
        """
        m = np.array([float(v) for v in values], dtype=np.float64).reshape(4, 4)
        rows = m[:3, :3]
        scales = np.linalg.norm(rows, axis=1)
        scales[scales == 0] = 1.0
        rotation = (rows / scales[:, None]).T
        return cls(m[3, :3], matrix_quaternion(rotation), scales[0])

    def rotate(self, vectors: np.ndarray) -> np.ndarray:
        return vectors @ quaternion_matrix(self.orientation).T

    def apply(self, points: np.ndarray) -> np.ndarray:
        """ApplyToPoint: position + orientation * (point * scale)."""
        return self.position + self.rotate(points) * self.scale


# ---------------------------------------------------------------- environment
def scene_environment(port, config, scene) -> dict:
    """Fog and the sun of SceneRenderConfig- and DirectionalLightComponent.

    11.20 drops the global material: the scene authors its fog colour and
    density on the render config (no FOG_LINEAR means `1 - exp(-density *
    distance)`) and its sun as a scene component with its own rotation.
    """
    render = scene_component(scene, "SceneRenderConfigComponent")
    light = scene_component(scene, "DirectionalLightComponent")
    if render is None or light is None:
        raise SystemExit(f"{config.client} has no render config or no directional light")
    if not render.get("VERTEX_FOG"):
        raise SystemExit(f"{config.client} renders without fog, which this port does not expect")
    if render.get("FOG_LINEAR"):
        raise SystemExit(f"{config.client} authors linear fog on its render config")
    colour = [round(float(v), 6) for v in render["fogColor"][:3]]
    density = render.get("fogDensity")
    if len(colour) != 3 or density is None:
        raise SystemExit(f"{config.client} authors no fog colour or density")

    values = light.get("light") or {}
    rotation = ((light.get("transformNode") or {}).get("transform") or {}).get("rotation")
    if rotation is None:
        raise SystemExit(f"{config.client} has no sun rotation")
    # Light::SetPositionDirectionFromMatrix: the light travels along the
    # node's -Y (MapConfig.sun_axis), which is the second column of R.
    column = 1 if config.sun_axis == "-y" else 2
    dava_direction = -quaternion_matrix(rotation)[:, column]
    dava_direction /= np.linalg.norm(dava_direction) or 1.0
    if config.orientation == "dava":
        direction = (dava_direction[0], dava_direction[2], dava_direction[1])
    else:
        direction = (dava_direction[0], dava_direction[2], -dava_direction[1])
    direction = tuple(float(v) for v in direction)
    return {
        "fog": {
            "color": colour,
            "start": VP_FOG_START,
            "end": VP_FOG_END,
            "limit": float(render.get("fogLimit", 1.0)),
            "atmosphere": False,
            "atmosphereDistance": 0.0,
            "scattering": 1.0,
            "linear": False,
            "density": round(float(density), 6),
        },
        "ambient": [
            float(values.get("ambColor.r", 1.0)),
            float(values.get("ambColor.g", 1.0)),
            float(values.get("ambColor.b", 1.0)),
        ],
        "sunColor": [
            float(values.get("color.r", 1.0)),
            float(values.get("color.g", 1.0)),
            float(values.get("color.b", 1.0)),
        ],
        "sunBasis": port.sun_basis(direction),
        "sunDirection": [round(v, 6) for v in direction],
    }


def map_border(port, config, scene, bbox: tuple) -> dict:
    """The play area: MapBorderComponent's rect (minX, minY, maxX, maxY)."""
    for _, entity in port.named_entities(scene):
        for component in port.components(entity, "MapBorderComponent"):
            rect = component.get("mbc.rect")
            # An AABBox2 (min, max) the archive keeps as four raw floats.
            if isinstance(rect, (bytes, bytearray)) and len(rect) == 16:
                rect = struct.unpack("<4f", bytes(rect))
            if rect is None or len(rect) != 4:
                raise SystemExit(f"{config.client} has a map border without a rect")
            min_x, min_y, max_x, max_y = (float(v) for v in rect)
            if config.orientation == "dava":
                min_z, max_z = -max_y, -min_y
            else:
                min_z, max_z = min_y, max_y
            return {
                "min": [min_x, float(bbox[2]), min_z],
                "max": [max_x, float(bbox[5]), max_z],
            }
    raise SystemExit(f"{config.client} has no MapBorderComponent")


# ------------------------------------------------------------------------- UI
def minimap_candidates(map_id: str) -> list[str]:
    folder = f"Gfx/UI/BattleScreenHUD/minimap/{map_id}"
    return [f"{folder}/MiniMapSmall@2x.packed.webp", f"{folder}/MiniMapSmall.packed.webp"]


# The 4.8.0 MiniMapSmall.txt frame side the battle HUD is laid out for.
MINIMAP_FRAME_SIDE = 326


def minimap_area(port, terrain: dict, frame: list[int]) -> dict:
    """The ground under the minimap frame and the size the HUD draws it at.

    11.20 prefers the retina MiniMapSmall@2x, whose frame (505 px on the moon)
    is larger than the 4.8.0 ones. BattleMinimap resamples the whole frame
    into `size`, the 4.8.0 side with the frame's aspect kept, so every minimap
    shows at one size without cropping or re-encoding the picture.
    """
    area = port.minimap_area(terrain, frame)
    fit = MINIMAP_FRAME_SIDE / max(frame[2], frame[3])
    area["size"] = [round(frame[2] * fit), round(frame[3] * fit)]
    return area


def export_ui(pack: ClientPack, config, map_dir: Path) -> tuple[list[dict], list[int]]:
    """The client's minimap (retina copy first) and loading screen.

    11.20 ships no MiniMapSmall.txt: the sprite is the map padded to a power
    of two, so the frame is the bounding box of its alpha. The client draws
    the minimap translucent (alpha about 230), which the PNG keeps.
    """
    from PIL import Image
    sources = []
    minimap = next((name for name in minimap_candidates(config.id) if pack.has(name)), None)
    if minimap is None:
        raise SystemExit(f"the 11.20 pack has no minimap for {config.id}")
    with Image.open(pack.local(minimap)) as image:
        image.load()
        box = image.convert("RGBA").getchannel("A").getbbox()
        image.save(map_dir / "minimap.png", format="PNG")
    if box is None:
        raise SystemExit(f"the {config.id} minimap is fully transparent")
    frame = [box[0], box[1], box[2] - box[0], box[3] - box[1]]
    sources.append(pack.source(minimap))

    loading = f"Gfx/UI/BattleLoadingScreen/{config.id}/BackgroundLoading.packed.webp"
    with Image.open(pack.local(loading)) as image:
        image.save(map_dir / "loading_screen.png", format="PNG")
    sources.append(pack.source(loading))
    return sources, frame


# ------------------------------------------------------------------ materials
# blending.slh: BLENDING n -> (src, dst) as Unity BlendMode values.
BLENDING_MODES = {1: (5, 10), 2: (1, 1), 3: (5, 1), 7: (1, 10)}

# The client shader of an effect decides what the port draws it with.
SHADER_KINDS = {
    "materials": "surface",
    "lit-materials": "lit",
    "skyobject-materials": "sky",
}


class MaterialTemplates:
    """The `.material` effects of 11.20 with their presets, at HIGH quality.

    A material document is `Material: {Layers, Shader, UniqueDefines,
    QualityDependentUniqueDefines, RenderState, Presets}`; an NMaterial
    switches presets on through `enabledPresets`, and every enabled preset
    may replace the layer and the shader, add defines (`[BLENDING, n]` picks
    the blend factors) and override render state. The result has the shape
    of the 4.8 loader's, so client_map_scene builds one record type.
    """

    def __init__(self, pack: Path):
        self.root = Path(pack) / DATA_ROOT / "Materials"
        self.documents: dict[str, dict] = {}

    def document(self, name: str) -> dict:
        if name not in self.documents:
            path = self.root / name
            if not path.is_file():
                raise SystemExit(f"the 11.20 pack has no material template {name}")
            text = path.read_text(encoding="utf-8").replace("\r", "")
            self.documents[name] = yaml.safe_load(text)["Material"]
        return self.documents[name]

    def load(self, reference: str, presets=()) -> dict:
        name = reference.split("/")[-1]
        document = self.document(name)
        known = document.get("Presets") or {}
        unknown = sorted(set(presets) - set(known))
        if unknown:
            raise SystemExit(f"{name} has no preset {', '.join(unknown)}")
        layers = list(document.get("Layers") or [])
        shader = str(document.get("Shader") or "")
        defines = list(document.get("UniqueDefines") or [])
        defines += list((document.get("QualityDependentUniqueDefines") or {}).get("HIGH") or [])
        state = dict(document.get("RenderState") or {})
        for preset, body in known.items():
            if preset not in presets:
                continue
            body = body or {}
            layers = list(body.get("Layers") or layers)
            shader = str(body.get("Shader") or shader)
            defines += list(body.get("UniqueDefines") or [])
            state.update(body.get("RenderState") or {})
        names, blending = [], None
        for define in defines:
            if isinstance(define, list):
                names.append(str(define[0]))
                if define[0] == "BLENDING":
                    blending = int(define[1])
            else:
                names.append(str(define))
        blend = bool(state.get("blend", False))
        if blending is not None and blending not in BLENDING_MODES:
            raise SystemExit(f"{name} blends with an unknown BLENDING {blending}")
        src, dst = BLENDING_MODES.get(blending, (5, 10)) if blend else (1, 0)
        if not layers:
            raise SystemExit(f"{name} names no render layer")
        return {
            "effect": name,
            "layer": layers[0],
            "shader": shader,
            "defines": names,
            "blend": blend,
            "src": src,
            "dst": dst,
            "depthWrite": bool(state.get("depthWrite", True)),
            "cull": str(state.get("cullMode", "FACE_BACK")) == "FACE_BACK",
            "kind": SHADER_KINDS.get(shader.split("/")[-1]),
        }


# MapBorderRenderObject batches carry no material: the client builds the red
# ribbon's material in code. Vertex colour (255, 69, 69) fades from opaque at
# the ground to clear at the top, blended over the frame, seen from both sides.
BORDER_MATERIAL = {
    "effect": "MapBorder",
    "kind": "surface",
    "queue": 3000,
    "cull": False,
    "zWrite": False,
    "blend": True,
    "srcBlend": 5,
    "dstBlend": 10,
    "alphaTest": False,
    "cutoff": 0.5,
    "fog": True,
    "albedo": "",
    "lightmap": "",
    "lightmapST": [1, 1, 0, 0],
    "alphamask": "",
    "flatColor": [1, 1, 1, 1],
    "texShift": [0, 0],
    "texShiftSpeed": [0, 0],
    "speedTree": False,
    "leafColorMul": [0.5, 0.5, 0.5, 0.5],
    "leafOcclusion": [0.5, 0.0],
    "wind": False,
    "flowmap": "",
    "flow": [0.0, 0.0],
    "normalmap": "",
    "cubemap": "",
    "normalScale": [1, 1],
    "normal0Shift": [0, 0],
    "normal1Shift": [0, 0],
    "fresnel": [0, 1],
    "reflectionTint": [1, 1, 1],
    "vertexColor": True,
}


# ---------------------------------------------------------------- animations
ANIMATION_MAGIC = b"BLTZANM1"

# AnimationTrack channel targets and AnimationChannel interpolations.
CHANNEL_TARGETS = {"translation": 0, "rotation": 1, "scale": 2}
INTERPOLATION_LINEAR = 0
INTERPOLATION_SPHERICAL_LINEAR = 1

# ActionComponent: Action::TYPE_ANIMATION started on EVENT_ADDED_TO_SCENE.
ACTION_ANIMATION = 6
EVENT_ADDED_TO_SCENE = 1


def read_clip(path: Path) -> dict:
    import dava_anim
    return dava_anim.read_anim(str(path))


def clip_channels(clip: dict, joints: list[dict]) -> list[dict]:
    """The clip's channels bound to joints by uid, values in Unity axes.

    SkeletonAnimation::BindSkeleton binds a track to the joint with the same
    uid; a track without one (the duck clip's `1_stn_Stones_duck1`) is never
    evaluated.
    """
    index = {joint["uid"]: position for position, joint in enumerate(joints)}
    channels = []
    for node in clip["nodes"]:
        joint = index.get(node["uid"])
        if joint is None:
            continue
        for channel in node["channels"]:
            target = CHANNEL_TARGETS.get(channel["target"])
            if target is None:
                raise SystemExit(f"clip channel targets {channel['target']}")
            interpolation = int(channel["flags"])
            if interpolation not in (INTERPOLATION_LINEAR, INTERPOLATION_SPHERICAL_LINEAR):
                raise SystemExit(f"clip channel interpolates with {interpolation}")
            keys = []
            for time, value in channel["keys"]:
                if target == 0:
                    value = unity_point(value)
                elif target == 1:
                    value = unity_quaternion(value)
                keys.append((float(time), [float(v) for v in value]))
            channels.append({
                "joint": joint,
                "target": target,
                "interpolation": interpolation,
                "dimension": int(channel["dimension"]),
                "keys": keys,
            })
    return channels


def encode_clips(clips: list[tuple[float, list[dict]]]) -> bytes:
    """`BLTZANM1`, the clip count, then per clip its duration and channels.

    A channel is joint, target (0 translation, 1 rotation, 2 scale),
    interpolation (0 linear, 1 slerp), dimension and key count, then the
    keys as (time, value...) floats.
    """
    blob = [ANIMATION_MAGIC, struct.pack("<i", len(clips))]
    for duration, channels in clips:
        blob.append(struct.pack("<fi", duration, len(channels)))
        for channel in channels:
            blob.append(struct.pack("<5i", channel["joint"], channel["target"],
                                    channel["interpolation"], channel["dimension"],
                                    len(channel["keys"])))
            flat = [v for time, value in channel["keys"] for v in [time, *value]]
            blob.append(struct.pack("<%df" % len(flat), *flat))
    return b"".join(blob)


def evaluate_channel(channel: dict, time: float) -> list[float]:
    """AnimationChannel::Evaluate, for tests and audits of the export."""
    keys = channel["keys"]
    k = 0
    while k < len(keys) and keys[k][0] <= time:
        k += 1
    if k == 0:
        return list(keys[0][1])
    if k == len(keys):
        return list(keys[-1][1])
    (t0, v0), (t1, v1) = keys[k - 1], keys[k]
    t = (time - t0) / (t1 - t0)
    if channel["interpolation"] == INTERPOLATION_LINEAR:
        return [a + (b - a) * t for a, b in zip(v0, v1)]
    return dava_slerp(v0, v1, t)


def dava_slerp(q1, q2, t: float) -> list[float]:
    """Quaternion::Slerp followed by Normalize, as AnimationChannel uses it."""
    cosom = sum(a * b for a, b in zip(q1, q2))
    if cosom < 0.0:
        cosom = -cosom
        q2 = [-v for v in q2]
    if 1.0 - cosom > 0.05:
        omega = math.acos(min(1.0, cosom))
        sinom = math.sin(omega)
        scale0 = math.sin((1.0 - t) * omega) / sinom
        scale1 = math.sin(t * omega) / sinom
    else:
        scale0, scale1 = 1.0 - t, t
    q = [scale0 * a + scale1 * b for a, b in zip(q1, q2)]
    length = math.sqrt(sum(v * v for v in q)) or 1.0
    return [v / length for v in q]


def motion_timing(port, entity) -> dict:
    """When the client starts the entity's motion, and how often it plays.

    MotionSystem scales time by `motion.playbackRate`. An ActionComponent
    action of TYPE_ANIMATION on EVENT_ADDED_TO_SCENE starts it after
    `delay + RandFloat(delayVariation)` seconds and stops it after
    `stopAfterNRepeats` plays (-1: never, stored as 0).
    """
    motion = next(port.components(entity, "MotionComponent"), None)
    if motion is None:
        raise SystemExit(f"{entity.get('name')} is skinned but has no MotionComponent")
    timing = {
        "clip": str(motion.get("motion.filepath") or ""),
        "playbackRate": float(motion.get("motion.playbackRate", 1.0)),
        "autoplay": False,
        "delay": 0.0,
        "delayVariation": 0.0,
        "repeats": 0,
    }
    for component in port.components(entity, "ActionComponent"):
        for action in items({k: v for k, v in component.items() if isinstance(v, dict)}):
            if int(action.get("act.type", -1)) != ACTION_ANIMATION or \
                    int(action.get("act.event", -1)) != EVENT_ADDED_TO_SCENE:
                continue
            repeats = int(action.get("act.stopAfterNRepeats", -1))
            timing.update({
                "autoplay": True,
                "delay": float(action.get("act.delay", 0.0)),
                "delayVariation": float(action.get("act.delayVariation", 0.0)),
                "repeats": max(repeats, 0),
            })
            break
    return timing


# ----------------------------------------------------------------- vegetation
VEGETATION_MAGIC = b"BLTZVEG1"

# VegetationRenderObject: a 128 x 128 density map over the landscape, cells
# of four by four make one pattern, and the three resolutions draw a cell
# alone, two by two or the whole pattern.
DENSITY_MAP_SIZE = 128
PATTERN_CELLS = 4
RESOLUTION_SCALE = (1, 2, 4)
TILES_PER_ROW = (4, 2, 1)
CLUSTER_STRIDE = (1, 2, 4)

# The client seeds its generator from the clock, so no battle repeats
# another's stones; the port fixes the seed to keep its export reproducible.
VEGETATION_SEED = 1120


def vegetation_layers(chunk_set: dict) -> list[dict]:
    """LOD 0 of every variation of the custom geometry: one per layer.

    The pivot a cluster turns and scales about is the vertex with the lowest
    z of its layer (VegetationGeometry::GenerateClusterPositionData).
    """
    count = int(chunk_set.get("cgd.variationsCount", 0))
    layers = []
    for variation in range(count):
        lod = (chunk_set.get(f"cgd.variation.{variation}") or {}).get("cgd.lod.0")
        if not lod:
            raise SystemExit(f"vegetation variation {variation} has no LOD 0")
        positions = np.array([lod[f"cgd.lod.pos.{i}"] for i in range(int(lod["cgd.lod.posCount"]))],
                             dtype=np.float64)
        uvs = np.array([lod[f"cgd.lod.tex.{i}"] for i in range(int(lod["cgd.lod.texCoordCount"]))],
                       dtype=np.float64)
        indices = np.array([int(lod[f"cgd.lod.index.{i}"]) for i in range(int(lod["cgd.lod.indexCount"]))],
                           dtype=np.int64)
        if len(uvs) != len(positions) or indices.size % 3 or (indices.size and indices.max() >= len(positions)):
            raise SystemExit(f"vegetation variation {variation} is malformed")
        pivot = positions[int(np.argmin(positions[:, 2]))].copy()
        layers.append({"positions": positions, "uvs": uvs, "indices": indices, "pivot": pivot})
    return layers


def generate_clusters(limits, rotation, scale, unit: float, seed: int = VEGETATION_SEED) -> list[dict]:
    """The clusters of one pattern, as GenerateClusterPositionData lays them.

    Layer l places (4 m)^2 clusters, m = clusterLayerLimit[l], m by m in each
    density cell, each at a random spot of its cell, turned by
    rotationVariation * (rand - 0.5) degrees and scaled by
    1 - scaleVariation * rand.
    """
    rng = random.Random(seed)
    clusters = []
    for layer, limit in enumerate(limits):
        m = int(limit)
        if m <= 0:
            continue
        row = PATTERN_CELLS * m
        for cy in range(row):
            for cx in range(row):
                cell_x, cell_y = cx // m, cy // m
                clusters.append({
                    "layer": layer,
                    "limit": m,
                    "cx": cx,
                    "cy": cy,
                    "position": (cell_x * unit + unit * rng.random(),
                                 cell_y * unit + unit * rng.random()),
                    "rotation": float(rotation[layer]) * (rng.random() - 0.5),
                    "scale": 1.0 - float(scale[layer]) * rng.random(),
                })
    return clusters


def prepare_resolution_id(resolution: int, cx: int, cy: int) -> int:
    """The coarsest resolution a cluster still appears in, up to the next."""
    following = min(len(CLUSTER_STRIDE) - 1, resolution + 1)
    stride = CLUSTER_STRIDE[following]
    return following if cx % stride == 0 and cy % stride == 0 else resolution


def resolution_cells(clusters: list[dict], resolution: int) -> list[list[tuple[dict, int]]]:
    """GenerateClusterResolutionData: the clusters of each cell of a resolution."""
    step = RESOLUTION_SCALE[resolution]
    per_row = TILES_PER_ROW[resolution]
    cells: list[list[tuple[dict, int]]] = [[] for _ in range(per_row * per_row)]
    for cluster in clusters:
        cx, cy = cluster["cx"], cluster["cy"]
        if cx % step or cy % step:
            continue
        span = (PATTERN_CELLS // per_row) * cluster["limit"]
        cell = cx // span + (cy // span) * per_row
        cells[cell].append((cluster, prepare_resolution_id(resolution, cx, cy)))
    return cells


def resolution_mesh(layers: list[dict], cells) -> dict:
    """One resolution's pattern mesh in Unity axes, a submesh per cell.

    A vertex keeps the cluster's pivot and its resolution id beside it:
    grass-vp lifts the whole cluster to the height under the pivot and
    shrinks it towards the pivot by density, distance and LOD.
    """
    positions, uvs, pivots, submeshes = [], [], [], []
    base = 0
    for members in cells:
        triangles = []
        for cluster, resolution_id in members:
            layer = layers[cluster["layer"]]
            pivot = layer["pivot"]
            angle = math.radians(cluster["rotation"])
            c, s = math.cos(angle), math.sin(angle)
            turn = np.array([[c, -s, 0.0], [s, c, 0.0], [0.0, 0.0, 1.0]])
            origin = np.array([cluster["position"][0], cluster["position"][1], 0.0])
            local = ((layer["positions"] - pivot) * cluster["scale"]) @ turn.T + pivot + origin
            anchor = pivot + origin
            count = local.shape[0]
            positions.append(local[:, [0, 2, 1]])
            uvs.append(layer["uvs"])
            pivots.append(np.tile([anchor[0], anchor[2], anchor[1], float(resolution_id)], (count, 1)))
            # The (x, z, y) swap mirrors every cluster, so each winds the
            # other way round in Unity.
            triangles.append(layer["indices"].reshape(-1, 3)[:, [0, 2, 1]] + base)
            base += count
        submeshes.append(np.concatenate(triangles).reshape(-1) if triangles else np.zeros(0, np.int64))
    return {
        "positions": np.concatenate(positions).astype("<f4"),
        "uvs": np.concatenate(uvs).astype("<f4"),
        "pivots": np.concatenate(pivots).astype("<f4"),
        "submeshes": [indices.astype("<i4") for indices in submeshes],
    }


def encode_vegetation(density: np.ndarray, meshes: list[dict]) -> bytes:
    """`BLTZVEG1`, the density map, then each resolution's pattern mesh."""
    side = density.shape[0]
    blob = [VEGETATION_MAGIC, struct.pack("<i", side), density.astype(np.uint8).tobytes(),
            struct.pack("<i", len(meshes))]
    for mesh in meshes:
        count = mesh["positions"].shape[0]
        blob.append(struct.pack("<i", count))
        blob.append(mesh["positions"].tobytes())
        blob.append(mesh["uvs"].tobytes())
        blob.append(mesh["pivots"].tobytes())
        blob.append(struct.pack("<i", len(mesh["submeshes"])))
        for indices in mesh["submeshes"]:
            blob.append(struct.pack("<i", indices.size))
            blob.append(indices.tobytes())
    return b"".join(blob)


def vegetation_ranges(lod_ranges, unit: float, visibility: float) -> list[float]:
    """The squared distance bands of the three resolutions, flattened.

    A band ends where the next starts, at lodRanges cells; the last one runs
    out to visibilityDistance.x squared again (the client squares the
    already squared value), which only means it never ends first.
    """
    bounds = [(float(v) * unit) ** 2 for v in lod_ranges]
    flat = []
    for index in range(len(bounds)):
        upper = bounds[index + 1] if index + 1 < len(bounds) else float(visibility) ** 2
        flat += [bounds[index], upper]
    return flat


# ------------------------------------------------------------------- the port
def main(port, config, args) -> int:
    """Port one 11.20 map: the same resources and manifest as a 4.8 map."""
    import client_map_scene

    pack = ClientPack(args.pack or port.PACK_1120)
    client_dir = f"3d/Maps/{config.client}"
    pack.materialize_tree(client_dir)
    pack.materialize_tree(f"{DATA_ROOT}/Materials", ".material")
    destination = args.destination
    resources = destination / "Assets/Blitz/Resources"
    languages = json.loads((resources / "Data/lang.json").read_text(encoding="utf-8"))["languages"]
    for language in languages:
        pack.local(f"{DATA_ROOT}/Strings/{language}.yaml")
    scene = load_scene(pack, config.client)
    external = pack.materialize_textures(scene, config.client)

    scratch = pack.scratch
    source_dir = scratch / client_dir
    map_dir = resources / "Maps" / config.directory
    map_dir.mkdir(parents=True, exist_ok=True)
    scene_stem = f"{config.id}_scene"
    stats_path = map_dir / f"{scene_stem}.json"
    if args.skip_scene and not stats_path.is_file():
        raise SystemExit(f"--skip-scene needs a previously exported {scene_stem}.json")

    bbox = port.landscape_bbox(scene)
    terrain = port.write_terrain(source_dir / config.heightmap, map_dir / "terrain_height.bytes", bbox)
    for source_name, destination_name in [
        (config.colormap, "colormap.png"),
        (config.tilemask, "tilemask.png"),
        (config.tiletextures, "tiletextures.png"),
    ]:
        if not port.decode_texture(source_dir / source_name, map_dir / destination_name):
            raise SystemExit(f"cannot decode required map texture: {source_name}")

    if args.skip_scene:
        obj_stats = json.loads(stats_path.read_text(encoding="utf-8"))
    else:
        obj_stats = client_map_scene.export(port, config, scene, scratch, map_dir, scene_stem)
        stats_path.write_text(json.dumps(obj_stats, indent=2) + "\n", encoding="utf-8")

    environment = scene_environment(port, config, scene)
    ui_sources, frame = export_ui(pack, config, map_dir)
    name = port.write_localization(scratch, resources)

    height_grid = terrain.pop("__grid__")
    ground = port.ground_sampler(height_grid, bbox, terrain["step"])
    team_a, team_b = port.spawn_points(scene, height_grid, bbox, terrain["step"])
    landscape = port.landscape_material(scene)
    terrain["tileTexScale"] = landscape["tiling"][0]
    for index, colour in enumerate(landscape["colours"]):
        terrain[f"tileColour{index}"] = colour
    # tilemask-fp of 11.20 has no specular term, and with
    # LANDSCAPE_SEPARATE_LIGHTMAP_CHANNEL the colour map's alpha is the baked
    # light the colour is multiplied by, not a glossiness.
    terrain["client"] = {**landscape, "specular": False, "separateLightmapChannel": True}
    markers = port.ai_markers(scene, ground)

    music_sources = []
    intro_music = ""
    if config.intro_music:
        source = port.GODOT / config.intro_music
        if not source.is_file():
            raise SystemExit(f"the Godot project has no {config.intro_music}")
        intro_music = f"map_intro_{config.id}"
        audio = resources / "Audio" / f"{intro_music}{source.suffix}"
        audio.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, audio)
        music_sources.append({"path": f"blitz-legacy-godot/{config.intro_music}",
                              "sha256": sha256(source)})

    clips = sorted({
        posixpath.normpath(f"{client_dir}/{motion.get('motion.filepath')}")
        for _, entity in port.named_entities(scene)
        for motion in port.components(entity, "MotionComponent")
        if motion.get("motion.filepath")
    })
    manifest = {
        "id": config.id,
        "nameKey": f"map:{config.id}/name",
        "scene": f"{client_dir}/{config.client}.sc2",
        "externalScene": f"Maps/{config.directory}/{scene_stem}",
        "externalSceneSource": f"{client_dir}/{config.client}.sc2",
        "randomTiers": list(config.random_tiers),
        **({"orientation": config.orientation} if config.orientation != "mirrored" else {}),
        "sceneFormat": "client",
        "minimap": f"Maps/{config.directory}/minimap",
        "minimapArea": minimap_area(port, terrain, frame),
        "loadingScreen": f"Maps/{config.directory}/loading_screen",
        **({"introMusic": intro_music} if intro_music else {}),
        "sky": {"basis": [1, 0, 0, 0, 1, 0, 0, 0, 1], "origin": [0, 0, 0], "faces": []},
        "border": map_border(port, config, scene, bbox),
        "terrain": terrain,
        "environment": environment,
        "directionalLight": {
            "basis": environment["sunBasis"],
            "origin": [0, 64, 0],
            "color": environment["sunColor"],
            "energy": round(port.CLIENT_SUN_ENERGY * config.exposure, 6),
            # maps.yaml grants this map no shadowMapsAvailable, so the client
            # never renders the DirectionalShadowComponent's cascades on it.
            "shadows": False,
        },
        "spawns": {"teamA": team_a, "teamB": team_b},
        "aiMarkers": markers,
        "objects": obj_stats,
        "sources": [
            pack.source(f"{client_dir}/{config.client}.sc2"),
            pack.source(f"{client_dir}/{config.client}.scg"),
        ] + [
            pack.source(f"{client_dir}/{reference}")
            for reference in [config.heightmap, config.colormap, config.tilemask, config.tiletextures]
        ] + [pack.source(clip) for clip in clips] + [pack.source(texture) for texture in external] + [
            pack.source(f"{DATA_ROOT}/Strings/{language}.yaml") for language in languages
        ] + ui_sources + music_sources,
    }
    (resources / "Data").mkdir(parents=True, exist_ok=True)
    (resources / f"Data/map_{config.id}.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(
        {"map": config.id, "name": name, "terrain": terrain, "objects": obj_stats},
        indent=2, ensure_ascii=False))
    return 0
