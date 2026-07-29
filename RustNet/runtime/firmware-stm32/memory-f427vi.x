/* STM32F427VIT6 — Netduino 3 WiFi.

   RAM covers the contiguous SRAM1+SRAM2+SRAM3 run at 0x20000000
   (112K + 16K + 64K = 192K). The part's remaining 64K is CCM at 0x10000000:
   core-accessible but unreachable by DMA, so it is left out deliberately
   rather than silently handed to the allocator. 192K + 64K = the 256K the
   datasheet advertises.

   FLASH stops at 384K of the part's 2 MB, matching the F401RE so both
   boards keep storage in the same place: sector 7 (0x08060000, 128 KB),
   which the firmware erases at runtime and the linker must therefore never
   place code in. There is another 1.6 MB beyond it if this image ever
   needs the room. */
MEMORY
{
  FLASH : ORIGIN = 0x08000000, LENGTH = 384K
  RAM   : ORIGIN = 0x20000000, LENGTH = 192K
}
