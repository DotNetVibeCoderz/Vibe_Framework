// Rustzor.Browser.NFC — Web NFC.
export function install({ ops, dispatch, addHandle }) {
  ops["nfc.supported"] = () => "NDEFReader" in globalThis;
  ops["nfc.scan"] = async ({ id }) => {
    const reader = new NDEFReader();
    const controller = new AbortController();
    await reader.scan({ signal: controller.signal });
    reader.onreading = (e) => {
      const records = e.message.records.map((r) => ({
        type: r.recordType,
        mediaType: r.mediaType ?? "",
        data: r.data ? new TextDecoder(r.encoding || "utf-8").decode(r.data) : "",
      }));
      dispatch(id, { serial: e.serialNumber, records });
    };
    reader.onreadingerror = () => dispatch(id, { error: "the tag could not be read" });
    return addHandle(() => controller.abort());
  };
  ops["nfc.write"] = async ({ text, url }) => {
    await new NDEFReader().write({ records: [{ recordType: url ? "url" : "text", data: text }] });
    return true;
  };
}
