// Rustzor.Browser.Notifications — notifications, vibration, app badge.
export function install({ ops, dispatch }) {
  ops["notify.supported"] = () => "Notification" in globalThis;
  ops["notify.permission"] = () => ("Notification" in globalThis ? Notification.permission : "denied");
  ops["notify.request"] = () => Notification.requestPermission();
  ops["notify.show"] = ({ title, body, icon, tag, id }) => {
    const n = new Notification(title, { body: body ?? undefined, icon: icon ?? undefined, tag: tag ?? undefined });
    if (id) {
      n.onclick = () => {
        focus();
        dispatch(id, null);
      };
    }
  };
  ops["notify.vibrate"] = ({ pattern }) => (navigator.vibrate ? navigator.vibrate(pattern) : false);
  ops["notify.badge"] = async ({ count }) => {
    if (!navigator.setAppBadge) return false;
    if (count > 0) await navigator.setAppBadge(count);
    else await navigator.clearAppBadge();
    return true;
  };
}
