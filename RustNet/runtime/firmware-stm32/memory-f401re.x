/* STM32F401RET6 — Nucleo-F401RE.

   FLASH stops short of the part's 512 KB on purpose. Sector 7
   (0x08060000, 128 KB) is where the firmware keeps its provisioned key and
   any uploaded application, and the linker must never place code there:
   the firmware erases that sector at runtime, and would otherwise be
   erasing itself. Leaving it out of MEMORY is the enforcement — a build
   that outgrows the space fails to link instead of corrupting storage. */
MEMORY
{
  FLASH : ORIGIN = 0x08000000, LENGTH = 384K
  RAM   : ORIGIN = 0x20000000, LENGTH = 96K
}
