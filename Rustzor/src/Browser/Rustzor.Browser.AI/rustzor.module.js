// Rustzor.Browser.AI — Chrome built-in AI (Prompt, Summarizer, Translator, LanguageDetector).
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle }) {
  const api = (name) => globalThis[name] ?? globalThis.ai?.[name.charAt(0).toLowerCase() + name.slice(1)];
  ops["bai.availability"] = async ({ api: name }) => {
    const a = api(name);
    if (!a) return "unavailable";
    if (a.availability) return a.availability();
    const caps = await a.capabilities?.();
    return caps?.available === "readily" ? "available" : caps?.available === "after-download" ? "downloadable" : "unavailable";
  };
  ops["bai.session"] = async ({ system, temperature, topK }) => {
    const LM = api("LanguageModel");
    if (!LM) throw new Error("the built-in language model is not available in this browser");
    const options = { temperature, topK };
    if (system) options.initialPrompts = [{ role: "system", content: system }];
    return addHandle(await LM.create(options));
  };
  ops["bai.prompt"] = ({ h, text }) => getHandle(h).prompt(text);
  ops["bai.stream"] = ({ h, text, id }) => {
    (async () => {
      try {
        let all = "";
        for await (const chunk of getHandle(h).promptStreaming(text)) {
          // Newer browsers stream deltas, older ones the text so far.
          all = chunk.startsWith(all) ? chunk : all + chunk;
          dispatch(id, { text: all });
        }
        dispatch(id, { done: true });
      } catch (e) {
        dispatch(id, { done: true, error: String(e?.message ?? e) });
      }
    })();
  };
  ops["bai.destroy"] = ({ h }) => {
    getHandle(h)?.destroy?.();
    releaseHandle(h);
  };
  ops["bai.summarize"] = async ({ text, type, length }) => {
    const S = api("Summarizer");
    if (!S) throw new Error("the built-in summarizer is not available in this browser");
    const s = await S.create({ type, length, format: "plain-text" });
    try {
      return await s.summarize(text);
    } finally {
      s.destroy?.();
    }
  };
  ops["bai.translate"] = async ({ text, from, to }) => {
    const T = api("Translator");
    if (!T) throw new Error("the built-in translator is not available in this browser");
    const t = await T.create({ sourceLanguage: from, targetLanguage: to });
    return t.translate(text);
  };
  ops["bai.detect"] = async ({ text }) => {
    const D = api("LanguageDetector");
    if (!D) throw new Error("the built-in language detector is not available in this browser");
    const [best] = await (await D.create()).detect(text);
    return { language: best?.detectedLanguage ?? "und", confidence: best?.confidence ?? 0 };
  };
}
