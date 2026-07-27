#!/usr/bin/env python3
"""Verify the private AAR field used by receiver heartbeat observability."""

from __future__ import annotations

import argparse
import hashlib
import io
import re
import struct
import sys
import zipfile
from pathlib import Path


EXPECTED_AAR_SHA256 = "19acfdbd409ecfe6ead1b12c00264fd491e926cdc20ca67231be8bc45d5df90a"
CLASS_PATH = "com/picovr/robotassistantlib/MediaDecoder.class"


class ClassReader:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.offset = 0

    def u1(self) -> int:
        return self._read(">B", 1)

    def u2(self) -> int:
        return self._read(">H", 2)

    def u4(self) -> int:
        return self._read(">I", 4)

    def take(self, size: int) -> bytes:
        if size < 0 or self.offset + size > len(self.data):
            raise ValueError("truncated Java class file")
        result = self.data[self.offset : self.offset + size]
        self.offset += size
        return result

    def _read(self, fmt: str, size: int) -> int:
        return struct.unpack(fmt, self.take(size))[0]


def parse_fields(class_bytes: bytes) -> list[tuple[str, str]]:
    reader = ClassReader(class_bytes)
    if reader.u4() != 0xCAFEBABE:
        raise ValueError("invalid Java class magic")
    reader.u2()
    reader.u2()

    constant_pool_count = reader.u2()
    constant_pool: list[str | None] = [None] * constant_pool_count
    index = 1
    while index < constant_pool_count:
        tag = reader.u1()
        if tag == 1:
            length = reader.u2()
            constant_pool[index] = reader.take(length).decode("utf-8", errors="strict")
        elif tag in (3, 4):
            reader.take(4)
        elif tag in (5, 6):
            reader.take(8)
            index += 1
        elif tag in (7, 8, 16, 19, 20):
            reader.take(2)
        elif tag in (9, 10, 11, 12, 17, 18):
            reader.take(4)
        elif tag == 15:
            reader.take(3)
        else:
            raise ValueError(f"unsupported Java constant-pool tag {tag}")
        index += 1

    reader.take(6)
    interface_count = reader.u2()
    reader.take(interface_count * 2)

    fields: list[tuple[str, str]] = []
    field_count = reader.u2()
    for _ in range(field_count):
        reader.u2()
        name_index = reader.u2()
        descriptor_index = reader.u2()
        name = constant_pool[name_index]
        descriptor = constant_pool[descriptor_index]
        if name is None or descriptor is None:
            raise ValueError("field name or descriptor is not UTF-8")
        fields.append((name, descriptor))
        attribute_count = reader.u2()
        for _ in range(attribute_count):
            reader.u2()
            reader.take(reader.u4())
    return fields


