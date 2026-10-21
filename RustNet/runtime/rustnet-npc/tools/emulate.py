"""Run an RNPC module's procedures on an emulated Cortex-M before any board.

The module is placed by the real Rust loader (`examples/npc_place.rs`) at a
chosen SRAM address — once at one address and once at another, so every
relocation is exercised — and its procedures are called with the NPC ABI on
the Unicorn emulator (pip install unicorn). Checks the same results the
LibApp end-to-end test checks on the host:

    python tools/emulate.py path/to/module.rnpc [v6m|v7em]
"""
import os
import struct
import subprocess
import sys
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_MODE_MCLASS
from unicorn.arm_const import UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_SP, UC_ARM_REG_LR

module, arch = os.path.abspath(sys.argv[1]), (sys.argv[2] if len(sys.argv) > 2 else "v6m")
RAM, RAM_SIZE = 0x2000_0000, 0x4_0000
ARGS, OUT, STACK_TOP, RETURN = 0x2003_0000, 0x2003_1000, 0x2003_F000, 0x2003_F800


REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))


def place(base):
    out = os.path.abspath(f"npc_{base:08x}.bin")
    listing = subprocess.run(
        ["cargo", "run", "-q", "-p", "rustnet-npc", "--example", "npc_place", "--", module, f"{base:x}", out, arch],
        check=True, capture_output=True, text=True, cwd=REPO).stdout
    exports = {l.split()[0]: int(l.split()[1]) for l in listing.splitlines() if l.strip()}
    return open(out, "rb").read(), exports


def call(code, exports, base, name, args):
    uc = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
    uc.mem_map(RAM, RAM_SIZE)
    uc.mem_write(base, code)
    uc.mem_write(ARGS, args)
    uc.mem_write(RETURN, b"\x00\xbf\x00\xbf")  # nop; nop — where the call returns
    uc.reg_write(UC_ARM_REG_R0, ARGS)
    uc.reg_write(UC_ARM_REG_R1, len(args))
    uc.reg_write(UC_ARM_REG_R2, OUT)
    uc.reg_write(UC_ARM_REG_R3, 64)
    uc.reg_write(UC_ARM_REG_SP, STACK_TOP)
    uc.reg_write(UC_ARM_REG_LR, RETURN | 1)
    uc.emu_start(base + exports[name], RETURN, count=1_000_000)
    n = struct.unpack("<i", struct.pack("<I", uc.reg_read(UC_ARM_REG_R0)))[0]
    return n, bytes(uc.mem_read(OUT, max(n, 0)))


ok = True
for base in (0x2000_1000, 0x2001_2340):
    code, exports = place(base)
    n, out = call(code, exports, base, "rustnet_sum_u32", struct.pack("<3I", 40, 2, 100000))
    ok &= n == 4 and struct.unpack("<I", out)[0] == 100042
    n, out = call(code, exports, base, "rustnet_crc32", b"123456789")
    crc = struct.unpack("<I", out)[0] if n == 4 else None
    ok &= crc == 0xCBF43926
    n, out = call(code, exports, base, "rustnet_reverse", b"RustNet")
    ok &= out == b"teNtsuR"
    n, _ = call(code, exports, base, "rustnet_fail", b"")
    ok &= n == -7
    print(f"base 0x{base:08x}: sum, crc32=0x{crc or 0:08X}, reverse, fail -> {'ok' if ok else 'FAILED'}")
sys.exit(0 if ok else 1)
