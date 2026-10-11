// Rustzor.Browser.Serial — Web Serial, line-oriented.
export function install({ ops, dispatch, addHandle, getHandle }) {
  ops["serial.supported"] = () => "serial" in navigator;
  ops["serial.request"] = async ({ vendor }) =>
    addHandle({ port: await navigator.serial.requestPort(vendor ? { filters: [{ usbVendorId: vendor }] } : {}), reading: false });
  ops["serial.open"] = async ({ h, baud }) => {
    await getHandle(h).port.open({ baudRate: baud });
    return true;
  };
  ops["serial.write"] = async ({ h, text }) => {
    const writer = getHandle(h).port.writable.getWriter();
    await writer.write(new TextEncoder().encode(text));
    writer.releaseLock();
    return true;
  };
  ops["serial.read"] = ({ h, id }) => {
    const box = getHandle(h);
    box.reading = true;
    (async () => {
      const decoder = new TextDecoder();
      let buffer = "";
      while (box.reading && box.port.readable) {
        box.reader = box.port.readable.getReader();
        try {
          for (;;) {
            const { value, done } = await box.reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            let at;
            while ((at = buffer.indexOf("\n")) >= 0) {
              dispatch(id, buffer.slice(0, at).replace(/\r$/, ""));
              buffer = buffer.slice(at + 1);
            }
          }
        } catch {
          break;
        } finally {
          box.reader.releaseLock();
        }
      }
    })();
  };
  ops["serial.close"] = async ({ h }) => {
    const box = getHandle(h);
    if (!box) return true;
    box.reading = false;
    await box.reader?.cancel().catch(() => {});
    await box.port.close().catch(() => {});
    return true;
  };
}
