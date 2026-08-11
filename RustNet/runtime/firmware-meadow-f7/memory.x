/* STM32F777 — Wilderness Labs Meadow F7 Micro v1.0.

   RAM starts at SRAM1 rather than at 0x20000000, deliberately. The 128 KB
   below it is DTCM: the fastest memory on the part, but tightly coupled to
   the core and **not reachable by DMA**. Handing it to the allocator would
   work until the first driver DMAs into a buffer that happened to land
   there, and then fail in a way that depends on allocation order. The
   Netduino's memory map leaves its CCM out for exactly this reason.

   So RAM is SRAM1 (368K) + SRAM2 (16K) = 384K contiguous at 0x20020000,
   out of the part's 512K. That is three times what the interpreter's heap
   asks for.

   FLASH stops at 1 MB of the part's 2 MB. The image is nowhere near that;
   the ceiling exists so the upper megabyte stays free for a flash
   filesystem, and so the linker can never place code where an erase would
   land. The F777's sectors up there are 256 KB each, which is the erase
   granularity that region would have. */
MEMORY
{
  FLASH : ORIGIN = 0x08000000, LENGTH = 1M
  RAM   : ORIGIN = 0x20020000, LENGTH = 384K
}
