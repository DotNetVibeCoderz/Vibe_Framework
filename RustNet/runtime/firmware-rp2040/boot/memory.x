/* The slot picker: the ROM runs the 256-byte second stage at flash offset 0,
   which sets up execute-in-place and runs what follows it — this, which then
   jumps into firmware slot A (0x10004000) or B (0x10080000). It must stay
   inside the 16 KB in front of slot A; see rustnet-hal-rp2040/src/ota.rs. */
MEMORY {
    BOOT2 : ORIGIN = 0x10000000, LENGTH = 0x100
    FLASH : ORIGIN = 0x10000100, LENGTH = 0x4000 - 0x100
    RAM   : ORIGIN = 0x20000000, LENGTH = 256K
}

SECTIONS {
    .boot2 ORIGIN(BOOT2) : {
        KEEP(*(.boot2));
    } > BOOT2
} INSERT BEFORE .text;
