#!/usr/bin/env bash
# Install or update the latest published PTMagic Linux release.
# Usage: bash install-linux.sh /absolute/installation/directory
set -euo pipefail

if [[ $# -ne 1 || "${1:0:1}" != "/" || "$1" == "/" ]]; then
  echo "Usage: bash install-linux.sh /absolute/installation/directory" >&2
  exit 2
fi
command -v python3 >/dev/null || { echo "Python 3 is required" >&2; exit 1; }
command -v dotnet >/dev/null || { echo "ASP.NET Core runtime is required" >&2; exit 1; }

python3 - "$1" <<'PY'
import hashlib
import json
import os
import shutil
import stat
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath

destination = Path(sys.argv[1]).resolve()
if destination == Path('/') or (destination.exists() and not destination.is_dir()):
    sys.exit('Invalid installation directory')

headers = {'User-Agent': 'PTMagic-Linux-Installer', 'Accept': 'application/vnd.github+json'}
req = urllib.request.Request('https://api.github.com/repos/PTMagicians/PTMagic/releases/latest', headers=headers)
with urllib.request.urlopen(req, timeout=30) as response:
    release = json.load(response)
assets = [
    asset for asset in release.get('assets', [])
    if asset['name'].lower().endswith('.zip')
    and asset['name'].lower().startswith(('ptm_', 'ptmagic'))
]
if len(assets) != 1:
    sys.exit('Cannot uniquely identify the latest published PTMagic ZIP; no files changed')
print('PTMagic release:', release['tag_name'])
print('Destination:', destination)
print('Stop PTMagic and Monitor before updating an existing installation.')

with tempfile.TemporaryDirectory(prefix='ptmagic-installer-') as staging:
    archive_path = Path(staging) / 'release.zip'
    req = urllib.request.Request(assets[0]['browser_download_url'], headers=headers)
    with urllib.request.urlopen(req, timeout=120) as response, archive_path.open('wb') as output:
        shutil.copyfileobj(response, output)
    checksum = assets[0].get('digest', '')
    if checksum.startswith('sha256:'):
        with archive_path.open('rb') as source:
            actual = hashlib.file_digest(source, 'sha256').hexdigest()
        if actual.lower() != checksum[7:].lower():
            sys.exit('Release checksum mismatch; no files changed')

    with zipfile.ZipFile(archive_path) as archive:
        entries = [info for info in archive.infolist() if not info.is_dir()]
        paths = [PurePosixPath(info.filename) for info in entries]
        for info, path in zip(entries, paths):
            if (path.is_absolute() or '..' in path.parts or '\\' in info.filename
                    or stat.S_ISLNK(info.external_attr >> 16)):
                sys.exit('Unsafe archive entry; no files changed')
        # Current release layout: PTM_VERSION/PTMagic/...
        candidates = [
            p.parent for p in paths
            if p.name == 'PTMagic.dll'
            and PurePosixPath(*(p.parent.parts + ('Monitor', 'Monitor.dll'))) in paths
        ]
        if len(candidates) != 1:
            sys.exit('Cannot identify PTMagic/Monitor application root; no files changed')
        root = candidates[0]
        selected = [
            (info, Path(*path.parts[len(root.parts):]))
            for info, path in zip(entries, paths)
            if len(path.parts) > len(root.parts) and path.parts[:len(root.parts)] == root.parts
        ]
        names = [str(rel) for _, rel in selected]
        if len(names) != len(set(names)):
            sys.exit('Duplicate release paths; no files changed')

        def preserve(relative):
            parts = [p.lower() for p in relative.parts]
            name = relative.name.lower()
            return (
                any(p in {'logs', '_logs', 'data', '_data', '_presets'} for p in parts)
                or name.startswith('settings.')
                or name.startswith('appsettings.')
                or name in {'appsettings.json', 'nlog.config'}
            )

        for _, relative in selected:
            path = destination / relative
            if path.is_symlink():
                sys.exit('Refusing symlink destination: ' + str(path))
            parent = path.parent
            while parent == destination or destination in parent.parents:
                if parent.is_symlink():
                    sys.exit('Refusing symlink directory: ' + str(parent))
                if parent == destination:
                    break
                parent = parent.parent

        destination.mkdir(parents=True, exist_ok=True)
        updated = preserved = 0
        for info, relative in selected:
            target = destination / relative
            if target.exists() and preserve(relative):
                preserved += 1
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(info) as source, tempfile.NamedTemporaryFile(
                dir=target.parent, delete=False
            ) as output:
                temporary = Path(output.name)
                shutil.copyfileobj(source, output)
            try:
                mode = (info.external_attr >> 16) & 0o777
                os.chmod(temporary, mode or 0o644)
                os.replace(temporary, target)
            finally:
                temporary.unlink(missing_ok=True)
            updated += 1
print(f'Updated {updated} application files; preserved {preserved} existing settings/data files.')
print('Restart PTMagic and Monitor manually after updating.')
PY
