"""Portable, non-executing .env loader and public-only Unity build resources."""
from __future__ import annotations

import os
from pathlib import Path
import re
import shlex
from urllib.parse import urlsplit
import uuid

PUBLIC_RESOURCES = {
    'BLITZ_SENTRY_DSN': 'Assets/Blitz/Resources/Data/sentry_dsn.txt',
    'BLITZ_DIRECT_SERVER': 'Assets/Blitz/Direct/Resources/BlitzDirect/server.txt',
    'BLITZ_PHOTON_APP_ID': 'Assets/Blitz/Network/Resources/BlitzNet/photon_app_id.txt',
}


def load_environment(root: Path, inherited=None) -> dict[str, str]:
    """Explicit process variables win. Values are literals, never shell code."""
    env = dict(os.environ if inherited is None else inherited)
    selected = env.get('BLITZ_ENV_FILE')
    path = Path(selected).expanduser() if selected else root / '.env'
    if not path.is_absolute():
        path = root / path
    if not selected and not path.exists():
        path = root / '.env.release.local'
    if selected and not path.is_file():
        raise ValueError('BLITZ_ENV_FILE does not name an existing file')
    if not path.exists():
        return env
    inherited_keys = set(env)
    for number, line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
        try:
            parts = shlex.split(line, comments=True, posix=True)
        except ValueError:
            raise ValueError(f'{path.name}:{number}: invalid quoting (value withheld)') from None
        if parts and parts[0] == 'export':
            parts = parts[1:]
        if not parts:
            continue
        if len(parts) != 1 or not re.fullmatch(r'[A-Za-z_][A-Za-z_0-9]*=.*', parts[0], re.S):
            raise ValueError(f'{path.name}:{number}: expected KEY=literal (value withheld)')
        key, value = parts[0].split('=', 1)
        if key not in inherited_keys:
            env[key] = value
    return env


def validate_public_settings(env: dict[str, str]) -> None:
    dsn = env.get('BLITZ_SENTRY_DSN', '').strip()
    if dsn:
        try:
            uri = urlsplit(dsn)
            valid = (uri.scheme == 'https' and uri.hostname and uri.username
                     and not uri.password and uri.path.strip('/')
                     and not uri.query and not uri.fragment and not any(c.isspace() for c in dsn))
        except ValueError:
            valid = False
        if not valid:
            raise ValueError('BLITZ_SENTRY_DSN must be a public HTTPS ingestion DSN, not a token')
    address = env.get('BLITZ_DIRECT_SERVER', '').strip()
    if address:
        try:
            uri = urlsplit('//' + address)
            valid = (uri.hostname and not uri.username and not uri.password
                     and not uri.path and not uri.query and not uri.fragment
                     and re.fullmatch(r'[A-Za-z0-9_.:\[\]-]+', address)
                     and (uri.port is None or 1 <= uri.port <= 65535))
        except ValueError:
            valid = False
        if not valid:
            raise ValueError('BLITZ_DIRECT_SERVER must be host[:port] or [IPv6]:port')
    app_id = env.get('BLITZ_PHOTON_APP_ID', '').strip()
    if app_id:
        try:
            uuid.UUID(app_id)
        except ValueError:
            raise ValueError('BLITZ_PHOTON_APP_ID must be a Fusion App ID GUID') from None


def write_runtime_settings(root: Path, env: dict[str, str]) -> None:
    """Whitelist only three public values. Never serialize the full environment."""
    validate_public_settings(env)
    for key, relative in PUBLIC_RESOURCES.items():
        path = root / relative
        value = env.get(key, '').strip()
        if not value:
            path.unlink(missing_ok=True)
            Path(str(path) + '.meta').unlink(missing_ok=True)
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            text = value + '\n'
            if not path.exists() or path.read_text(encoding='utf-8') != text:
                path.write_text(text, encoding='utf-8')


def require_android_signing(env: dict[str, str]) -> None:
    """Fail closed: no alpha APK may silently use Unity's debug certificate."""
    keys = ('BLITZ_ANDROID_KEYSTORE', 'BLITZ_ANDROID_KEYSTORE_PASSWORD',
            'BLITZ_ANDROID_KEY_ALIAS', 'BLITZ_ANDROID_KEY_PASSWORD')
    missing = [key for key in keys if not env.get(key)]
    if missing:
        raise ValueError('Release Android signing is required; configure ' + ', '.join(missing))
    if not Path(env['BLITZ_ANDROID_KEYSTORE']).is_file():
        raise ValueError('BLITZ_ANDROID_KEYSTORE must name an existing file')
