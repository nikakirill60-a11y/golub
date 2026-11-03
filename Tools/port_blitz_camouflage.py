#!/usr/bin/env python3
"""Port the Blitz camouflage system: one test camouflage, per-tank support,
camouflage masks, render parameters, the 4.8 camouflage shop art and strings.

Per tank everything is read from the pack its card was ported from: cards
without ``source_version`` come from the 4.8.0 client (``BLITZ_PACK``), cards
with ``source_version == "11.20"`` from the 11.20 client (``BLITZ_1120_PACK``).

Support (the tank can wear the camouflage) is the client's own rule set:

* the vehicle XML has a ``<camouflage>`` block,
* the camouflage's ``vehicleFilter`` does not exclude the vehicle by its
  ``list.xml`` tags (``eventCamouflage_user`` for the test camouflage), and
* the scene material that draws the hull albedo binds a ``decalmask``, the
  TILED_DECAL_MASK input the client shader blends the camouflage with.
  Skin repaints (R71 IS-2 Berlin) draw the hull with a texture no scene
  material names - the Unity model porter binds the repaint by file name over
  the original UVs - so when the hull sheet matches no material and the scene
  binds exactly one decalmask, the hull sheet uses that mask.

Render parameters come from ``camouflages.yaml`` with its nation and vehicle
``override`` blocks (alpha, scale, offset; alpha defaults to 1, offset to 0).
The vehicle XML ``<tiling>`` is reported, not applied: every ported vehicle
carries the neutral "1 1 0 0".

Masks are the client ``CamouflageMasks/*_CM`` textures, decoded exactly like
the tank albedos (no flip), one PNG per source file. The camouflage texture is
the legacy (non-PBR) ``3d/Tanks/Camouflages/<texture>`` of each pack.

Policies that are not client data (the 4.8 server owned them):

* price: ``BASE_PRICE`` credits - the sample price CamouflagesShopProto.yaml
  authors on the camouflage tile - times the vehicle XML
  ``<camouflage><priceFactor>``, rounded to whole credits. 11.20 sold
  camouflages permanently and its XML carries 1.0 on almost every tier, so
  11.20 cards use the 4.8 tree rule ``PRICE_FACTOR_PER_TIER`` x tier
  (4.8 tier 1 = 0.2 ... tier 10 = 2.0);
* map kinds: ``MAP_KINDS`` names the camouflage slot (summer, winter, desert)
  each ported map uses; the client maps carry no such field;
* bonus: an installed camouflage whose slot matches the battle map adds
  ``CONCEALMENT_BONUS`` to the vehicle's camouflage factor
  (``#vehicle_customization:Camouflage/Universal/Descr``, ``%(number)``).
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import posixpath
import re
import sys
import tempfile
from pathlib import Path
from xml.etree import ElementTree as ET

import numpy as np
import yaml
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
RESOURCES = ROOT / "Assets/Blitz/Resources"
DATA = RESOURCES / "Data"
TEXTURES = RESOURCES / "Textures"
CAMO_ROOT = RESOURCES / "Camouflages"
ATLAS = DATA / "atlas_regions.json"
LANG = DATA / "lang.json"
CATALOG = DATA / "camouflages.json"
MANIFEST = DATA / "camouflage_source_manifest.json"

GODOT_TOOLS = Path.home() / "projects/blitz-legacy-godot/tools"
sys.path.insert(0, str(GODOT_TOOLS))
import dava  # noqa: E402
import dvpl  # noqa: E402
import pvr2png  # noqa: E402

CAMOUFLAGE = "the_stony_soil"
BASE_PRICE = 2800
PRICE_FACTOR_PER_TIER = 0.2
CONCEALMENT_BONUS = 0.02
SLOTS = ("summer", "winter", "desert")
MAP_KINDS = {
    "asia": "summer",
    "desert_train": "desert",
    "karieri": "summer",
    "lake": "desert",
    "moon": "winter",
    "ordeal": "summer",
    "rudniki": "summer",
    "sea_of_ducks": "summer",
}
PACK_480 = "480"
PACK_1120 = "1120"

UI_TEXTURE = "camouflage_ui"
UI_ATLAS_SIZE = (1024, 1024)
UI_PADDING = 2
# Gfx2 is the 2x art set: one virtual (yaml) unit is two texels.
UI_SCALE = 2

LANGUAGES = ("en", "ru")
SOURCE_STRING_KEYS = [
    "#camouflages:Shop/Title",
    "#camouflages:Slots/Summer",
    "#camouflages:Slots/Winter",
    "#camouflages:Slots/Desert",
    "#camouflages:Slots/NoAvailable",
    "#camouflages:Shop/Cost",
    "#camouflages:Shop/BuyCamouflage/Confirmation",
    "#camouflages:Shop/BuyCamouflage/Confirmation/Cost",
    "#camouflages:Shop/BuyCamouflage/OK",
    "#camouflages:Shop/BuyCamouflage/Cancel",
    "#vehicle_customization:Camouflage/Universal/Descr",
    "#vehicle_customization:Camouflage/Summer/Descr",
    "#vehicle_customization:Camouflage/Winter/Descr",
    "#vehicle_customization:Camouflage/Desert/Descr",
    "#vehicle_customization:Camouflage/Standard/Name",
    "#settings:Graphics/Camouflage/Title",
    "#settings:Graphics/Camouflage/Description",
    "#settings:Graphics/Camouflage/Off",
    "#settings:Graphics/Camouflage/ForPlayer",
    "#settings:Graphics/Camouflage/ForAll",
    "#common:DialogButton/Remove",
    "#common:DialogButton/Buy",
    "#common:DialogButton/SetUp",
    "tips/camo",
]


def pack_root(env: str, names) -> Path:
    value = os.environ.get(env)
    candidates = [Path(value)] if value else []
    candidates += [Path.home() / "projects" / name for name in names]
    for path in candidates:
        if path.is_dir():
            return path
    raise SystemExit(f"pack not found; set {env}")


PACKS = {
    PACK_480: pack_root("BLITZ_PACK", ("blitz480files", "wotblitz4.8.0files")),
    PACK_1120: pack_root("BLITZ_1120_PACK", ("blitz1120files", "wotblitz11.20.0files")),
}
SOURCES: dict[str, str] = {}


def sha256_bytes(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def find(pack: str, rel: str, suffixes=("",)) -> Path | None:
    base = PACKS[pack]
    for suffix in suffixes:
        for candidate in (rel + suffix, rel + suffix + ".dvpl"):
            path = base / candidate
            if path.is_file():
                return path
    return None


def read(pack: str, rel: str, suffixes=("",), record=True) -> tuple[bytes, Path]:
    path = find(pack, rel, suffixes)
    if path is None:
        raise FileNotFoundError(f"{pack}: {rel}{suffixes}")
    payload = path.read_bytes()
    if record:
        SOURCES[f"{pack}:{path.relative_to(PACKS[pack])}"] = sha256_bytes(payload)
    if path.suffix == ".dvpl":
        payload = dvpl.unpack_bytes(payload)
    return payload, path


def parse_xml(payload: bytes) -> ET.Element:
    text = payload.decode("utf-8", "replace").lstrip("\ufeff")
    return ET.fromstring(text)


# PVRv3 channel-specified A8 (channel 'a', 8 bits): some 11.20 masks use it.
PVR_A8 = (8 << 32) | ord("a")


def decode_mask(payload: bytes, name: str) -> Image.Image:
    """The mask value of a CamouflageMasks texture, mip 0, as an L image."""
    header = pvr2png.PvrHeader(payload)
    if header.pixel_format == PVR_A8:
        size = header.width * header.height
        data = payload[header.data_offset : header.data_offset + size]
        if len(data) != size:
            raise SystemExit(f"{name}: truncated A8 mask")
        return Image.frombytes("L", (header.width, header.height), data)
    # L8, ETC1 and ASTC masks carry the value in every colour channel.
    return decode_pvr(payload, name).getchannel("R")


def decode_pvr(payload: bytes, name: str):
    with tempfile.NamedTemporaryFile(suffix="_" + name, delete=False) as handle:
        handle.write(payload)
        temp = handle.name
    try:
        header, width, height, pixels = pvr2png.decode(temp, 0, False)
    finally:
        os.unlink(temp)
    return Image.frombytes("RGBA", (width, height), bytes(pixels))


# ---------------------------------------------------------------------------
# Camouflage definitions.
# ---------------------------------------------------------------------------

def camouflage_xml(pack: str):
    rel = "XML/item_defs/vehicles/common/camouflages.xml"
    if pack == PACK_1120:
        rel = "from_apk/" + rel
    root = parse_xml(read(pack, rel)[0])
    groups = {}
    for node in root.find("groups"):
        groups[node.tag] = {
            "name": (node.findtext("userString") or "").strip(),
            "rarity": int((node.findtext("rarity") or "0").strip()),
        }
    entry = None
    for parent in root.iter():
        child = parent.find(CAMOUFLAGE)
        if child is not None and child.find("id") is not None:
            entry = child
            break
    if entry is None:
        raise SystemExit(f"{pack}: {CAMOUFLAGE} missing from camouflages.xml")
    exclude = []
    for tags in entry.findall("vehicleFilter/exclude/vehicle/tags"):
        exclude += (tags.text or "").split()
    include = []
    for tags in entry.findall("vehicleFilter/include/vehicle/tags"):
        include += (tags.text or "").split()
    return {
        "id": int(entry.findtext("id").strip()),
        "name": entry.findtext("userString").strip(),
        "description": entry.findtext("description").strip(),
        "category": (entry.findtext("category") or "").strip(),
        "group": entry.findtext("group").strip(),
        "kinds": (entry.findtext("kind") or "").split(),
        "not_in_shop": (entry.findtext("notInShop") or "false").strip() == "true",
        "lockable": (entry.findtext("lockable") or "false").strip() == "true",
        "exclude_tags": exclude,
        "include_tags": include,
    }, groups


def camouflage_yaml(pack: str):
    rel = "from_apk/Data/camouflages.yaml" if pack == PACK_480 else "from_apk/camouflages.yaml"
    table = yaml.safe_load(read(pack, rel)[0].decode("utf-8"))
    if CAMOUFLAGE not in table:
        raise SystemExit(f"{pack}: {CAMOUFLAGE} missing from camouflages.yaml")
    return table[CAMOUFLAGE]


def render_params(entry, nation: str, vehicle: str):
    params = {
        "alpha": float(entry.get("alpha", 1.0)),
        "scale": [float(v) for v in entry.get("scale", [1.0, 1.0])],
        "offset": [float(v) for v in entry.get("offset", [0.0, 0.0])],
        "texture": entry["texture"],
    }
    layers = []
    nation_entry = (entry.get("override") or {}).get(nation)
    if nation_entry:
        layers.append(nation_entry)
        vehicle_entry = (nation_entry.get("override") or {}).get(vehicle)
        if vehicle_entry:
            layers.append(vehicle_entry)
    for layer in layers:
        if "alpha" in layer:
            params["alpha"] = float(layer["alpha"])
        if "scale" in layer:
            params["scale"] = [float(v) for v in layer["scale"]]
        if "offset" in layer:
            params["offset"] = [float(v) for v in layer["offset"]]
        if "texture" in layer:
            params["texture"] = layer["texture"]
    return params


# ---------------------------------------------------------------------------
# Vehicles.
# ---------------------------------------------------------------------------

LIST_CACHE: dict[tuple[str, str], dict[str, list[str]]] = {}


def vehicle_tags(pack: str, xml_rel: str, vehicle: str):
    list_rel = posixpath.join(posixpath.dirname(xml_rel), "list.xml")
    key = (pack, list_rel)
    if key not in LIST_CACHE:
        root = parse_xml(read(pack, list_rel)[0])
        LIST_CACHE[key] = {node.tag: (node.findtext("tags") or "").split() for node in root}
    return LIST_CACHE[key].get(vehicle)


def scene_materials(pack: str, scene_rel: str):
    payload, _ = read(pack, scene_rel)
    raw = dava.read_sc2(payload)
    nodes = raw.get("dataNodes") or raw["scene"].get("#dataNodes", [])
    materials = {}
    for node in nodes:
        if node.get("##name") == "NMaterial":
            materials[int.from_bytes(bytes(node["#id"]), "little")] = node
    return materials


def merged_textures(materials, key):
    chain = []
    seen = set()
    while key is not None and key not in seen:
        seen.add(key)
        material = materials.get(int(key))
        if material is None:
            break
        chain.append(material)
        key = material.get("parentMaterialKey")
    textures = {}
    for material in reversed(chain):
        config = material.get("configArchive_0", material.get("configArchive", {})) or {}
        textures.update(config.get("textures") or {})
        textures.update(material.get("textures") or {})
    return textures


def strip_texture_suffix(path: str) -> str:
    for suffix in (".mali.pvr", ".astc.pvr", ".pvr", ".tex"):
        if path.endswith(suffix):
            return path[: -len(suffix)]
    return path


def decal_masks(pack: str, scene_rel: str):
    """albedo source path (no suffix) -> decalmask source path (no suffix)."""
    scene_dir = posixpath.dirname(scene_rel)
    materials = scene_materials(pack, scene_rel)
    result = {}
    for key in materials:
        textures = merged_textures(materials, key)
        albedo = textures.get("albedo")
        mask = textures.get("decalmask")
        if not albedo or not mask:
            continue
        albedo_path = strip_texture_suffix(posixpath.normpath(posixpath.join(scene_dir, albedo)))
        mask_path = strip_texture_suffix(posixpath.normpath(posixpath.join(scene_dir, mask)))
        previous = result.get(albedo_path)
        if previous is not None and previous != mask_path:
            raise SystemExit(f"{scene_rel}: {albedo_path} binds two masks ({previous}, {mask_path})")
        result[albedo_path] = mask_path
    return result


MASK_SUFFIXES = (".mali.pvr", ".astc.pvr", ".pvr")
MASKS: dict[tuple[str, str], str] = {}


def export_mask(pack: str, mask_path: str) -> str:
    key = (pack, mask_path)
    if key in MASKS:
        return MASKS[key]
    payload, path = read(pack, mask_path, MASK_SUFFIXES)
    channel = decode_mask(payload, path.name.replace(".dvpl", ""))
    rel = mask_path.split("3d/Tanks/", 1)[-1]
    destination = CAMO_ROOT / "Masks" / pack / (rel + ".png")
    destination.parent.mkdir(parents=True, exist_ok=True)
    channel.save(destination, optimize=True)
    resource = destination.relative_to(RESOURCES).with_suffix("").as_posix()
    MASKS[key] = resource
    return resource


CAMO_TEXTURES: dict[tuple[str, str], str] = {}


def export_camouflage_texture(pack: str, texture: str) -> str:
    key = (pack, texture)
    if key in CAMO_TEXTURES:
        return CAMO_TEXTURES[key]
    rel = "3d/Tanks/Camouflages/" + texture
    payload, path = read(pack, rel, (".mali.pvr", ".astc.pvr", ".pvr"))
    image = decode_pvr(payload, path.name.replace(".dvpl", ""))
    destination = CAMO_ROOT / "Textures" / pack / (texture + ".png")
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination, optimize=True)
    resource = destination.relative_to(RESOURCES).with_suffix("").as_posix()
    CAMO_TEXTURES[key] = resource
    return resource


def price_factor(root: ET.Element):
    block = root.find("camouflage")
    if block is None:
        return None, None
    factor = float((block.findtext("priceFactor") or "1").strip())
    tiling = " ".join((block.findtext("tiling") or "1 1 0 0").split())
    return factor, tiling


def collect_tanks(definitions, yamls):
    cards = json.loads((DATA / "tanks.json").read_text(encoding="utf-8"))["tanks"]
    models = json.loads((DATA / "tank_models_source_manifest.json").read_text(encoding="utf-8"))["tanks"]
    supported = {}
    unsupported = {}
    tilings = {}
    repaints = []
    for card in cards:
        name = card["name"]
        pack = PACK_1120 if card.get("source_version") == "11.20" else PACK_480
        xml_rel = card["source"]
        vehicle = posixpath.splitext(posixpath.basename(xml_rel))[0]
        nation = posixpath.basename(posixpath.dirname(xml_rel))
        root = parse_xml(read(pack, xml_rel)[0])
        factor, tiling = price_factor(root)
        if factor is None:
            unsupported[name] = "vehicle XML has no <camouflage> block"
            continue
        if pack == PACK_1120:
            factor = round(PRICE_FACTOR_PER_TIER * int(card["lvl"]), 4)
        tilings[tiling] = tilings.get(tiling, 0) + 1
        tags = vehicle_tags(pack, xml_rel, vehicle)
        if tags is None:
            raise SystemExit(f"{name}: {vehicle} missing from {nation}/list.xml")
        definition = definitions[pack]
        excluded = [tag for tag in definition["exclude_tags"] if tag in tags]
        if excluded:
            unsupported[name] = "list.xml tag " + " ".join(excluded) + " excluded by the camouflage vehicleFilter"
            continue
        if definition["include_tags"] and not any(tag in tags for tag in definition["include_tags"]):
            unsupported[name] = "list.xml tags miss the camouflage vehicleFilter include"
            continue
        model = models.get(name)
        if model is None:
            raise SystemExit(f"{name}: no tank_models_source_manifest entry")
        masks_by_albedo = decal_masks(pack, model["source"])
        masks = {}
        body_id = model["id"]
        for texture in model["textures"]:
            destination = posixpath.splitext(texture["destination"])[0]
            if destination.endswith("_tracks"):
                continue
            mask = masks_by_albedo.get(strip_texture_suffix(texture["source"]))
            if mask is not None:
                masks[destination] = export_mask(pack, mask)
        if body_id not in masks:
            scene_masks = set(masks_by_albedo.values())
            if len(scene_masks) != 1:
                unsupported[name] = "hull albedo material binds no decalmask"
                continue
            masks[body_id] = export_mask(pack, scene_masks.pop())
            repaints.append(name)
        params = render_params(yamls[pack], nation, vehicle)
        texture = export_camouflage_texture(pack, params.pop("texture"))
        params["texture"] = texture
        supported[name] = {
            "pack": pack,
            "price_factor": factor,
            "price": int(round(BASE_PRICE * factor)),
            "masks": dict(sorted(masks.items())),
            "camouflages": {CAMOUFLAGE: params},
        }
    return supported, unsupported, tilings, repaints, len(cards)


# ---------------------------------------------------------------------------
# Shop art.
# ---------------------------------------------------------------------------

def read_descriptor(path: Path):
    lines = [line.strip() for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    width, height = (int(v) for v in lines[2].split()[:2])
    count = int(lines[3])
    frames = [tuple(int(v) for v in line.split()[:6]) for line in lines[4 : 4 + count]]
    if len(frames) != count:
        raise SystemExit(f"{path.name}: expected {count} frames, found {len(frames)}")
    return (width, height), frames


ATLAS_IMAGES: dict[Path, Image.Image] = {}


def sprite(name: str, frame: int = 0) -> Image.Image:
    """A Gfx2 sprite frame baked into its authored canvas."""
    # The pack keeps most UI sprites in Gfx2/ and the APK-only ones in
    # from_apk/Data/Gfx2/.
    candidates = [PACKS[PACK_480] / root / (name + ".txt") for root in ("Gfx2", "from_apk/Data/Gfx2")]
    descriptor = next((path for path in candidates if path.is_file()), None)
    if descriptor is None:
        raise SystemExit(f"missing sprite descriptor: {name}")
    SOURCES[f"{PACK_480}:{descriptor.relative_to(PACKS[PACK_480])}"] = sha256_bytes(descriptor.read_bytes())
    size, frames = read_descriptor(descriptor)
    texture = descriptor.parent / "texture0.mali.webp"
    if texture not in ATLAS_IMAGES:
        SOURCES[f"{PACK_480}:{texture.relative_to(PACKS[PACK_480])}"] = sha256_bytes(texture.read_bytes())
        ATLAS_IMAGES[texture] = Image.open(texture).convert("RGBA")
    atlas = ATLAS_IMAGES[texture]
    x, y, w, h, xoff, yoff = frames[frame]
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.paste(atlas.crop((x, y, x + w, y + h)), (xoff, yoff))
    return canvas


def as_float(image: Image.Image) -> np.ndarray:
    return np.asarray(image.convert("RGBA"), dtype=np.float64) / 255.0


def nine_slice(image: Image.Image, size, cap: int) -> np.ndarray:
    """DAVA stretch caps: corners keep their texels, edges and centre stretch."""
    width, height = size
    if cap <= 0:
        return as_float(image.resize(size, Image.BILINEAR))
    src_w, src_h = image.size
    xs = [0, cap, src_w - cap, src_w]
    ys = [0, cap, src_h - cap, src_h]
    xd = [0, cap, width - cap, width]
    yd = [0, cap, height - cap, height]
    result = Image.new("RGBA", size, (0, 0, 0, 0))
    for row in range(3):
        for col in range(3):
            box = (xs[col], ys[row], xs[col + 1], ys[row + 1])
            target = (xd[col + 1] - xd[col], yd[row + 1] - yd[row])
            if target[0] <= 0 or target[1] <= 0:
                continue
            result.paste(image.crop(box).resize(target, Image.BILINEAR), (xd[col], yd[row]))
    return as_float(result)


def tiled(image: Image.Image, size, texel: int = 1) -> np.ndarray:
    """A detail layer tiled from the top-left corner, ``texel`` px per texel."""
    if texel != 1:
        image = image.resize((image.width * texel, image.height * texel), Image.NEAREST)
    width, height = size
    tiles_x = -(-width // image.width)
    tiles_y = -(-height // image.height)
    sheet = Image.new("RGBA", (tiles_x * image.width, tiles_y * image.height))
    for ty in range(tiles_y):
        for tx in range(tiles_x):
            sheet.paste(image, (tx * image.width, ty * image.height))
    return as_float(sheet.crop((0, 0, width, height)))


def composite(size, mask, detail, gradient=None, contour=None, mode="blend") -> Image.Image:
    """composit-fp.sl of the 4.8 client (DRAW_TILED_MULTILAYER), straight alpha out."""
    mask_a = mask[..., 3]
    rgb = detail[..., :3]
    if gradient is not None:
        g_rgb = gradient[..., :3]
        if mode == "blend":
            rgb = rgb * (1.0 - gradient[..., 3:4]) + g_rgb * gradient[..., 3:4]
        elif mode == "overlay":
            low = 2.0 * rgb * g_rgb
            high = 1.0 - 2.0 * (1.0 - rgb) * (1.0 - g_rgb)
            rgb = np.where(rgb >= 0.5, high, low)
        elif mode == "multiply":
            rgb = rgb * g_rgb
        else:
            raise ValueError(mode)
    premult = rgb * mask_a[..., None]
    if contour is None:
        out_rgb = premult
        out_a = mask_a
    else:
        c_a = contour[..., 3]
        out_rgb = (contour[..., :3] - premult) * c_a[..., None] + premult
        out_a = mask_a + c_a - mask_a * c_a
    straight = np.where(out_a[..., None] > 1e-6, out_rgb / np.maximum(out_a[..., None], 1e-6), 0.0)
    result = np.concatenate([np.clip(straight, 0, 1), np.clip(out_a[..., None], 0, 1)], axis=-1)
    return Image.fromarray(np.round(result * 255.0).astype(np.uint8), "RGBA")


def multilayer(virtual_size, layers, cap=0, mode="blend", detail_image=None, detail_texel=1):
    """Compose one DRAW_TILED_MULTILAYER background at the 2x art scale."""
    size = (virtual_size[0] * UI_SCALE, virtual_size[1] * UI_SCALE)
    cap_px = cap * UI_SCALE
    mask = nine_slice(sprite(layers["mask"]), size, cap_px)
    detail_source = detail_image if detail_image is not None else sprite(layers["detail"])
    detail = tiled(detail_source, size, detail_texel)
    gradient = None
    if layers.get("gradient"):
        gradient = as_float(sprite(layers["gradient"]).resize(size, Image.BILINEAR))
    contour = None
    if layers.get("contour"):
        contour = nine_slice(sprite(layers["contour"]), size, cap_px)
    return composite(size, mask, detail, gradient, contour, mode)


COMMON_BUTTON = {
    "mask": "UI/CommonUIElements/Button/mask",
    "detail": "UI/CommonUIElements/Button/detail",
    "contour": "UI/CommonUIElements/Button/gray_contour",
}
LOBBY_BUTTON = {
    "mask": "UI/LobbyCommon/Button/mask",
    "detail": "UI/LobbyCommon/Button/detail",
    "gradient": "UI/LobbyCommon/Button/gradient",
    "contour": "UI/LobbyCommon/Button/countur",
}
SELECTION = {
    "mask": "UI/CommonUIElements/Selection/mask",
    "detail": "UI/CommonUIElements/Selection/detail",
    "gradient": "UI/CommonUIElements/Selection/gradient",
    "contour": "UI/CommonUIElements/Selection/contour",
}


def styled_button(color: str):
    """StyledButton/DefaultStyledButton in one ButtonStyle.yaml colour class."""
    return {
        "mask": "UI/CommonUIElements/Button/mask",
        "detail": "UI/CommonUIElements/Button/detail",
        "gradient": f"UI/CommonUIElements/Button/{color}_gradient",
        "contour": f"UI/CommonUIElements/Button/{color}_contour",
    }


def shop_images(camouflage_texture: Image.Image):
    images = {
        # Hangar.yaml TankCamouflages / InventorySlot.
        # Hangar.yaml: .slot_empty draws ItemButton frame 0, .slot_filled frame 1.
        "camo_hangar_slot": sprite("UI/Hangar/ItemButton", 0),
        "camo_hangar_slot_filled": sprite("UI/Hangar/ItemButton", 1),
        "camo_hangar_icon": sprite("UI/Hangar/IconCamouflage"),
        "camo_white_circle": sprite("UI/CommonUIElements/WhiteCircle"),
        # CamouflagesShopProto.yaml.
        "camo_group_gradient": sprite("UI/CamouflagesShop/group_element_gradient"),
        "camo_preview_icon": sprite("UI/CamouflagesShop/icon_camouflage_preview", 0),
        "camo_preview_icon_on": sprite("UI/CamouflagesShop/icon_camouflage_preview", 1),
        "camo_empty_slot": sprite("UI/InventoryScreen/EmptySlot"),
        "camo_credits_small": sprite("UI/LobbyCommon/CurrencyIconsSmall", 1),
        "camo_credits_big": sprite("UI/LobbyCommon/CurrencyIconsBig", 1),
        "camo_check_mark": sprite("UI/LobbyCommon/mark"),
        "camo_dropdown_arrow": sprite("UI/CommonUIElements/IconArrowDropDownMenu"),
        "camo_scroll": sprite("UI/CommonUIElements/VerticalScroll"),
        # CamouflageSlot: LobbyCommon button (Overlay gradient), 100 x 100.
        "camo_slot_button": multilayer((100, 100), LOBBY_BUTTON, 0, "overlay"),
        # CamouflageSlot/CamouflageIcon: the installed camouflage, 100 x 100.
        f"camo_slot_{CAMOUFLAGE}": multilayer(
            (100, 100), {"mask": COMMON_BUTTON["mask"]}, 20, "blend",
            camouflage_texture, UI_SCALE),
        # CamouflageGroupElement/button: the camouflage tile, 92 x 90.
        f"camo_tile_{CAMOUFLAGE}": multilayer(
            (92, 90), COMMON_BUTTON, 20, "blend", camouflage_texture, UI_SCALE),
        # Selection/Selection, tinted at runtime: selection-color-orange for
        # the pick, selection-color-green for an owned camouflage.
        "camo_selection_tile": multilayer((92, 90), SELECTION, 20, "blend"),
        "camo_selection_slot": multilayer((100, 100), SELECTION, 20, "blend"),
        # CamouflagesShop.yaml previewButton: 64 x 64
        # button-color-gray; ShopFooter.yaml Apply: the 222 x 64 StyledButton
        # in mode=button-payment (green) and mode=button-sell (red).
        "camo_button_gray": multilayer((64, 64), styled_button("gray"), 20, "overlay"),
        "camo_button_payment": multilayer((222, 64), styled_button("green"), 20, "overlay"),
        "camo_button_sell": multilayer((222, 64), styled_button("red"), 20, "overlay"),
    }
    return images


def shelf_pack(images):
    sheet = Image.new("RGBA", UI_ATLAS_SIZE, (0, 0, 0, 0))
    regions = {}
    pen_x = pen_y = shelf_h = 0
    for name in sorted(images, key=lambda n: (-images[n].height, n)):
        image = images[name]
        if pen_x + image.width > UI_ATLAS_SIZE[0]:
            pen_x = 0
            pen_y += shelf_h + UI_PADDING
            shelf_h = 0
        if pen_y + image.height > UI_ATLAS_SIZE[1]:
            raise SystemExit("camouflage atlas overflow; raise UI_ATLAS_SIZE")
        sheet.paste(image, (pen_x, pen_y))
        regions[name] = {"texture": UI_TEXTURE, "x": pen_x, "y": pen_y, "w": image.width, "h": image.height}
        pen_x += image.width + UI_PADDING
        shelf_h = max(shelf_h, image.height)
    used_h = pen_y + shelf_h
    height = 1
    while height < used_h:
        height *= 2
    return sheet.crop((0, 0, UI_ATLAS_SIZE[0], height)), regions


# ---------------------------------------------------------------------------
# Strings.
# ---------------------------------------------------------------------------

def read_strings(path: Path):
    pattern = re.compile(r'^"((?:[^"\\]|\\.)+)"\s*:\s*"(.*)"\s*$')
    values = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = pattern.match(line.strip())
        if match:
            values[match.group(1)] = match.group(2).replace('\\"', '"').replace("\\n", "\n")
    return values


def port_string(value: str) -> str:
    # <resource_small/> is the 4.8 rich-text price (amount + currency icon) and
    # %(number) the bonus percentage; both become ShopText.Fill slots.
    return value.replace("<resource_small/>", "%s").replace("%(number)", "%s")


def update_hangar_manifest(atlas, lang):
    path = DATA / "hangar_source_manifest.json"
    if not path.exists():
        return
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(manifest.get("atlas"), dict):
        manifest["atlas"]["textures"] = len(atlas["textures"])
        manifest["atlas"]["regions"] = len(atlas["regions"])
    if isinstance(manifest.get("localization"), dict):
        manifest["localization"]["keys"] = len(lang["keys"])
        manifest["localization"]["languages"] = len(lang["languages"])
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    definitions = {}
    groups = {}
    yamls = {}
    for pack in (PACK_480, PACK_1120):
        definitions[pack], groups[pack] = camouflage_xml(pack)
        yamls[pack] = camouflage_yaml(pack)
    definition = definitions[PACK_480]
    for field in ("id", "name", "description", "group", "kinds", "exclude_tags"):
        if definitions[PACK_1120][field] != definition[field]:
            raise SystemExit(f"{CAMOUFLAGE}: 4.8 and 11.20 disagree on {field}")
    if sorted(definition["kinds"]) != sorted(SLOTS):
        raise SystemExit(f"{CAMOUFLAGE}: expected a universal camouflage, got {definition['kinds']}")

    for folder in ("Masks", "Textures"):
        target = CAMO_ROOT / folder
        if target.is_dir():
            for png in target.rglob("*.png"):
                png.unlink()

    supported, unsupported, tilings, repaints, total = collect_tanks(definitions, yamls)

    texture_480 = Image.open(RESOURCES / (export_camouflage_texture(PACK_480, yamls[PACK_480]["texture"]) + ".png"))
    images = shop_images(texture_480.convert("RGBA"))
    sheet, regions = shelf_pack(images)
    TEXTURES.mkdir(parents=True, exist_ok=True)
    sheet.save(TEXTURES / f"{UI_TEXTURE}.png", optimize=True)
    atlas = json.loads(ATLAS.read_text(encoding="utf-8"))
    atlas["regions"] = {n: r for n, r in atlas["regions"].items() if r.get("texture") != UI_TEXTURE}
    atlas["regions"].update(regions)
    atlas["textures"][UI_TEXTURE] = {"width": sheet.width, "height": sheet.height}
    ATLAS.write_text(json.dumps(atlas, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    strings_dir = PACKS[PACK_480] / "from_apk/Data/Strings"
    tables = {}
    for code in LANGUAGES:
        path = strings_dir / f"{code}.yaml"
        SOURCES[f"{PACK_480}:{path.relative_to(PACKS[PACK_480])}"] = sha256_bytes(path.read_bytes())
        tables[code] = read_strings(path)
    lang = json.loads(LANG.read_text(encoding="utf-8"))
    keys = lang["keys"]
    string_keys = SOURCE_STRING_KEYS + [definition["name"], groups[PACK_480][definition["group"]]["name"]]
    for key in dict.fromkeys(string_keys):
        entry = keys.setdefault(key, {})
        for code in LANGUAGES:
            value = tables[code].get(key)
            if value is None:
                raise SystemExit(f"missing string {key} in {code}.yaml")
            entry[code] = port_string(value)
    LANG.write_text(json.dumps(lang, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    update_hangar_manifest(atlas, lang)

    group = groups[PACK_480][definition["group"]]
    catalog = {
        "generator": "Tools/port_blitz_camouflage.py",
        "policy": {
            "price": f"{BASE_PRICE} credits (CamouflagesShopProto sample price) x vehicle <camouflage><priceFactor>;"
                     f" 11.20 cards x {PRICE_FACTOR_PER_TIER} per tier (4.8 tree rule)",
            "map_kinds": "documented port table; the client maps carry no camouflage kind",
            "bonus": f"+{CONCEALMENT_BONUS} camouflage factor while the slot of the battle map kind is installed",
            "support": "vehicle <camouflage> block, vehicleFilter tags, hull albedo decalmask"
                       " (repainted hulls: the single scene decalmask)",
        },
        "base_price": BASE_PRICE,
        "concealment_bonus": CONCEALMENT_BONUS,
        "slots": list(SLOTS),
        "map_kinds": dict(sorted(MAP_KINDS.items())),
        "groups": {definition["group"]: group},
        "camouflages": {
            CAMOUFLAGE: {
                "id": definition["id"],
                "name": definition["name"],
                "description": definition["description"],
                "group": definition["group"],
                "kinds": [kind for kind in SLOTS if kind in definition["kinds"]],
                "category": definition["category"],
                "tile": f"camo_tile_{CAMOUFLAGE}",
                "slot_icon": f"camo_slot_{CAMOUFLAGE}",
            },
        },
        "tanks": dict(sorted(supported.items())),
        "unsupported": dict(sorted(unsupported.items())),
    }
    CATALOG.write_text(json.dumps(catalog, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    by_pack = {}
    for record in supported.values():
        by_pack[record["pack"]] = by_pack.get(record["pack"], 0) + 1
    MANIFEST.write_text(json.dumps({
        "generator": "Tools/port_blitz_camouflage.py",
        "sources": [{"path": key, "sha256": value} for key, value in sorted(SOURCES.items())],
        "tanks": total,
        "supported": len(supported),
        "supported_by_pack": by_pack,
        "unsupported": len(unsupported),
        "masks": len(set(MASKS.values())),
        "camouflage_textures": sorted(set(CAMO_TEXTURES.values())),
        "xml_tiling": tilings,
        "repainted_hull_masks": sorted(repaints),
        "ui": {"texture": UI_TEXTURE, "width": sheet.width, "height": sheet.height, "regions": len(regions)},
        "strings": len(dict.fromkeys(string_keys)),
    }, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"tanks {total}: supported {len(supported)} {by_pack}, unsupported {len(unsupported)}")
    print(f"masks {len(set(MASKS.values()))}, camouflage textures {sorted(set(CAMO_TEXTURES.values()))}")
    print(f"xml tiling {tilings}")
    print(f"repainted hulls using the scene decalmask: {sorted(repaints)}")
    for name, reason in sorted(unsupported.items()):
        print(f"  unsupported {name}: {reason}")


if __name__ == "__main__":
    main()
