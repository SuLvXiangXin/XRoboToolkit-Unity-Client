#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
vr_root="$(cd -- "${repo_root}/.." && pwd)"
unity_root="${UNITY_ROOT:-${vr_root}/.tools/unity-2022.3.16f1-global/Editor}"
jdk_bin="${unity_root}/Data/PlaybackEngines/AndroidPlayer/OpenJDK/bin"
android_jar="${unity_root}/Data/PlaybackEngines/AndroidPlayer/SDK/platforms/android-31/android.jar"
fbo_aar="${repo_root}/Assets/Plugins/Android/fbolibrary.aar"
output="${repo_root}/Assets/Plugins/Android/xrobot-persistent-media-decoder.aar"
source_root="${repo_root}/Tools/android_receiver/src"
manifest="${repo_root}/Tools/android_receiver/AndroidManifest.xml"

for required in "${jdk_bin}/javac" "${jdk_bin}/jar" "${android_jar}" "${fbo_aar}" "${manifest}"; do
    if [[ ! -e "${required}" ]]; then
        printf 'Missing Android receiver build dependency: %s\n' "${required}" >&2
        exit 1
    fi
done

work_dir="$(mktemp -d)"
trap 'rm -rf -- "${work_dir}"' EXIT
mkdir -p "${work_dir}/fbo" "${work_dir}/classes" "${work_dir}/aar"
unzip -q "${fbo_aar}" classes.jar -d "${work_dir}/fbo"

"${jdk_bin}/javac" \
    -source 8 -target 8 \
    -classpath "${android_jar}:${work_dir}/fbo/classes.jar" \
    -d "${work_dir}/classes" \
    "${source_root}/com/xrobotoolkit/video/PersistentMediaDecoder.java"

find "${work_dir}/classes" -exec touch -t 198001010000 {} +
"${jdk_bin}/jar" cMf "${work_dir}/aar/classes.jar" -C "${work_dir}/classes" .
cp "${manifest}" "${work_dir}/aar/AndroidManifest.xml"
touch -t 198001010000 "${work_dir}/aar/classes.jar" "${work_dir}/aar/AndroidManifest.xml"
rm -f -- "${output}"
(cd "${work_dir}/aar" && zip -X -q "${output}" AndroidManifest.xml classes.jar)
sha256sum "${output}"
