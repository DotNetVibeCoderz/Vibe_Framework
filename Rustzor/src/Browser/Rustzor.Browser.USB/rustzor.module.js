// Rustzor.Browser.USB — WebUSB.
export function install({ ops, addHandle, getHandle }) {
  const b64 = (view) => btoa(String.fromCharCode(...new Uint8Array(view.buffer, view.byteOffset, view.byteLength)));
  const unb64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
  ops["usb.supported"] = () => "usb" in navigator;
  ops["usb.request"] = async ({ vendor }) => {
    const d = await navigator.usb.requestDevice({ filters: vendor ? [{ vendorId: vendor }] : [] });
    return { h: addHandle(d), product: d.productName ?? "", maker: d.manufacturerName ?? "", vendor: d.vendorId, productId: d.productId };
  };
  ops["usb.open"] = async ({ h, iface }) => {
    const d = getHandle(h);
    await d.open();
    if (d.configuration === null) await d.selectConfiguration(1);
    await d.claimInterface(iface);
    return true;
  };
  ops["usb.out"] = async ({ h, ep, data }) => {
    await getHandle(h).transferOut(ep, unb64(data));
    return true;
  };
  ops["usb.in"] = async ({ h, ep, len }) => b64((await getHandle(h).transferIn(ep, len)).data);
  ops["usb.control"] = async ({ h, request, value, index, data }) => {
    await getHandle(h).controlTransferOut({ requestType: "vendor", recipient: "device", request, value, index }, data ? unb64(data) : undefined);
    return true;
  };
  ops["usb.close"] = async ({ h }) => {
    await getHandle(h)?.close();
    return true;
  };
}
