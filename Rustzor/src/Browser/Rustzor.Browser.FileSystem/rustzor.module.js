// Rustzor.Browser.FileSystem — pickers, saving, drag and drop, OPFS.
export function install({ ops, dispatch, addHandle, getHandle, elementOf }) {
  const describe = (file, handle) => ({
    name: file.name,
    size: file.size,
    type: file.type,
    modified: file.lastModified,
    h: addHandle({ file, handle }),
  });
  const read = (h, how) =>
    new Promise((resolve, reject) => {
      const r = new FileReader();
      r.onload = () => resolve(r.result);
      r.onerror = () => reject(r.error);
      r[how](getHandle(h).file);
    });
  ops["fs.native"] = () => "showOpenFilePicker" in globalThis;
  ops["fs.pick"] = async ({ accept, multiple }) => {
    if (globalThis.showOpenFilePicker && !accept.includes("/")) {
      const types = accept
        ? [{ description: "Files", accept: { "application/octet-stream": accept.split(",").map((s) => s.trim()) } }]
        : undefined;
      try {
        const handles = await showOpenFilePicker({ multiple, types });
        return Promise.all(handles.map(async (h) => describe(await h.getFile(), h)));
      } catch (e) {
        if (e.name === "AbortError") return [];
        throw e;
      }
    }
    return new Promise((resolve) => {
      const input = document.createElement("input");
      input.type = "file";
      input.accept = accept;
      input.multiple = multiple;
      input.onchange = () => resolve(Array.from(input.files).map((f) => describe(f, null)));
      input.click();
    });
  };
  ops["fs.readText"] = ({ h }) => read(h, "readAsText");
  ops["fs.readUrl"] = ({ h }) => read(h, "readAsDataURL");
  ops["fs.readBytes"] = async ({ h }) => {
    const url = await read(h, "readAsDataURL");
    return url.slice(url.indexOf(",") + 1);
  };
  ops["fs.writeBack"] = async ({ h, text }) => {
    const box = getHandle(h);
    if (!box.handle) throw new Error("this file was not opened with a writable handle");
    const w = await box.handle.createWritable();
    await w.write(text);
    await w.close();
    return true;
  };
  ops["fs.save"] = async ({ name, text, type }) => {
    if (globalThis.showSaveFilePicker) {
      try {
        const handle = await showSaveFilePicker({ suggestedName: name });
        const w = await handle.createWritable();
        await w.write(new Blob([text], { type }));
        await w.close();
        return true;
      } catch (e) {
        if (e.name === "AbortError") return false;
      }
    }
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([text], { type }));
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 10000);
    return true;
  };
  ops["fs.drop"] = ({ el, id }) => {
    const target = elementOf(el);
    const over = (e) => {
      e.preventDefault();
      target.classList.add("rz-dragover");
    };
    const leave = () => target.classList.remove("rz-dragover");
    const drop = (e) => {
      e.preventDefault();
      leave();
      dispatch(id, Array.from(e.dataTransfer.files).map((f) => describe(f, null)));
    };
    target.addEventListener("dragover", over);
    target.addEventListener("dragleave", leave);
    target.addEventListener("drop", drop);
    return addHandle(() => {
      target.removeEventListener("dragover", over);
      target.removeEventListener("dragleave", leave);
      target.removeEventListener("drop", drop);
    });
  };
  const walk = async (path, create) => {
    let dir = await navigator.storage.getDirectory();
    const parts = path.split("/").filter(Boolean);
    const file = parts.pop();
    for (const p of parts) dir = await dir.getDirectoryHandle(p, { create });
    return { dir, file };
  };
  ops["fs.opfsWrite"] = async ({ path, text }) => {
    const { dir, file } = await walk(path, true);
    const w = await (await dir.getFileHandle(file, { create: true })).createWritable();
    await w.write(text);
    await w.close();
    return true;
  };
  ops["fs.opfsRead"] = async ({ path }) => {
    try {
      const { dir, file } = await walk(path, false);
      return await (await (await dir.getFileHandle(file)).getFile()).text();
    } catch {
      return null;
    }
  };
  ops["fs.opfsDelete"] = async ({ path }) => {
    const { dir, file } = await walk(path, false);
    await dir.removeEntry(file);
    return true;
  };
  ops["fs.opfsList"] = async ({ path }) => {
    let dir = await navigator.storage.getDirectory();
    for (const p of path.split("/").filter(Boolean)) dir = await dir.getDirectoryHandle(p);
    const names = [];
    for await (const [name] of dir.entries()) names.push(name);
    return names;
  };
}
