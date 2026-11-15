"""Embed only maintained local documentation and verbatim notices, never application binaries."""
from pathlib import Path
import argparse,zipfile
p=argparse.ArgumentParser();p.add_argument('payload',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
with zipfile.ZipFile(a.output,'x',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for f in sorted(a.payload.rglob('*')):
        if not f.is_file(): continue
        r=f.relative_to(a.payload)
        if len(r.parts)==1 and f.suffix in ('.md','.txt') or r.parts[0] in ('docs','Images','licenses') or str(r)=='LICENSE':
            z.write(f,r.as_posix())
