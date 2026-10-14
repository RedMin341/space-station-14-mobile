"""Prepare the isolated, pinned Fobos Android build (run from repository root)."""
from pathlib import Path
import subprocess
import zipfile

CONTENT = "94c8636c48eb4d5797a5037f00438917f925337b"
ENGINE = "b55b7ecd9d9cfa0ca6c4aaaa419b11d497a3c30b"

for directory, expected in (("space-station-14", CONTENT), ("space-station-14/RobustToolbox", ENGINE)):
    actual = subprocess.check_output(["git", "-C", directory, "rev-parse", "HEAD"], text=True).strip()
    if actual != expected:
        raise SystemExit(f"Wrong revision in {directory}: {actual}; expected {expected}")

for f in [*Path("Content.Android").rglob("*"), *Path("space-station-14/RobustToolbox").rglob("*.cs")]:
    if f.is_file() and f.suffix in (".cs", ".csproj", ".xml"):
        s = f.read_text()
        s = s.replace("io.spacestation14.android", "io.ss14.touch.sandbox276").replace("io.ss14.touch.fobos276", "io.ss14.touch.sandbox276")
        s = s.replace('Label = "Space Station 14"', 'Label = "SS14 Песочница"')
        s = s.replace('<string name="app_name">Space Station 14</string>', '<string name="app_name">SS14 Песочница</string>')
        f.write_text(s)

# Embed engine assets only. Content resources must come from the matching server,
# never from the upstream 292 snapshot previously baked into resources.zip.
root = Path("space-station-14/RobustToolbox/Resources")
with zipfile.ZipFile("Content.Android/resources.zip", "w", zipfile.ZIP_DEFLATED) as z:
    for f in sorted(root.rglob("*")):
        if f.is_file():
            z.write(f, f.relative_to(root).as_posix())
with zipfile.ZipFile("Content.Android/resources.zip") as z:
    assert z.testzip() is None
    assert any(n.startswith("Shaders/") for n in z.namelist())
print(f"Fobos content {CONTENT}; engine 276.0.0 ({ENGINE})")
