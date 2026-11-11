"""Create verified, portable ZIPs from already prepared Glasswp packages."""
from pathlib import Path
import hashlib
import json
import os
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / 'artifacts'
TITLES = ('Glasswp-Purple', 'Glasswp-Angel', 'Glasswp-Corgi')


def sha(data):
    return hashlib.sha256(data).hexdigest()


results = []
for title in TITLES:
    folder = ARTIFACTS / title
    if folder.is_symlink() or folder.resolve() != ARTIFACTS.resolve() / title:
        raise ValueError(f'Package path must be a plain directory: {folder}')
    info = json.loads((folder / 'package-info.json').read_text(encoding='utf-8'))
    files = sorted(path for path in folder.rglob('*') if path.is_file())
    if any(path.is_symlink() for path in folder.rglob('*')):
        raise ValueError(f'Package must not contain links: {folder}')
    actual = {path.relative_to(folder).as_posix(): sha(path.read_bytes()) for path in files}
    expected = dict(info['files'], **{'package-info.json': sha((folder / 'package-info.json').read_bytes())})
    if info['title'] != title or actual != expected:
        raise ValueError(f'Package file inventory or hashes differ from manifest: {folder}')
    output = ARTIFACTS / (title + '.zip')
    if output.is_symlink():
        raise ValueError(f'ZIP output must not be a link: {output}')
    temporary = ARTIFACTS / ('.' + title + '-' + str(uuid.uuid4()) + '.zip')
    try:
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for path in files:
                archive.write(path, title + '/' + path.relative_to(folder).as_posix())
        with zipfile.ZipFile(temporary) as archive:
            entries = archive.namelist()
            if len(entries) != len(set(entries)) or archive.testzip() is not None:
                raise ValueError(f'ZIP includes duplicates or has a corrupt entry: {temporary}')
            archived = {entry[len(title) + 1:]: sha(archive.read(entry)) for entry in entries}
            if archived != actual or not all(entry.startswith(title + '/') for entry in entries):
                raise ValueError(f'ZIP file inventory or content hashes changed: {temporary}')
        os.replace(temporary, output)
    finally:
        if temporary.exists():
            temporary.unlink()
    results.append(dict(title=title, path=str(output), sourceBuildId=info['sourceBuildId'],
                        files=len(files), bytes=output.stat().st_size, sha256=sha(output.read_bytes())))

manifest = ARTIFACTS / 'Glasswp-workshop-zips.json'
manifest.write_text(json.dumps(dict(packages=results), ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(dict(packages=results, manifest=str(manifest)), ensure_ascii=False, indent=2))
