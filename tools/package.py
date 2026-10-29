"""Package a release zip: python tools/package.py <repo> <build output dir> <zip path>. One top folder WaypointQueue."""
import json, re, sys, zipfile, os
repo, out, dest = sys.argv[1], sys.argv[2], sys.argv[3]
ver = re.search(r'AssemblyVersion\("([\d.]+)"\)', open(os.path.join(repo, 'WaypointQueue/Properties/AssemblyInfo.cs'), encoding='utf-8-sig').read()).group(1)
info = open(os.path.join(repo, 'Info.json'), encoding='utf-8').read()
info = re.sub(r'"version":\s*"[^"]+"', '"version": "%s"' % '.'.join(ver.split('.')[:3]), info)
json.loads(info)
with zipfile.ZipFile(dest, 'w', zipfile.ZIP_DEFLATED) as z:
    for f in ('WaypointQueue.dll', '0Harmony.dll'):
        z.write(os.path.join(out, f), 'WaypointQueue/' + f)
    z.writestr('WaypointQueue/Info.json', info)
    for f in ('LICENSE.txt', 'THIRD_PARTY_NOTICES.txt'):
        z.write(os.path.join(repo, f), 'WaypointQueue/' + f)
print(ver, dest)
