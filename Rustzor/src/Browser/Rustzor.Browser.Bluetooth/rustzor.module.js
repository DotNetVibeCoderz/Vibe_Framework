// Rustzor.Browser.Bluetooth — Web Bluetooth (GATT).
export function install({ ops, dispatch, addHandle, getHandle }) {
  const b64 = (view) => {
    const bytes = new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
    return btoa(String.fromCharCode(...bytes));
  };
  const unb64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
  const characteristic = async (h, service, ch) => {
    const box = getHandle(h);
    if (!box.server?.connected) box.server = await box.device.gatt.connect();
    return (await box.server.getPrimaryService(service)).getCharacteristic(ch);
  };
  ops["ble.available"] = async () => (navigator.bluetooth ? navigator.bluetooth.getAvailability() : false);
  ops["ble.request"] = async ({ services, prefix, optional }) => {
    const filters = prefix ? [{ namePrefix: prefix }] : services.map((s) => ({ services: [s] }));
    const device = await navigator.bluetooth.requestDevice({ filters, optionalServices: [...services, ...optional] });
    return { h: addHandle({ device, server: null }), name: device.name ?? "", id: device.id };
  };
  ops["ble.connect"] = async ({ h }) => {
    const box = getHandle(h);
    box.server = await box.device.gatt.connect();
    return true;
  };
  ops["ble.disconnect"] = ({ h }) => getHandle(h)?.device.gatt.disconnect();
  ops["ble.read"] = async ({ h, service, ch }) => b64(await (await characteristic(h, service, ch)).readValue());
  ops["ble.write"] = async ({ h, service, ch, data }) => {
    await (await characteristic(h, service, ch)).writeValue(unb64(data));
    return true;
  };
  ops["ble.notify"] = async ({ h, service, ch, id }) => {
    const c = await characteristic(h, service, ch);
    c.addEventListener("characteristicvaluechanged", (e) => dispatch(id, b64(e.target.value)));
    await c.startNotifications();
    return true;
  };
}
