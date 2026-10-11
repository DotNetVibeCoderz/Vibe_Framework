// Rustzor.Game — input state the C# game loop polls once per frame.
// One call returns everything that changed, so input costs one bridge
// crossing per frame however many keys are held.
export function install({ ops, elementOf }) {
  const down = new Set();
  let pressed = new Set();
  let released = new Set();
  const pointer = { x: 0, y: 0, buttons: 0, clicks: 0, wheel: 0, inside: false, target: null };

  const gameKey = (e) => {
    // Keys a game owns should not scroll the page.
    if (["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Space"].includes(e.code) && pointer.target) e.preventDefault();
  };
  addEventListener("keydown", (e) => {
    if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return;
    gameKey(e);
    if (!down.has(e.code)) pressed.add(e.code);
    down.add(e.code);
  });
  addEventListener("keyup", (e) => {
    down.delete(e.code);
    released.add(e.code);
  });
  addEventListener("blur", () => down.clear());

  const bind = (el) => {
    if (pointer.target === el || !el) return;
    pointer.target = el;
    const at = (e) => {
      const r = el.getBoundingClientRect();
      pointer.x = e.clientX - r.left;
      pointer.y = e.clientY - r.top;
    };
    el.addEventListener("pointermove", (e) => {
      at(e);
      pointer.buttons = e.buttons;
    });
    el.addEventListener("pointerdown", (e) => {
      at(e);
      pointer.buttons = e.buttons;
      pointer.clicks++;
      el.setPointerCapture?.(e.pointerId);
    });
    el.addEventListener("pointerup", (e) => {
      at(e);
      pointer.buttons = e.buttons;
    });
    el.addEventListener("pointerenter", () => (pointer.inside = true));
    el.addEventListener("pointerleave", () => (pointer.inside = false));
    el.addEventListener("wheel", (e) => {
      pointer.wheel += e.deltaY;
      e.preventDefault();
    }, { passive: false });
    el.style.touchAction = "none";
  };

  ops["game.input"] = ({ el }) => {
    if (el) bind(elementOf(el));
    const out = {
      down: [...down],
      pressed: [...pressed],
      released: [...released],
      x: pointer.x,
      y: pointer.y,
      buttons: pointer.buttons,
      clicks: pointer.clicks,
      wheel: pointer.wheel,
      inside: pointer.inside,
    };
    pressed = new Set();
    released = new Set();
    pointer.clicks = 0;
    pointer.wheel = 0;
    return out;
  };
}
