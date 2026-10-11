// Rustzor.Browser.HID — WebHID.
export function install({ ops, dispatch, addHandle, getHandle }) {
  const b64 = (view) => btoa(String.fromCharCode(...new Uint8Array(view.buffer, view.byteOffset, view.byteLength)));
  const unb64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
  ops["hid.supported"] = () => "hid" in navigator;
  ops["hid.request"] = async ({ vendor, usagePage }) => {
    const filter = {};
    if (vendor) filter.vendorId = vendor;
    if (usagePage) filter.usagePage = usagePage;
    const [d] = await navigator.hid.requestDevice({ filters: Object.keys(filter).length ? [filter] : [] });
    return d ? { h: addHandle(d), name: d.productName, vendor: d.vendorId, product: d.productId } : null;
  };
  ops["hid.open"] = async ({ h }) => {
    const d = getHandle(h);
    if (!d.opened) await d.open();
    return true;
  };
  ops["hid.listen"] = ({ h, id }) => {
    getHandle(h).addEventListener("inputreport", (e) => dispatch(id, { report: e.reportId, data: b64(e.data) }));
  };
  ops["hid.send"] = async ({ h, report, data }) => {
    await getHandle(h).sendReport(report, unb64(data));
    return true;
  };
  ops["hid.close"] = async ({ h }) => {
    await getHandle(h)?.close();
    return true;
  };
}
