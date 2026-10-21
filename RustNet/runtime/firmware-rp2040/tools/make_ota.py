"""Build an OTA update for the Pico: the firmware linked for both slots.

The RP2040 runs its firmware in place from flash, so an image only runs at
the address it was linked for. An update therefore carries two builds — one
for slot A, one for slot B — and the device keeps the one for the slot it is
not running from (see rustnet-hal-rp2040/src/ota.rs). This builds both and
packs them as an `RNP2` pair:

    magic "RNP2", len_a: u32, len_b: u32, reserved: u32, image A, image B

each image the flash contents of its slot from the slot's first byte.

    python tools/make_ota.py [out.ota]          # from runtime/firmware-rp2040
    rustnet ota push out.ota --chip rp2040 --key keys/rustnet-signing.key

`--no-build` packs whatever the two target directories already hold.
"""
import os
import struct
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from elf2uf2 import flatten, load_segments  # noqa: E402

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FLASH_START = 0x10000000
SLOTS = {"A": 0x4000, "B": 0x80000}
SLOT_LEN = 0x7C000
BIN = "thumbv6m-none-eabi/release/rustnet-firmware-rp2040"
BUILDS = {
    "A": (["cargo", "build", "--release"], os.path.join(HERE, "target", BIN)),
    "B": (["cargo", "build", "--release", "--features", "slot-b", "--target-dir", "target/slot-b"],
          os.path.join(HERE, "target", "slot-b", BIN)),
}


def image_for(slot):
    command, elf = BUILDS[slot]
    if "--no-build" not in sys.argv:
        subprocess.run(command, cwd=HERE, check=True)
    base, image = flatten(load_segments(elf))
    want = FLASH_START + SLOTS[slot]
    if base != want:
        raise SystemExit(f"{elf} starts at {base:#010x}, not slot {slot} at {want:#010x}")
    if len(image) > SLOT_LEN:
        raise SystemExit(f"the slot {slot} image is {len(image)} bytes; a slot holds {SLOT_LEN}")
    return image


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out = args[0] if args else os.path.join(HERE, "target", "rustnet-pico.ota")
    a, b = image_for("A"), image_for("B")
    with open(out, "wb") as handle:
        handle.write(b"RNP2" + struct.pack("<III", len(a), len(b), 0) + a + b)
    print(f"slot A {len(a)} bytes, slot B {len(b)} bytes -> {out}")


if __name__ == "__main__":
    main()
