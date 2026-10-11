// Rustzor.Browser.Video — media element control, frames, picture-in-picture.
export function install({ ops, elementOf, addHandle, dispatch }) {
  ops["video.play"] = ({ el }) => elementOf(el).play().then(() => true);
  ops["video.do"] = ({ el, action }) => elementOf(el)[action]();
  ops["video.set"] = ({ el, prop, value }) => {
    elementOf(el)[prop] = value;
  };
  ops["video.state"] = ({ el }) => {
    const v = elementOf(el);
    return { time: v.currentTime, duration: isFinite(v.duration) ? v.duration : 0, paused: v.paused, volume: v.volume };
  };
  ops["video.pip"] = ({ el }) => elementOf(el).requestPictureInPicture().then(() => true);
  ops["video.frame"] = ({ el, type }) => {
    const v = elementOf(el);
    const c = document.createElement("canvas");
    c.width = v.videoWidth;
    c.height = v.videoHeight;
    c.getContext("2d").drawImage(v, 0, 0);
    return c.toDataURL(type);
  };
  ops["video.onTime"] = ({ el, id }) => {
    const v = elementOf(el);
    const fn = () => dispatch(id, { time: v.currentTime, duration: isFinite(v.duration) ? v.duration : 0 });
    v.addEventListener("timeupdate", fn);
    return addHandle(() => v.removeEventListener("timeupdate", fn));
  };
}
