// Rustzor.AI — local inference with transformers.js, loaded on first use.
// Models download from the Hugging Face hub once and are cached by the browser.
export function install({ ops }) {
  const SRC = globalThis.RustzorCdn?.transformers ?? "https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.0.2";
  let lib = null;
  const load = () => (lib ??= import(/* @vite-ignore */ SRC));
  const pipelines = new Map();
  const pipe = async (task, model) => {
    const key = task + "|" + model;
    if (!pipelines.has(key)) pipelines.set(key, load().then((t) => t.pipeline(task, model)));
    return pipelines.get(key);
  };

  ops["ai.local.embed"] = async ({ model, texts }) => {
    const extract = await pipe("feature-extraction", model);
    const out = await extract(texts, { pooling: "mean", normalize: true });
    return out.tolist();
  };
  ops["ai.local.classify"] = async ({ model, text }) => {
    const classify = await pipe("text-classification", model);
    return await classify(text, { top_k: 5 });
  };
  ops["ai.local.generate"] = async ({ model, prompt, max }) => {
    const generate = await pipe("text-generation", model);
    const out = await generate([{ role: "user", content: prompt }], { max_new_tokens: max, do_sample: false });
    const last = out[0].generated_text;
    return Array.isArray(last) ? last[last.length - 1].content : String(last);
  };
}
