import hashlib
from pathlib import Path
import plistlib
import struct
import sys
import tempfile
import unittest
import uuid
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from verify_device_archive import verify


class DeviceArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.root / "own.xcarchive"
        self.app = self.archive / "Products/Applications/PhoneLinkCompanion.app"
        self.app.mkdir(parents=True)
        self.ipa = self.root / "unsigned.ipa"
        self.info = {"CFBundleIdentifier": "com.gabri.PhoneLinkCompanion", "CFBundleExecutable": "PhoneLinkCompanion",
                     "CFBundlePackageType": "APPL", "CFBundleSupportedPlatforms": ["iPhoneOS"], "DTPlatformName": "iphoneos",
                     "MinimumOSVersion": "26.0", "CFBundleShortVersionString": "0.1.0", "CFBundleVersion": "1",
                     "NSCameraUsageDescription": "Camera reason", "NSContactsUsageDescription": "Contacts reason",
                     "NSMotionUsageDescription": "Explicit foreground sensor diagnostic reason",
                     "NSLocalNetworkUsageDescription": "Network reason", "NSBonjourServices": ["_phonelink._tcp"],
                     "CFBundleURLTypes": [{"CFBundleURLSchemes": ["phonelink"]}]}
        self.write_info()
        (self.app / "PrivacyInfo.xcprivacy").write_bytes(plistlib.dumps({"NSPrivacyTracking": False, "NSPrivacyCollectedDataTypes": []}))
        (self.app / "Assets.car").write_bytes(b"synthetic-assets")
        self.write_image()
        self.pack()

    def write_info(self):
        (self.app / "Info.plist").write_bytes(plistlib.dumps(self.info))

    def write_image(self, platform=2, cpu=0x100000C, minimum=26 << 16):
        commands = struct.pack("<IIIIII", 0x32, 24, platform, minimum, (26 << 16) | (2 << 8), 0)
        commands += struct.pack("<II", 0x1B, 24) + uuid.UUID("db7647be-f8f9-3e7f-8f89-f2ad988a92a9").bytes
        header = struct.pack("<IiiIIIII", 0xFEEDFACF, cpu, 0, 2, 2, len(commands), 0, 0)
        (self.app / "PhoneLinkCompanion").write_bytes(header + commands)

    def pack(self, extra=None):
        with zipfile.ZipFile(self.ipa, "w") as output:
            for path in self.app.rglob("*"):
                if path.is_file():
                    output.write(path, "Payload/PhoneLinkCompanion.app/" + path.relative_to(self.app).as_posix())
            if extra:
                output.writestr(extra, b"synthetic")

    def check(self):
        return verify(self.archive, self.ipa, "a" * 40)

    def test_matching_fixture_stays_explicitly_unsigned_and_not_physically_verified(self):
        before = hashlib.sha256(self.ipa.read_bytes()).hexdigest()
        result = self.check()
        self.assertEqual(result["ipaSha256"], before)
        self.assertEqual(result["platform"], "iOS")
        self.assertEqual(result["minimumOS"], "26.0.0")
        self.assertFalse(result["appleDevelopmentSigningVerified"])
        self.assertFalse(result["physicalInstallationVerified"])
        self.assertFalse(result["provisioningProfileIncluded"])
        self.assertEqual(hashlib.sha256(self.ipa.read_bytes()).hexdigest(), before)

    def test_arm64_simulator_is_not_a_device_binary(self):
        self.write_image(platform=7)
        self.pack()
        with self.assertRaisesRegex(ValueError, "device platform"):
            self.check()

    def test_x64_image_is_refused(self):
        self.write_image(cpu=0x1000007)
        self.pack()
        with self.assertRaisesRegex(ValueError, "ARM64"):
            self.check()

    def test_bundle_platform_cannot_claim_device_for_simulator(self):
        self.info["CFBundleSupportedPlatforms"] = ["iPhoneSimulator"]
        self.write_info()
        self.pack()
        with self.assertRaisesRegex(ValueError, "iPhoneOS"):
            self.check()

    def test_executable_deployment_must_match_plist(self):
        self.write_image(minimum=27 << 16)
        self.pack()
        with self.assertRaisesRegex(ValueError, "minimum OS mismatch"):
            self.check()

    def test_missing_permission_reason_is_refused(self):
        del self.info["NSCameraUsageDescription"]
        self.write_info()
        self.pack()
        with self.assertRaisesRegex(ValueError, "bundle metadata"):
            self.check()

    def test_missing_motion_reason_is_refused(self):
        del self.info["NSMotionUsageDescription"]
        self.write_info()
        self.pack()
        with self.assertRaisesRegex(ValueError, "bundle metadata"):
            self.check()

    def test_provisioning_profile_is_not_published(self):
        (self.app / "embedded.mobileprovision").write_bytes(b"synthetic-profile")
        self.pack()
        with self.assertRaisesRegex(ValueError, "provisioning profile"):
            self.check()

    def test_package_cannot_substitute_another_executable(self):
        (self.app / "PhoneLinkCompanion").write_bytes(b"wrong")
        # IPA is still the original valid fixture; archive mismatch is refused first.
        with self.assertRaisesRegex(ValueError, "size out of bounds"):
            self.check()

    def test_ipa_executable_must_match_archive(self):
        self.write_image(minimum=(26 << 16) | 1)
        self.pack()
        self.write_image()
        with self.assertRaisesRegex(ValueError, "content mismatch"):
            self.check()

    def test_ipa_cannot_omit_or_change_compiled_assets(self):
        (self.app / "Assets.car").write_bytes(b"other-assets")
        with self.assertRaisesRegex(ValueError, "(size|content) mismatch"):
            self.check()

    def test_unsafe_or_foreign_members_are_refused(self):
        for name in ("../private", "/private", "Payload/../private", "Payload\\private", "Other.app/executable"):
            with self.subTest(name=name):
                self.pack(extra=name)
                with self.assertRaises(ValueError):
                    self.check()

    def test_truncated_load_commands_fail(self):
        image = self.app / "PhoneLinkCompanion"
        image.write_bytes(image.read_bytes()[:-1])
        self.pack()
        with self.assertRaisesRegex(ValueError, "Truncated"):
            self.check()

    def test_commit_is_not_an_arbitrary_label(self):
        with self.assertRaisesRegex(ValueError, "source commit"):
            verify(self.archive, self.ipa, "unknown")


if __name__ == "__main__":
    unittest.main()
