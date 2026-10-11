// Rustzor.Browser.Location — geolocation.
export function install({ ops, dispatch }) {
  const wire = (p) => ({
    lat: p.coords.latitude,
    lon: p.coords.longitude,
    acc: p.coords.accuracy,
    alt: p.coords.altitude,
    heading: p.coords.heading,
    speed: p.coords.speed,
    ts: p.timestamp,
  });
  ops["geo.supported"] = () => "geolocation" in navigator;
  ops["geo.current"] = ({ high, timeout }) =>
    new Promise((resolve, reject) =>
      navigator.geolocation.getCurrentPosition(
        (p) => resolve(wire(p)),
        (e) => reject(new Error(e.message)),
        { enableHighAccuracy: high, timeout },
      ),
    );
  ops["geo.watch"] = ({ id, high }) =>
    navigator.geolocation.watchPosition(
      (p) => dispatch(id, wire(p)),
      (e) => dispatch(id, { error: e.message }),
      { enableHighAccuracy: high },
    );
  ops["geo.clear"] = ({ watch }) => navigator.geolocation.clearWatch(watch);
}
