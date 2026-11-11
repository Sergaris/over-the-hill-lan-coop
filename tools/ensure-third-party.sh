#!/usr/bin/env bash
# Ensure third_party/ holds the authentic BepInEx zip pinned in manifest/supported-builds.json.
# Downloads (full GET, never resume/range) and verifies sha256; fails the build on mismatch.
# Does not commit the zip (.gitignore: /third_party/*.zip).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
MANIFEST="$ROOT/manifest/supported-builds.json"
python3 - "$MANIFEST" <<'PY'
import hashlib, json, os, sys, urllib.request

manifest_path = sys.argv[1]
doc = json.load(open(manifest_path, encoding="utf-8"))
bx = doc["bepinex"]
name = bx["zip"]
url = bx["url"]
want = bx["sha256"].lower()
dest_dir = os.path.join(os.path.dirname(manifest_path), "..", "third_party")
dest_dir = os.path.normpath(dest_dir)
os.makedirs(dest_dir, exist_ok=True)
dest = os.path.join(dest_dir, name)

def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

def ok() -> bool:
    return os.path.isfile(dest) and sha256_file(dest) == want

if ok():
    print(f"ensure-third-party: {name} ok sha256={want}")
    sys.exit(0)

print(f"ensure-third-party: fetching {url}", flush=True)
# Full download into a temp file — never curl -C / Range resume (a short partial at offset N
# yields authentic[N:] which is exactly the 0.2.6.1/0.2.6.2 truncated-embed failure mode).
tmp = dest + ".partial"
try:
    if os.path.exists(tmp):
        os.remove(tmp)
    req = urllib.request.Request(url, headers={"User-Agent": "OTH-LAN-ensure-third-party/0.2.7"})
    with urllib.request.urlopen(req, timeout=300) as r, open(tmp, "wb") as out:
        while True:
            chunk = r.read(1024 * 1024)
            if not chunk:
                break
            out.write(chunk)
    got = sha256_file(tmp)
    if got != want:
        os.remove(tmp)
        print(f"ensure-third-party: FAIL sha256 {got} != expected {want}", file=sys.stderr)
        sys.exit(1)
    os.replace(tmp, dest)
except Exception:
    if os.path.exists(tmp):
        try: os.remove(tmp)
        except OSError: pass
    raise

if not ok():
    print(f"ensure-third-party: FAIL after download", file=sys.stderr)
    sys.exit(1)
print(f"ensure-third-party: wrote {dest} sha256={want} size={os.path.getsize(dest)}")
PY
