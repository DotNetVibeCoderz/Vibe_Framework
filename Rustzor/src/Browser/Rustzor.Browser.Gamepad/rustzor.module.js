// Rustzor.Browser.Gamepad — controllers, polled per frame.
export function install({ ops }) {
  ops["pad.poll"] = () =>
    Array.from(navigator.getGamepads ? navigator.getGamepads() : [])
      .filter(Boolean)
      .map((g) => ({
        index: g.index,
        id: g.id,
        connected: g.connected,
        axes: Array.from(g.axes),
        buttons: g.buttons.map((b) => [b.pressed, b.value]),
      }));
  ops["pad.rumble"] = async ({ index, duration, strong, weak }) => {
    const g = navigator.getGamepads()[index];
    if (!g?.vibrationActuator) return false;
    await g.vibrationActuator.playEffect("dual-rumble", { duration, strongMagnitude: strong, weakMagnitude: weak });
    return true;
  };
}
