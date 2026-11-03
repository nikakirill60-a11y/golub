#!/usr/bin/env python3
"""Export the tracks of the hangar music player.

Every track of Tools/hangar_music_playlist.json (id, artist, title, source file
and the one default track) is encoded from its source in --sources to Ogg
Vorbis at --quality (0 by default: about 60 kbit/s for stereo music, where the
sources are mostly 320 kbit/s MP3) as
Assets/StreamingAssets/HangarMusic/<id>.ogg, which the player loads from disk
on every platform, and the runtime playlist
Assets/Blitz/Resources/Data/hangar_music.json lists the tracks with their
length and their integrated EBU R128 loudness, measured on the encoded file:
the sources lie 25 LU apart, from quiet game themes to clipped dance tracks,
and the player evens them out with that figure. A track whose source and
encoder settings did not change since the last export keeps its file and its
measurement; the files of tracks the playlist no longer lists are deleted
together with their .meta.

The encode is bit-exact (no random Ogg serial numbers, no encoder version tag),
so the same ffmpeg writes the same files again.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SPEC = ROOT / 'Tools' / 'hangar_music_playlist.json'
OUTPUT = ROOT / 'Assets' / 'StreamingAssets' / 'HangarMusic'
PLAYLIST = ROOT / 'Assets' / 'Blitz' / 'Resources' / 'Data' / 'hangar_music.json'
SOURCES = Path.home() / 'Загрузки' / 'hangarmusic' / 'hangar audio music'
FOLDER = 'HangarMusic'
ID = re.compile(r'^[a-z0-9_]+$')
# The summary ebur128 prints at the end; a silent file reads -70 LUFS, its gate.
LOUDNESS = re.compile(r'Integrated loudness:\s*\n\s*I:\s*(-?[0-9.]+) LUFS')


def fail(message):
    sys.exit('export_hangar_music: ' + message)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            digest.update(block)
    return digest.hexdigest()


def probe(path, entries):
    result = subprocess.run(
        ['ffprobe', '-v', 'error', '-select_streams', 'a:0', '-show_entries', entries, '-of', 'json', str(path)],
        capture_output=True, text=True)
    if result.returncode != 0:
        fail(f'ffprobe {path}: {result.stderr.strip()}')
    return json.loads(result.stdout)


def load_spec():
    spec = json.loads(SPEC.read_text(encoding='utf-8'))
    tracks = spec['tracks']
    if not tracks:
        fail(f'{SPEC} lists no tracks')
    ids = [t['id'] for t in tracks]
    for track in tracks:
        if not ID.match(track['id']):
            fail(f'track id {track["id"]!r} is not lower_snake_case')
        if not track.get('artist') or not track.get('title') or not track.get('source'):
            fail(f'{track["id"]}: artist, title and source are required')
    if len(set(ids)) != len(ids):
        fail('duplicate track ids: ' + ', '.join(sorted({i for i in ids if ids.count(i) > 1})))
    defaults = [t['id'] for t in tracks if t.get('default')]
    if len(defaults) != 1:
        fail(f'the playlist needs exactly one default track, has {len(defaults)}')
    return tracks


def previous_entries():
    if not PLAYLIST.exists():
        return {}
    data = json.loads(PLAYLIST.read_text(encoding='utf-8'))
    return {t['id']: t for t in data.get('tracks', [])}


def encode(track, source, target, quality):
    channels = int(probe(source, 'stream=channels')['streams'][0]['channels'])
    temporary = target.with_name(target.name + '.part')
    command = ['ffmpeg', '-nostdin', '-v', 'error', '-y', '-i', str(source),
               '-map', '0:a:0', '-map_metadata', '-1', '-map_chapters', '-1',
               '-c:a', 'libvorbis', '-q:a', str(quality)]
    if channels > 2:
        command += ['-ac', '2']
    command += ['-fflags', '+bitexact', '-flags:a', '+bitexact', '-f', 'ogg', str(temporary)]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        temporary.unlink(missing_ok=True)
        fail(f'{track["id"]}: ffmpeg failed: {result.stderr.strip()}')
    os.replace(temporary, target)


def loudness(track, path):
    """Integrated loudness of the whole file in LUFS (EBU R128, ffmpeg's ebur128)."""
    result = subprocess.run(
        ['ffmpeg', '-nostdin', '-hide_banner', '-nostats', '-i', str(path),
         '-map', '0:a:0', '-af', 'ebur128=framelog=quiet', '-f', 'null', '-'],
        capture_output=True, text=True)
    found = LOUDNESS.search(result.stderr)
    if result.returncode != 0 or found is None:
        fail(f'{track["id"]}: cannot measure the loudness of {path}: {result.stderr.strip()[-400:]}')
    return float(found.group(1))


def export(track, sources, quality, encoder, previous, force):
    source = sources / track['source']
    if not source.is_file():
        fail(f'{track["id"]}: source {source} is missing')
    source_hash = sha256(source)
    target = OUTPUT / (track['id'] + '.ogg')
    old = previous.get(track['id'])
    reused = (not force and old is not None and target.is_file() and
              old.get('sourceSha256') == source_hash and old.get('encoder') == encoder and
              old.get('sha256') == sha256(target))
    if not reused:
        encode(track, source, target, quality)
    measured = old['loudness'] if reused and 'loudness' in old else loudness(track, target)
    info = probe(target, 'stream=sample_rate,channels:format=duration')
    stream = info['streams'][0]
    entry = {
        'id': track['id'],
        'artist': track['artist'],
        'title': track['title'],
        'file': target.name,
        'seconds': round(float(info['format']['duration']), 3),
        'sampleRate': int(stream['sample_rate']),
        'channels': int(stream['channels']),
        'loudness': round(measured, 1),
        'bytes': target.stat().st_size,
        'sha256': sha256(target),
        'source': track['source'],
        'sourceSha256': source_hash,
        'encoder': encoder,
    }
    if track.get('default'):
        entry['default'] = True
    return entry, reused


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--sources', type=Path, default=SOURCES, help='folder of the source files')
    parser.add_argument('--quality', type=int, default=0, help='libvorbis quality, -1..10')
    parser.add_argument('--jobs', type=int, default=os.cpu_count() or 1)
    parser.add_argument('--force', action='store_true', help='encode every track again')
    args = parser.parse_args()
    if not -1 <= args.quality <= 10:
        fail('--quality must be between -1 and 10')

    tracks = load_spec()
    previous = previous_entries()
    encoder = f'libvorbis q{args.quality}'
    OUTPUT.mkdir(parents=True, exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        results = list(pool.map(
            lambda t: export(t, args.sources, args.quality, encoder, previous, args.force), tracks))

    listed = {entry['file'] for entry, _ in results}
    stale = [p for p in OUTPUT.glob('*.ogg') if p.name not in listed]
    for path in stale:
        path.unlink()
        Path(str(path) + '.meta').unlink(missing_ok=True)

    playlist = {
        'generator': 'Tools/export_hangar_music.py',
        'encoder': encoder,
        'folder': FOLDER,
        'tracks': [entry for entry, _ in results],
    }
    PLAYLIST.write_text(json.dumps(playlist, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')

    encoded = sum(1 for _, reused in results if not reused)
    size = sum(entry['bytes'] for entry, _ in results)
    seconds = sum(entry['seconds'] for entry, _ in results)
    quiet = min(entry['loudness'] for entry, _ in results)
    loud = max(entry['loudness'] for entry, _ in results)
    print(f'{len(results)} tracks ({encoded} encoded, {len(results) - encoded} kept, {len(stale)} stale removed), '
          f'{size / 1e6:.1f} MB, {seconds / 3600:.2f} h, {size * 8 / seconds / 1000:.1f} kbit/s on average, '
          f'{quiet:.1f}..{loud:.1f} LUFS; {PLAYLIST.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
