// Rustzor.Browser.Fetch — HTTP through the browser's fetch, buffered or streamed.
export function install({ ops, dispatch }) {
  ops["fetch.send"] = async ({ url, method, headers, body, timeout, credentials }) => {
    const controller = new AbortController();
    const timer = timeout > 0 ? setTimeout(() => controller.abort(), timeout) : null;
    try {
      const res = await fetch(url, {
        method,
        headers,
        body: body ?? undefined,
        credentials,
        signal: controller.signal,
      });
      const out = {};
      res.headers.forEach((v, k) => (out[k] = v));
      return { status: res.status, statusText: res.statusText, url: res.url, headers: out, text: await res.text() };
    } finally {
      if (timer) clearTimeout(timer);
    }
  };
  // Streams in flight, by callback id, so fetch.abort can stop one.
  const streams = new Map();
  ops["fetch.abort"] = ({ id }) => {
    streams.get(id)?.abort();
    streams.delete(id);
  };
  ops["fetch.stream"] = ({ id, url, method, headers, body }) => {
    const controller = new AbortController();
    streams.set(id, controller);
    (async () => {
      try {
        const res = await fetch(url, { method, headers, body: body ?? undefined, signal: controller.signal });
        if (!res.ok) throw new Error(`HTTP ${res.status} ${res.statusText}: ${(await res.text()).slice(0, 2000)}`);
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          dispatch(id, { kind: "chunk", text: decoder.decode(value, { stream: true }) });
        }
        dispatch(id, { kind: "done" });
      } catch (e) {
        if (!controller.signal.aborted) dispatch(id, { kind: "error", error: String(e?.message ?? e) });
      } finally {
        streams.delete(id);
      }
    })();
  };
}