def camera_only_property(video_config: str, property_name: str) -> str | None:
    source_match = re.search(
        r'(?ms)^- name:[ \t]*"CAMERA_ONLY"[ \t]*\r?\n(?P<body>.*?)(?=^- name:|\Z)',
        video_config,
    )
    if source_match is None:
        return None

    property_match = re.search(
        rf'(?ms)^[ \t]+- name:[ \t]*"{re.escape(property_name)}"[ \t]*\r?\n'
        rf'(?P<body>.*?)(?=^[ \t]+- name:|\Z)',
        source_match.group("body"),
    )
    if property_match is None:
        return None

    value_match = re.search(
        r'(?m)^[ \t]+value:[ \t]*(?P<value>[^#\r\n]+)',
        property_match.group("body"),
    )
    return value_match.group("value").strip() if value_match is not None else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--strict-artifact",
        action="store_true",
        help="also require the audited AAR SHA-256",
    )
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[1]
    aar_path = repo_root / "Assets/Plugins/Android/robotassistant_lib-i18n-release.aar"
    if not aar_path.is_file():
        print(f"ERROR: missing {aar_path}", file=sys.stderr)
        return 1

    aar_bytes = aar_path.read_bytes()
    actual_hash = hashlib.sha256(aar_bytes).hexdigest()
    if actual_hash != EXPECTED_AAR_SHA256:
        message = f"AAR SHA-256 changed: expected {EXPECTED_AAR_SHA256}, got {actual_hash}"
        if args.strict_artifact:
            print(f"ERROR: {message}", file=sys.stderr)
            return 1
        print(f"WARNING: {message}", file=sys.stderr)

    with zipfile.ZipFile(io.BytesIO(aar_bytes)) as aar:
        classes_jar = aar.read("classes.jar")
    with zipfile.ZipFile(io.BytesIO(classes_jar)) as jar:
        class_bytes = jar.read(CLASS_PATH)

    fields = parse_fields(class_bytes)
    if ("lastTimestamp", "J") not in fields:
        print(
            f"ERROR: {CLASS_PATH} does not contain private dependency lastTimestamp:J",
            file=sys.stderr,
        )
        return 1

    video_config = (repo_root / "Assets/StreamingAssets/video_source.yml").read_text(
        encoding="utf-8-sig"
    )
    if not video_config.startswith("# xrobo_config_version: 3"):
        print("ERROR: bundled video source config version is not 3", file=sys.stderr)
        return 1
    if camera_only_property(video_config, "CamWidth") is None:
        print("ERROR: CAMERA_ONLY video source is missing", file=sys.stderr)
        return 1

    required_false_properties = (
        "UsePcServiceControl",
        "UseStereoSideBySide",
        "VisualDependentProfile",
    )
    for property_name in required_false_properties:
        if camera_only_property(video_config, property_name) != "false":
            print(f"ERROR: CAMERA_ONLY must set {property_name}=false", file=sys.stderr)
            return 1

    required_values = {
        "CamWidth": "408",
        "CamHeight": "720",
        "CamFPS": "30",
        "CamBitrate": "4000000",
        "FollowHead": "true",
        "ReceiverHeartbeatEnabled": "true",
        "ReceiverHeartbeatPort": "13580",
    }
    for property_name, expected_value in required_values.items():
        actual_value = camera_only_property(video_config, property_name)
        if actual_value != expected_value:
            print(
                f"ERROR: CAMERA_ONLY must set {property_name}={expected_value}; "
                f"got {actual_value!r}",
                file=sys.stderr,
            )
            return 1

    remote_source = (repo_root / "Assets/Scripts/Camera/RemoteCameraWindow.cs").read_text()
    notification_guards = (
        "bool notifyPcService = _notifyPcService && _receiverActive;",
        "if (notifyPcService)",
        "if (_notifyPcService)",
    )
    if any(snippet not in remote_source for snippet in notification_guards):
        print("ERROR: PC Service camera notifications are not fully guarded", file=sys.stderr)
        return 1
    if "_heartbeatSettings?.VisualDependent == true" not in remote_source:
        print(
            "ERROR: visual_profile_available is not gated by explicit visual-profile opt-in",
            file=sys.stderr,
        )
        return 1
    if "RestartStalledReceiverIfNeeded(now);" not in remote_source:
        print("ERROR: stalled direct receiver is not restarted automatically", file=sys.stderr)
        return 1

    config_manager_source = (
        repo_root / "Assets/Scripts/Conf/VideoSourceConfigManager.cs"
    ).read_text()
    migration_markers = (
        "BundledConfigurationVersion = 3",
        "GetConfigurationVersion(yamlPath) >= BundledConfigurationVersion",
        'targetPath + ".pre-v" + BundledConfigurationVersion + ".bak"',
    )
    if any(marker not in config_manager_source for marker in migration_markers):
        print("ERROR: persistent video source config migration is incomplete", file=sys.stderr)
        return 1
    if '_currentVideoSource = GetVideoSource("CAMERA_ONLY")' not in config_manager_source:
        print("ERROR: CAMERA_ONLY is not the default video source", file=sys.stderr)
        return 1

    ui_operate_source = (repo_root / "Assets/Scripts/UI/UIOperate.cs").read_text()
    source_dropdown_markers = (
        "sourceNames.IndexOf(sourceConfig.CurrentVideoSource?.name)",
        "videoSourceDropdown.SetValueWithoutNotify(selectedIndex)",
    )
    if any(marker not in ui_operate_source for marker in source_dropdown_markers):
        print("ERROR: video source dropdown does not follow the configured default", file=sys.stderr)
        return 1

    camera_input_source = (
        repo_root / "Assets/Scripts/UI/CameraSendInputDialog.cs"
    ).read_text()
    camera_ip_markers = (
        "string poseIp = TcpHandler.GetTargetIP",
        "IsUsablePoseIp(poseIp)",
        'PlayerPrefs.GetString("CameraSendInputDialog", "")',
        "AddressFamily.InterNetwork",
        "!IPAddress.IsLoopback(address)",
    )
    if any(marker not in camera_input_source for marker in camera_ip_markers):
        print("ERROR: camera IP does not default to the pose PC IP", file=sys.stderr)
        return 1

    control_client_source = (
        repo_root / "Assets/Scripts/Network/TcpClient.cs"
    ).read_text()
    managed_client_markers = (
        "ConnectTimeoutMs = 4000",
        "SendTimeoutMs = 1000",
        "FrameControlBody",
        "Interlocked.Increment(ref _generation)",
        "EventExecutor.ExecuteInUpdate",
        "MaxQueuedMessages = 32",
        "public static string LocalAddress",
    )
    if any(marker not in control_client_source for marker in managed_client_markers):
        print("ERROR: managed bounded camera-control client is incomplete", file=sys.stderr)
        return 1
    if "AndroidJavaProxy" in control_client_source:
        print("ERROR: camera-control client still depends on the unbounded AAR client", file=sys.stderr)
        return 1

    protocol_source = (
        repo_root / "Assets/Scripts/ReceiverHeartbeat/ReceiverHeartbeatProtocol.cs"
    ).read_text()
    canonical_fields = (
        '"version"',
        '"session_id"',
        '"sequence"',
        '"decoded_frames"',
        '"displayed_frames"',
        '"unity_update_hz"',
        '"decoder_state"',
    )
    missing_fields = [field for field in canonical_fields if field not in protocol_source]
    if missing_fields:
        print(f"ERROR: heartbeat serializer missing {missing_fields}", file=sys.stderr)
        return 1

    print(f"OK: {CLASS_PATH} contains lastTimestamp:J")
    print(f"OK: AAR SHA-256 {actual_hash}")
    print("OK: CAMERA_ONLY is direct, flat, observation-only, and uses UDP 13580")
    print("OK: persistent video source config upgrades to bundled v3")
    print("OK: CAMERA_ONLY and the pose PC IP are the camera defaults")
    print("OK: camera control uses managed timeout/generation/bounded framing")
    print("OK: visual-profile availability requires explicit opt-in")
    print("OK: gateway canonical heartbeat fields are present")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
