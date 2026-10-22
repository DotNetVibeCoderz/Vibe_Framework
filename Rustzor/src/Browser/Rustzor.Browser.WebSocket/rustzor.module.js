// Rustzor.Browser.WebSocket — WebSocket with reconnect, and BroadcastChannel.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle }) {
  ops["ws.open"] = ({ url, id, reconnect }) => {
    const box = { socket: null, closedByUser: false, delay: 500 };
    const connect = () => {
      const s = new WebSocket(url);
      box.socket = s;
      s.onopen = () => {
        box.delay = 500;
        dispatch(id, { type: "open" });
      };
      s.onmessage = (e) => dispatch(id, { type: "message", data: typeof e.data === "string" ? e.data : "" });
      s.onerror = () => dispatch(id, { type: "error", message: "WebSocket error" });
      s.onclose = (e) => {
        dispatch(id, { type: "close", code: e.code, reason: e.reason });
        if (reconnect && !box.closedByUser) {
          setTimeout(connect, box.delay);
          box.delay = Math.min(box.delay * 2, 15000);
        }
      };
    };
    connect();
    return addHandle(box);
  };
  ops["ws.send"] = ({ h, data }) => getHandle(h)?.socket?.send(data);
  ops["ws.close"] = ({ h, code, reason }) => {
    const box = getHandle(h);
    if (!box) return;
    box.closedByUser = true;
    box.socket.close(code, reason);
    releaseHandle(h);
  };
  ops["bc.open"] = ({ name, id }) => {
    const ch = new BroadcastChannel(name);
    // text stays text; an object (posted with PostRaw) goes to C# as JSON, parsed there once
    ch.onmessage = (e) => dispatch(id, e.data);
    return addHandle(ch);
  };
  ops["bc.post"] = ({ h, data }) => getHandle(h)?.postMessage(data);
  ops["bc.close"] = ({ h }) => {
    getHandle(h)?.close();
    releaseHandle(h);
  };
}
