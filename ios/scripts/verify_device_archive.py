"""Audit our unsigned device archive/IPA. No signing, credentials, install or execution."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import plistlib
import re
import struct
import uuid
import zipfile


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(131072), b""):
            result.update(chunk)
    return result.hexdigest()


def read_plist(path):
    require(path.stat().st_size <= 1024 * 1024, "Plist exceeds audit limit")
    return plistlib.loads(path.read_bytes())


def macho_metadata(path):
    require(32 <= path.stat().st_size <= 128 * 1024 * 1024, "Executable size out of bounds")
    with path.open("rb") as stream:
        header = stream.read(32)
        magic, cpu, subtype, kind, count, size, _, _ = struct.unpack("<IiiIIIII", header)
        require(magic == 0xFEEDFACF and cpu == 0x100000C and kind == 2,
                "Expected a little-endian ARM64 Mach-O executable, not a simulator/FAT/test bundle")
        require(0 < count <= 4096 and size <= 1024 * 1024, "Invalid load-command bounds")
        commands = stream.read(size)
    require(len(commands) == size, "Truncated load commands")
    offset, build, image_uuid = 0, None, None
    for _ in range(count):
        require(offset + 8 <= size, "Truncated command")
        command, length = struct.unpack_from("<II", commands, offset)
        require(length >= 8 and length % 8 == 0 and offset + length <= size, "Invalid command length")
        if command == 0x32:  # LC_BUILD_VERSION; ARM64 alone does not distinguish iOS simulator.
            require(build is None and length >= 24, "Ambiguous build version")
            platform, minimum, sdk, tools = struct.unpack_from("<IIII", commands, offset + 8)
            require(platform == 2 and length == 24 + tools * 8, "Expected iOS device platform")
            build = (minimum, sdk)
        if command == 0x1B:
            require(image_uuid is None and length == 24, "Ambiguous UUID")
            image_uuid = str(uuid.UUID(bytes=commands[offset + 8:offset + 24]))
        offset += length
    require(offset == size and build is not None and image_uuid is not None, "Missing/inconsistent image metadata")
    def version(value):
        return f"{value >> 16}.{(value >> 8) & 255}.{value & 255}"
    return {"architecture": "arm64", "cpuSubtype": subtype, "imageUuid": image_uuid,
            "platform": "iOS", "minimumOS": version(build[0]), "sdk": version(build[1])}


def verify(archive, ipa, commit):
    require(re.fullmatch(r"[0-9a-f]{40}", commit) is not None, "Expected a full source commit")
    archive = Path(archive).resolve(strict=True)
    app = (archive / "Products/Applications/PhoneLinkCompanion.app").resolve(strict=True)
    require(app.is_relative_to(archive), "App escapes archive")
    info_path = app / "Info.plist"
    info = read_plist(info_path)
    require(info.get("CFBundleIdentifier") == "com.gabri.PhoneLinkCompanion", "Unexpected bundle identifier")
    require(info.get("CFBundleExecutable") == "PhoneLinkCompanion", "Unexpected executable name")
    require(info.get("CFBundlePackageType") == "APPL", "Not an application bundle")
    require(info.get("CFBundleSupportedPlatforms") == ["iPhoneOS"] and info.get("DTPlatformName") == "iphoneos",
            "Expected an iPhoneOS bundle, not a simulator build")
    require(info.get("MinimumOSVersion") == "26.0", "Unexpected deployment baseline")
    for key in ("CFBundleShortVersionString", "CFBundleVersion", "NSCameraUsageDescription",
                "NSContactsUsageDescription", "NSLocalNetworkUsageDescription"):
        value = info.get(key)
        require(isinstance(value, str) and value.strip() and "$(" not in value, "Missing/unexpanded bundle metadata")
    schemes = [scheme for item in info.get("CFBundleURLTypes", []) for scheme in item.get("CFBundleURLSchemes", [])]
    require("phonelink" in schemes and "_phonelink._tcp" in info.get("NSBonjourServices", []), "Missing pairing handlers")
    require(not (app / "embedded.mobileprovision").exists(), "Unexpected provisioning profile in unsigned artifact")
    require(not list(app.rglob("*.xctest")), "Test bundle must not ship in device app")
    privacy_path = app / "PrivacyInfo.xcprivacy"
    privacy = read_plist(privacy_path)
    require(privacy.get("NSPrivacyTracking") is False and privacy.get("NSPrivacyCollectedDataTypes") == [],
            "Unexpected developer collection/tracking declaration")
    require((app / "Assets.car").is_file(), "Compiled app assets missing")
    executable = (app / "PhoneLinkCompanion").resolve(strict=True)
    require(executable.is_relative_to(app), "Executable escapes app")
    metadata = macho_metadata(executable)
    require(metadata["minimumOS"] == "26.0.0", "Executable/plist minimum OS mismatch")
    executable_hash = digest(executable)
    app_files = {}
    for path in app.rglob("*"):
        require(not path.is_symlink(), "Unexpected symlink in device app")
        if path.is_file():
            app_files[path.relative_to(app).as_posix()] = path
    require(len(app_files) <= 10000 and sum(path.stat().st_size for path in app_files.values()) <= 256 * 1024 * 1024,
            "App contents exceed audit limit")
    ipa = Path(ipa).resolve(strict=True)
    require(0 < ipa.stat().st_size <= 256 * 1024 * 1024, "IPA size out of bounds")
    with zipfile.ZipFile(ipa) as package:
        entries = package.infolist()
        names = [item.filename for item in entries]
        require(len(entries) <= 10000 and len(set(names)) == len(names), "Duplicate/oversized IPA member list")
        require(sum(item.file_size for item in entries) <= 256 * 1024 * 1024, "IPA expanded size out of bounds")
        prefix = "Payload/PhoneLinkCompanion.app/"
        for name in names:
            require("\\" not in name and not PurePosixPath(name).is_absolute() and ".." not in PurePosixPath(name).parts,
                    "Unsafe IPA member path")
            require(name == "Payload/" or name.startswith(prefix), "Unexpected IPA root or other app")
            require(not name.endswith("embedded.mobileprovision") and ".xctest/" not in name, "Unexpected signed/test content")
        files = {item.filename[len(prefix):]: item for item in entries if not item.is_dir()}
        require(files.keys() == app_files.keys(), "IPA/archive file inventory mismatch")
        for relative, path in app_files.items():
            member = files[relative]
            require((member.external_attr >> 16) & 0o170000 != 0o120000, "Unexpected IPA symlink")
            require(member.file_size == path.stat().st_size, "IPA/archive file size mismatch")
            zipped_hash = hashlib.sha256()
            with package.open(member) as stream:
                for chunk in iter(lambda: stream.read(131072), b""):
                    zipped_hash.update(chunk)
            require(zipped_hash.hexdigest() == digest(path), "IPA/archive content mismatch")
    return {"schemaVersion": 1, "sourceCommit": commit, "bundleIdentifier": info["CFBundleIdentifier"],
            "appVersion": info["CFBundleShortVersionString"], "buildVersion": info["CFBundleVersion"],
            **metadata, "packagedFiles": len(app_files), "executableSha256": executable_hash, "ipaSha256": digest(ipa),
            "appleDevelopmentSigningVerified": False, "provisioningProfileIncluded": False,
            "physicalInstallationVerified": False,
            "warning": "Unsigned device build for owner-controlled signing only. Not ready to install; no credentials or provisioning profile included. Build/archive checks are not physical-device tests."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True)
    parser.add_argument("--ipa", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    report = verify(args.archive, args.ipa, args.commit)
    # Do not overwrite an existing report or put owner-specific paths into public output.
    with Path(args.report).open("x", encoding="utf-8") as output:
        json.dump(report, output, indent=2)
        output.write("\n")
    print(json.dumps(report, indent=2))
