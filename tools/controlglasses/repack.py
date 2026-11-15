#!/usr/bin/env python3
"""Replace one rebuilt DEX while retaining the original APK's other content.

Produces an unsigned APK; alignment and normal development signing follow.
Requires the original APK hash to prevent accidentally patching another build.
"""
import argparse
import copy
import hashlib
import re
import zipfile
from pathlib import Path


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('original', type=Path)
    parser.add_argument('rebuilt', type=Path)
    parser.add_argument('dex', help='DEX entry containing DisplayModel, e.g. classes3.dex')
    parser.add_argument('output', type=Path)
    parser.add_argument('--source-sha256', required=True)
    args = parser.parse_args()
    if args.output.resolve() in {args.original.resolve(), args.rebuilt.resolve()}:
        raise ValueError('Output must not overwrite either input APK')
    if not re.fullmatch(r'classes(?:[2-9]|[1-9][0-9]+)?\.dex', args.dex):
        raise ValueError('Invalid DEX entry name')
    with args.original.open('rb') as original_file:
        digest = hashlib.file_digest(original_file, 'sha256').hexdigest()
    if digest != args.source_sha256:
        raise ValueError('Original APK hash mismatch')
    with zipfile.ZipFile(args.rebuilt) as rebuilt:
        dex = rebuilt.read(args.dex)
    if not dex.startswith(b'dex\n') or b'Air 2 Ultra' not in dex:
        raise ValueError('Expected patched DEX was not found')
    signature = re.compile(r'META-INF/(?:MANIFEST\.MF|[^/]+\.(?:SF|RSA|DSA|EC))', re.I)
    with zipfile.ZipFile(args.original) as original:
        if len(set(original.namelist())) != len(original.namelist()):
            raise ValueError('Duplicate original APK entries')
        if args.dex not in original.namelist():
            raise ValueError('Original APK lacks the requested DEX entry')
        with zipfile.ZipFile(args.output, 'w') as output:
            for info in original.infolist():
                if signature.fullmatch(info.filename):
                    continue
                # writestr mutates CRC/size/offset fields; never mutate metadata
                # owned by the reader that will verify the original entries.
                output.writestr(copy.copy(info), dex if info.filename == args.dex else original.read(info.filename))
        with zipfile.ZipFile(args.output) as output:
            changed = [name for name in output.namelist() if output.read(name) != original.read(name)]
            if changed != [args.dex]:
                raise ValueError(f'Unexpected changed entries: {changed}')
    print(f'Verified: only {args.dex} changed; original native libraries and resources preserved.')
