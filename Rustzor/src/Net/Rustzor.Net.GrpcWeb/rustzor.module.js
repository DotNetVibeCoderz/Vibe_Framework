// Rustzor.Net.GrpcWeb — gRPC-Web calls over fetch, with frames read as they stream in.
export function install({ ops, dispatch }) {
  const calls = new Map();
  const fromBase64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
  const toBase64 = (bytes) => {
    let s = "";
    for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(s);
  };
  // "grpc-status: 0\r\ngrpc-message: ok" into an object.
  const parseTrailers = (text) => {
    const out = {};
    for (const line of text.split("\r\n")) {
      const at = line.indexOf(":");
      if (at > 0) out[line.slice(0, at).trim().toLowerCase()] = line.slice(at + 1).trim();
    }
    return out;
  };

  ops["grpcweb.call"] = ({ id, url, body, headers }) => {
    const controller = new AbortController();
    calls.set(id, controller);
    (async () => {
      let trailers = {};
      let error = null;
      try {
        const res = await fetch(url, {
          method: "POST",
          headers: { "content-type": "application/grpc-web+proto", "x-grpc-web": "1", accept: "application/grpc-web+proto", ...headers },
          body: fromBase64(body),
          signal: controller.signal,
        });
        // A trailers-only response puts the status in the HTTP headers.
        res.headers.forEach((v, k) => { if (k.startsWith("grpc-")) trailers[k] = v; });
        if (!res.ok) {
          error = `HTTP ${res.status} ${res.statusText}`;
          if (!trailers["grpc-status"]) trailers["grpc-status"] = res.status === 404 ? "12" : "14";
        } else {
          const reader = res.body.getReader();
          let buffer = new Uint8Array(0);
          for (;;) {
            const { done, value } = await reader.read();
            if (value) {
              const joined = new Uint8Array(buffer.length + value.length);
              joined.set(buffer);
              joined.set(value, buffer.length);
              buffer = joined;
            }
            while (buffer.length >= 5) {
              const flag = buffer[0];
              const length = ((buffer[1] << 24) | (buffer[2] << 16) | (buffer[3] << 8) | buffer[4]) >>> 0;
              if (buffer.length < 5 + length) break;
              const frame = buffer.subarray(5, 5 + length);
              if (flag & 0x80) trailers = { ...trailers, ...parseTrailers(new TextDecoder().decode(frame)) };
              else dispatch(id, { kind: "message", data: toBase64(frame) });
              buffer = buffer.slice(5 + length);
            }
            if (done) break;
          }
        }
      } catch (e) {
        if (controller.signal.aborted) return;
        error = String(e?.message ?? e);
        trailers["grpc-status"] ??= "14";
        trailers["grpc-message"] ??= error;
      } finally {
        calls.delete(id);
      }
      dispatch(id, { kind: "end", trailers, error });
    })();
  };

  ops["grpcweb.cancel"] = ({ id }) => {
    calls.get(id)?.abort();
    calls.delete(id);
  };
}
