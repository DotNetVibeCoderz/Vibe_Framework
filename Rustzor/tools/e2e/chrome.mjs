// Launches headless Chrome for the e2e tools. RZ_FAKE_MEDIA=1 adds a fake
// camera and microphone (a test pattern and a beep), accepts the permission
// prompts and screen sharing, and lets audio play without a click.
import { chromium } from "playwright-core";

export const fakeMediaArgs = [
  "--use-fake-ui-for-media-stream",
  "--use-fake-device-for-media-stream",
  "--auto-accept-this-tab-capture",
  "--auto-select-desktop-capture-source=Entire screen",
  "--autoplay-policy=no-user-gesture-required",
];

export function launch() {
  return chromium.launch({
    executablePath: process.env.CHROME_PATH ?? "C:/Program Files/Google/Chrome/Application/chrome.exe",
    headless: true,
    args: process.env.RZ_FAKE_MEDIA ? fakeMediaArgs : [],
  });
}
