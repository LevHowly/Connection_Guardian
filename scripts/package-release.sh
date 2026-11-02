#!/usr/bin/env bash
# Builds the plugin, packs build/release/latest.zip and regenerates pluginmaster.json.
# Then publish a GitHub release named v<version> with latest.zip attached.
set -euo pipefail

root_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$root_dir"
dotnet build ConnectionGuardian.csproj -c Release --nologo
python3 - <<'PY'
import json
from pathlib import Path
import zipfile

root = Path.cwd()
output = root / "bin/Release"
manifest = json.loads((output / "ConnectionGuardian.json").read_text())
version = manifest["AssemblyVersion"]
tag = "v" + version.removesuffix(".0")
url = f'{manifest["RepoUrl"]}/releases/download/{tag}/latest.zip'
entry = dict(manifest)
entry.update(DownloadLinkInstall=url, DownloadLinkUpdate=url, DownloadLinkTesting=url,
             IsHide=False, IsTestingExclusive=False)
release = root / "build/release"
release.mkdir(parents=True, exist_ok=True)
files = [output / f"ConnectionGuardian{suffix}" for suffix in (".dll", ".json", ".deps.json")]
for file in files:
    if not file.is_file():
        raise FileNotFoundError(file)
with zipfile.ZipFile(release / "latest.zip", "w", zipfile.ZIP_DEFLATED) as archive:
    for file in files:
        archive.write(file, file.name)
(root / "pluginmaster.json").write_text(json.dumps([entry], indent=2) + "\n")
print(f"Packaged {version}: build/release/latest.zip; publish GitHub release {tag}")
PY
