// Rustzor.Browser.WebXR — session lifecycle and viewer pose per frame.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle }) {
  ops["xr.supported"] = async ({ mode }) => (navigator.xr ? navigator.xr.isSessionSupported(mode) : false);
  ops["xr.start"] = async ({ mode, id, features }) => {
    if (!navigator.xr) throw new Error("WebXR is not available");
    const session = await navigator.xr.requestSession(mode, { optionalFeatures: features });
    const space = await session.requestReferenceSpace(mode === "inline" ? "viewer" : "local");
    const frame = (t, xrFrame) => {
      const pose = xrFrame.getViewerPose(space);
      if (pose) {
        const p = pose.transform.position;
        const o = pose.transform.orientation;
        dispatch(id, { x: p.x, y: p.y, z: p.z, qx: o.x, qy: o.y, qz: o.z, qw: o.w, t });
      }
      session.requestAnimationFrame(frame);
    };
    session.requestAnimationFrame(frame);
    return addHandle(session);
  };
  ops["xr.end"] = async ({ h }) => {
    const s = getHandle(h);
    releaseHandle(h);
    if (s) await s.end();
    return true;
  };
}
