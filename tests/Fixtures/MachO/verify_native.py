"""Compare project-owned Mach-O inspector output with Apple's native nm/otool."""
import json
import pathlib
import re
import subprocess
import sys

directory = pathlib.Path(sys.argv[1])
for architecture in ("arm64", "x86_64"):
    binary = directory / f"fixture-{architecture}.dylib"
    report = json.loads((directory / f"fixture-{architecture}.json").read_text())
    assert report["RuntimeAddressesAvailable"] is False
    image, = report["Images"]
    assert image["Architecture"] == architecture
    assert image["Uuid"] and not image["EntriesTruncated"]
    native_symbols = subprocess.check_output(["xcrun", "nm", "-n", str(binary)], text=True)
    native = re.search(r"^([0-9a-fA-F]+)\s+[A-Za-z]\s+_phone_link_probe\s*$", native_symbols, re.MULTILINE)
    assert native, native_symbols
    symbol, = [entry for entry in image["Entries"] if entry["Name"] == "_phone_link_probe" and entry["Source"] == "LC_SYMTAB"]
    address = int(native.group(1), 16)
    assert int(symbol["VirtualAddress"], 16) == address
    native_layout = subprocess.check_output(["xcrun", "otool", "-l", str(binary)], text=True)
    text_section = re.search(r"sectname __text\s+segname __TEXT\s+addr (0x[0-9a-fA-F]+)\s+size (0x[0-9a-fA-F]+)\s+offset (\d+)", native_layout)
    assert text_section, native_layout
    section_address, _, file_offset = text_section.groups()
    expected_file_offset = int(file_offset) + address - int(section_address, 16)
    assert int(symbol["FileOffset"], 16) == expected_file_offset
    exports = [entry for entry in image["Entries"] if entry["Name"] == "_phone_link_probe" and entry["Source"] == "EXPORT_TRIE"]
    assert exports and all(int(entry["VirtualAddress"], 16) == address for entry in exports)
    print(f"Verified {architecture}: symbol address and file mapping match nm/otool; exports match.")

fat = json.loads((directory / "fixture-universal.json").read_text())
assert len(fat["Images"]) == 2
assert {image["Architecture"] for image in fat["Images"]} == {"arm64", "x86_64"}
for image in fat["Images"]:
    thin = json.loads((directory / f"fixture-{image['Architecture']}.json").read_text())["Images"][0]
    assert image["Uuid"] == thin["Uuid"]
    fat_symbol, = [entry for entry in image["Entries"] if entry["Name"] == "_phone_link_probe" and entry["Source"] == "LC_SYMTAB"]
    thin_symbol, = [entry for entry in thin["Entries"] if entry["Name"] == "_phone_link_probe" and entry["Source"] == "LC_SYMTAB"]
    assert fat_symbol["VirtualAddress"] == thin_symbol["VirtualAddress"]
    assert int(fat_symbol["FileOffset"], 16) == int(image["SliceFileOffset"], 16) + int(thin_symbol["FileOffset"], 16)
print("Verified universal image: architecture, UUID and outer-file offsets match the native thin images.")
