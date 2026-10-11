// The browser half of this library. Rustzor installs it at startup in every app
// that references the library; each op is a name the C# side calls with Js.Call,
// Js.Send or Js.CallAsync.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle }) {
  ops["battery.supported"] = () => typeof navigator.getBattery === "function";

  ops["battery.level"] = async () => {
    if (typeof navigator.getBattery !== "function") return -1;
    return (await navigator.getBattery()).level;
  };

  ops["battery.watch"] = ({ id }) => {
    const box = { battery: null, onChange: null };
    navigator.getBattery?.().then((battery) => {
      box.battery = battery;
      box.onChange = () => dispatch(id, { level: battery.level });
      battery.addEventListener("levelchange", box.onChange);
    });
    return addHandle(box);
  };

  ops["battery.unwatch"] = ({ h }) => {
    const box = getHandle(h);
    box?.battery?.removeEventListener("levelchange", box.onChange);
    releaseHandle(h);
  };
}
