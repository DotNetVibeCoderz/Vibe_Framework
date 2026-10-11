// Rustzor.Browser.Sensors — orientation, motion, Generic Sensors, battery, wake lock.
export function install({ ops, dispatch, addHandle }) {
  ops["sensors.permission"] = async () => {
    const D = globalThis.DeviceOrientationEvent;
    if (D && typeof D.requestPermission === "function") return (await D.requestPermission()) === "granted";
    return true;
  };
  ops["sensors.orientation"] = ({ id }) => {
    const fn = (e) => dispatch(id, { alpha: e.alpha ?? 0, beta: e.beta ?? 0, gamma: e.gamma ?? 0, absolute: !!e.absolute });
    addEventListener("deviceorientation", fn);
    return addHandle(() => removeEventListener("deviceorientation", fn));
  };
  ops["sensors.motion"] = ({ id }) => {
    const fn = (e) => {
      const a = e.acceleration || {};
      const g = e.accelerationIncludingGravity || {};
      dispatch(id, { x: a.x ?? 0, y: a.y ?? 0, z: a.z ?? 0, gx: g.x ?? 0, gy: g.y ?? 0, gz: g.z ?? 0, interval: e.interval ?? 0 });
    };
    addEventListener("devicemotion", fn);
    return addHandle(() => removeEventListener("devicemotion", fn));
  };
  ops["sensors.generic"] = ({ id, sensor, frequency }) => {
    const Ctor = globalThis[sensor];
    if (!Ctor) {
      dispatch(id, { error: `${sensor} is not supported here` });
      return addHandle(() => {});
    }
    const s = new Ctor({ frequency });
    s.onreading = () => dispatch(id, { x: s.x ?? 0, y: s.y ?? 0, z: s.z ?? 0 });
    s.onerror = (e) => dispatch(id, { error: e.error?.message ?? "sensor error" });
    s.start();
    return addHandle(() => s.stop());
  };
  ops["sensors.light"] = ({ id }) => {
    if (!globalThis.AmbientLightSensor) {
      dispatch(id, { error: "AmbientLightSensor is not supported here" });
      return addHandle(() => {});
    }
    const s = new AmbientLightSensor();
    s.onreading = () => dispatch(id, { lux: s.illuminance });
    s.onerror = (e) => dispatch(id, { error: e.error?.message ?? "sensor error" });
    s.start();
    return addHandle(() => s.stop());
  };
  ops["sensors.battery"] = async () => {
    if (!navigator.getBattery) return null;
    const b = await navigator.getBattery();
    const finite = (v) => (v === Infinity ? -1 : v);
    return { level: b.level, charging: b.charging, chargingTime: finite(b.chargingTime), dischargingTime: finite(b.dischargingTime) };
  };
  ops["sensors.wakeLock"] = async () => {
    const lock = await navigator.wakeLock.request("screen");
    return addHandle(() => lock.release());
  };
}
