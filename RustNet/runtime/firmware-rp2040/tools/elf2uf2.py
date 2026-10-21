"""Pack an RP2040 ELF into a UF2 the BOOTSEL drive will accept.

The UF2 format is deliberately trivial — 512-byte blocks, each carrying at
most 256 bytes of payload and the address it belongs at — and the bootloader
checks a family ID rather than parsing an ELF. Doing the conversion here
rather than installing `elf2uf2-rs` keeps this port's toolchain to a Rust
target and a Python interpreter, which is what the rest of the repository
already assumes.

Only PT_LOAD segments with content are emitted, and only ones in flash: a
segment whose physical address is in SRAM is initialised data that the
startup code copies there, and writing it to flash at its virtual address
would land it on top of the program.

The flash segments are flattened into one image before being cut into blocks,
which matters more than it sounds. A linked RP2040 image has segments at
addresses like 0x100001c0 — neither 256-aligned nor abutting — and emitting
blocks per segment produces two faults at once: a short final block that
declares a full 256 bytes writes padding over the start of the next segment,
and a block whose address is not page-aligned is not where the bootloader
puts it. The symptom is a board that accepts the UF2 and comes straight back
as BOOTSEL with nothing to say about why. This port shipped that bug once.

Since OTA A/B, a bootable Pico needs two programs: the slot picker at
offset 0 (`boot/`, carrying the second stage) and the firmware linked for
slot A. `--blank` adds erased pages, which is how a USB reflash resets the OTA
boot state so the picker boots the slot A it was just given.

**The pieces go out as one contiguous run**, the gaps between them filled
with erased bytes. A UF2 whose blocks skipped from the picker straight to
slot A was accepted by a real Pico's boot ROM, which then rebooted — and slot
A did not hold what the file carried: the picker found no valid vector table,
and forcing a jump into the slot never brought the firmware up. The same two
programs with the gaps filled boot first time. (The emulator, which writes
blocks where they say, ran the gapped image happily; only the board showed
it.) The cost is a larger file — the run reaches the end of the last piece.


    python elf2uf2.py boot.elf firmware.elf --blank 0xFE000:0x2000 rustnet-pico.uf2
"""
import struct
import sys

UF2_MAGIC_START0 = 0x0A324655
UF2_MAGIC_START1 = 0x9E5D5157
UF2_MAGIC_END = 0x0AB16F30
UF2_FLAG_FAMILY_ID = 0x00002000
RP2040_FAMILY_ID = 0xE48BFF56

PAYLOAD = 256
BLOCK = 512
FLASH_START = 0x10000000
FLASH_END = 0x11000000

PT_LOAD = 1
PAD = bytes([0x00])
# What erased flash reads as. A gap between segments filled with this looks
# like untouched flash rather than a run of zeros that might be executed.
ERASED = bytes([0xFF])


def load_segments(path):
    """PT_LOAD segments as (physical address, bytes), flash only."""
    with open(path, "rb") as handle:
        elf = handle.read()

    if elf[:4] != b"\x7fELF":
        raise SystemExit(f"{path} is not an ELF file")
    if elf[4] != 1:
        raise SystemExit("expected a 32-bit ELF (thumbv6m)")

    e_phoff = struct.unpack_from("<I", elf, 0x1C)[0]
    e_phentsize = struct.unpack_from("<H", elf, 0x2A)[0]
    e_phnum = struct.unpack_from("<H", elf, 0x2C)[0]

    segments = []
    for i in range(e_phnum):
        off = e_phoff + i * e_phentsize
        p_type, p_offset, _p_vaddr, p_paddr, p_filesz = struct.unpack_from(
            "<IIIII", elf, off)
        if p_type != PT_LOAD or p_filesz == 0:
            continue
        if not FLASH_START <= p_paddr < FLASH_END:
            continue
        segments.append((p_paddr, elf[p_offset:p_offset + p_filesz]))
    return sorted(segments)


def flatten(segments):
    """One contiguous image, and the address it starts at."""
    base = min(addr for addr, _ in segments)
    end = max(addr + len(data) for addr, data in segments)
    image = bytearray(ERASED * (end - base))
    for addr, data in segments:
        start = addr - base
        image[start:start + len(data)] = data
    return base, bytes(image)


def pad_to_pages(base, image):
    """Pages are the unit the bootloader writes, so pieces must be whole pages."""
    if base % PAYLOAD:
        raise SystemExit(f"a piece starts at {base:#010x}, which is not page-aligned")
    if len(image) % PAYLOAD:
        image = image + ERASED * (PAYLOAD - len(image) % PAYLOAD)
    return base, image


def to_uf2(pieces):
    # Padded to a whole number of pages, and every block declares a full 256
    # bytes. The RP2040's bootloader writes flash a page at a time and every
    # known-good producer (picotool, elf2uf2-rs) does the same; a short final
    # block is accepted into the drive and then quietly not booted, which is
    # indistinguishable from a bad image.
    pieces = sorted(pad_to_pages(b, i) for b, i in pieces)
    for (a, ai), (b, _) in zip(pieces, pieces[1:]):
        if b < a + len(ai):
            raise SystemExit(f"pieces overlap at {b:#010x}")
    # One run from the first piece to the end of the last, gaps erased (see
    # the module docs for why a gapped UF2 is not safe on this ROM).
    start = pieces[0][0]
    end = max(b + len(i) for b, i in pieces)
    run = bytearray(ERASED * (end - start))
    for base, image in pieces:
        run[base - start:base - start + len(image)] = image
    blocks = [(start + off, bytes(run[off:off + PAYLOAD])) for off in range(0, len(run), PAYLOAD)]
    out = bytearray()
    for index, (addr, chunk) in enumerate(blocks):
        out += struct.pack(
            "<IIIIIIII",
            UF2_MAGIC_START0,
            UF2_MAGIC_START1,
            UF2_FLAG_FAMILY_ID,
            addr,
            PAYLOAD,
            index,
            len(blocks),
            RP2040_FAMILY_ID,
        )
        # The payload area is a fixed 476 bytes whatever the block carries.
        out += chunk.ljust(476, PAD)
        out += struct.pack("<I", UF2_MAGIC_END)
    return bytes(out)


def main():
    args = sys.argv[1:]
    blanks = []
    while "--blank" in args:
        i = args.index("--blank")
        offset, length = (int(v, 0) for v in args[i + 1].split(":"))
        blanks.append((FLASH_START + offset, ERASED * length))
        del args[i:i + 2]
    if len(args) < 2:
        raise SystemExit(__doc__)
    *elves, target = args

    pieces = []
    for elf in elves:
        segments = load_segments(elf)
        if not segments:
            raise SystemExit(f"{elf}: no loadable flash segments — is this a linked image?")
        pieces.append(flatten(segments))
    if min(base for base, _ in pieces) != FLASH_START:
        raise SystemExit(
            f"nothing starts at {FLASH_START:#010x}: the second stage must sit at "
            "flash offset 0 or the ROM will not run it — pass the slot picker "
            "(boot/) as well as the firmware"
        )

    uf2 = to_uf2(pieces + blanks)
    with open(target, "wb") as handle:
        handle.write(uf2)
    print(f"{len(elves)} image(s), {len(blanks)} erased range(s) in "
          f"{len(uf2) // BLOCK} blocks -> {target}")


if __name__ == "__main__":
    main()
