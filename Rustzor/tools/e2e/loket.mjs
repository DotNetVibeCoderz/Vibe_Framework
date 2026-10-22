// Drives the Loket sample (samples/Loket), a bilingual front-desk assistant, through its
// offline demo model: a reply streams in piece by piece, Stop cuts one short, a calculator
// call shows the right result on its tool card, a document dropped on the archive is found
// by a question and cited (the citation opens the passage), a note lands on the staff list,
// conversations survive a reload and can be deleted, and markup in a message is shown as
// text, never run.
//
// usage: node loket.mjs <base-url>   (the built site served, e.g. by tools/dev/serve.mjs)
//
// Live check, opt-in: with LOKET_AZURE_ENDPOINT, LOKET_AZURE_KEY and LOKET_AZURE_MODEL set,
// it also asks a real Azure OpenAI deployment for a streamed answer and a calculator call.
// The key goes from the environment into this browser's localStorage and nowhere else; it is
// never printed.
import { createServer } from "http";
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5360").replace(/\/$/, "");
const browser = await launch();
const context = await browser.newContext({ viewport: { width: 1280, height: 860 } });
const page = await context.newPage();
const errors = [];
page.on("console", (m) => m.type() === "error" && !m.text().includes("Failed to load resource") && errors.push(m.text()));
page.on("pageerror", (e) => errors.push(e.message));

let failed = 0;
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`);
  if (!ok) failed++;
};

const ready = async () => {
  await page.waitForSelector(".lk-board", { timeout: 30000 });
};
const idle = () => page.waitForSelector('[data-testid="send"]', { timeout: 30000 });
const lastAnswer = () => page.locator('[data-testid="desk-msg"]').last();
const ask = async (text) => {
  await page.fill("#lk-input", text);
  await page.click('[data-testid="send"]');
};

// Reads the newest answer's text every few milliseconds until the desk is idle again.
async function watchStream() {
  return await page.evaluate(async () => {
    const lengths = [];
    const t0 = performance.now();
    // First the desk has to start answering, or an idle check right after the click passes at once.
    while (!document.querySelector(".lk-msg.streaming") && performance.now() - t0 < 5000) await new Promise((r) => setTimeout(r, 5));
    while (performance.now() - t0 < 90000) {
      const answers = document.querySelectorAll('[data-testid="desk-msg"]');
      const text = answers[answers.length - 1]?.querySelector('[data-testid="answer"]')?.textContent ?? "";
      if (lengths.length === 0 || lengths[lengths.length - 1] !== text.length) lengths.push(text.length);
      if (document.querySelector('[data-testid="send"]') && !document.querySelector(".lk-msg.streaming")) break;
      await new Promise((r) => setTimeout(r, 12));
    }
    return lengths;
  });
}

await page.goto(base + "/");
await page.evaluate(() => localStorage.clear());
await page.goto(base + "/");
await ready();

// 1. Streaming, token by token.
await ask("Halo, apa saja yang bisa dibantu?");
const lengths = await watchStream();
const grew = lengths.every((n, i) => i === 0 || n >= lengths[i - 1]);
check("a reply streams in piece by piece", lengths.length >= 8 && grew && lengths[lengths.length - 1] > 200, `${lengths.length} distinct lengths, final ${lengths[lengths.length - 1]}`);
check("the conversation got a queue number", /^A-\d{3}$/.test(await page.textContent('[data-testid="ticket"]')));

// 2. Stop.
await ask("Tolong beri contoh format markdown");
await page.waitForFunction(() => (document.querySelectorAll('[data-testid="answer"]')[1]?.textContent.length ?? 0) > 20, null, { timeout: 15000 });
await page.click('[data-testid="stop"]');
await idle();
const stoppedText = await lastAnswer().locator('[data-testid="answer"]').textContent();
check("Stop ends the answer early and says so", (await lastAnswer().locator('[data-testid="stopped"]').count()) === 1 && stoppedText.length < 300, `${stoppedText.length} characters kept`);
await page.waitForTimeout(400);
check("nothing more arrives after Stop", (await lastAnswer().locator('[data-testid="answer"]').textContent()).length === stoppedText.length);

// The full Markdown answer: a table, a code block with a copy button.
await ask("contoh format markdown lagi");
await watchStream();
check("Markdown renders a table, a list and a code block with Copy",
  (await lastAnswer().locator("table").count()) === 1 && (await lastAnswer().locator("ol li").count()) === 3 && (await lastAnswer().locator(".lk-copy").count()) === 1);

// 3. A calculator call.
await ask("Berapa 15% dari 2.400.000?");
await watchStream();
const card = lastAnswer().locator('[data-testid="tool-card"][data-tool="calculator"]');
const summary = (await card.count()) ? await card.locator('[data-testid="tool-summary"]').textContent() : "";
check("the calculator runs and its card shows the result", summary.trim().endsWith("= 360000"), summary.trim());
check("the answer states the calculator's result", (await lastAnswer().locator('[data-testid="answer"]').textContent()).includes("360.000"));

// 4. A note for the staff.
await ask("Catat: telepon Pak RT soal surat domisili");
await watchStream();
check("create_note runs from a request", (await lastAnswer().locator('[data-tool="create_note"]').count()) === 1);

// 5. Markup in a message is text.
await ask('<img src=x onerror="window.__pwned=1"><script>window.__pwned=2</script> **tebal**');
await watchStream();
const escaped = await page.evaluate(() => ({
  pwned: window.__pwned,
  imgs: document.querySelectorAll('.lk-md img[src="x"], .lk-md script').length,
  shown: [...document.querySelectorAll('[data-testid="user-msg"] .lk-md')].pop()?.textContent ?? "",
  bold: [...document.querySelectorAll('[data-testid="user-msg"] .lk-md strong')].pop()?.textContent ?? "",
  echoed: [...document.querySelectorAll('[data-testid="answer"]')].pop()?.textContent ?? "",
}));
check("<script> and <img onerror> are shown as text and never run",
  escaped.pwned === undefined && escaped.imgs === 0 && escaped.shown.includes("<img src=x") && escaped.echoed.includes("<script>"), JSON.stringify({ pwned: escaped.pwned ?? null, imgs: escaped.imgs }));
check("Markdown still works next to escaped markup", escaped.bold === "tebal");

// 6. A document dropped on the archive answers a question, with a citation.
await page.click('a[href="dokumen"]');
await page.waitForSelector('[data-testid="drop"]');
const docText = "# SOP Parkir Sepeda\n\n## Penjaga parkir\nParkir sepeda di halaman samping kelurahan dijaga oleh Pak Darmawan setiap hari kerja.\n\n## Tarif\nParkir sepeda gratis dan tidak ada karcis.";
await page.evaluate((text) => {
  const zone = document.querySelector('[data-testid="drop"]');
  const dt = new DataTransfer();
  dt.items.add(new File([text], "parkir-sepeda.md", { type: "text/markdown" }));
  zone.dispatchEvent(new DragEvent("dragover", { dataTransfer: dt, bubbles: true, cancelable: true }));
  zone.dispatchEvent(new DragEvent("drop", { dataTransfer: dt, bubbles: true, cancelable: true }));
}, docText);
await page.waitForFunction(() => [...document.querySelectorAll(".lk-doc b")].some((b) => b.textContent === "SOP Parkir Sepeda"), null, { timeout: 10000 });
check("a dropped Markdown file is added and cut into passages", (await page.locator('[data-testid="passage"]').count()) === 2);

await page.click(".lk-ticket-link >> nth=0");
await idle();
await ask("Siapa yang menjaga parkir sepeda?");
await watchStream();
const search = lastAnswer().locator('[data-tool="search_documents"]');
check("the question searches the documents", (await search.count()) === 1);
const cites = lastAnswer().locator('[data-testid="answer"] .lk-cite');
check("the answer cites a source", (await cites.count()) >= 1);
if (await cites.count()) {
  await cites.first().click();
  const quoted = await page.textContent('[data-testid="source-text"]');
  const title = await page.textContent("#lk-src-title");
  check("the citation opens the retrieved passage of the dropped document", quoted.includes("Pak Darmawan") && title === "SOP Parkir Sepeda", title);
  await page.click('.lk-sheet-head .lk-icon-btn');
}
const tokensIn = Number((await page.textContent('[data-testid="tokens-in"]')).replace(/,/g, ""));
check("the board counts tokens", tokensIn > 0, `${tokensIn} in`);

// 7. Conversations persist across a reload; delete works and persists.
await page.click(".lk-take");
await ask("Terima kasih");
await watchStream();
const before = await page.locator(".lk-ticket").count();
const messagesBefore = await page.locator('[data-testid="user-msg"]').count();
await page.reload();
await ready();
check("conversations survive a reload", (await page.locator(".lk-ticket").count()) === before && (await page.locator('[data-testid="user-msg"]').count()) === messagesBefore, `${before} tickets`);
const doomed = await page.locator(".lk-ticket .lk-ticket-num").first().textContent();
await page.locator(".lk-ticket").first().hover();
await page.locator(".lk-ticket .lk-icon-btn.danger").first().click();
await page.click(".lk-dialog .lk-btn.danger");
await page.waitForTimeout(200);
await page.reload();
await ready();
const left = await page.locator(".lk-ticket .lk-ticket-num").allTextContents();
check("a deleted conversation stays deleted after a reload", left.length === before - 1 && !left.includes(doomed), `${doomed} deleted, ${left.length} left`);

await page.click('a[href="catatan"]');
check("the note is on the staff list", (await page.locator('[data-testid="note"]').filter({ hasText: "Telepon Pak RT soal surat domisili" }).count()) === 1);

// 8. The OpenAI-compatible provider over real HTTP, against a mock server here: a
//    streamed tool call whose JSON arguments arrive in pieces, then an answer one word
//    at a time. This is the path every real provider takes, minus the network.
const mock = createServer((req, res) => {
  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Headers", "*");
  if (req.method === "OPTIONS") return res.end();
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    res.writeHead(200, { "Content-Type": "text/event-stream" });
    const send = (o) => res.write(`data: ${JSON.stringify(o)}\n\n`);
    const answered = JSON.parse(body).messages.some((m) => m.role === "tool");
    const events = answered
      ? "Hasilnya 144, dihitung dengan kalkulator loket tanpa pembulatan sama sekali.".split(" ").map((w) => ({ choices: [{ index: 0, delta: { content: w + " " } }] }))
      : [
          { choices: [{ index: 0, delta: { role: "assistant", tool_calls: [{ index: 0, id: "call_mock1", type: "function", function: { name: "calculator", arguments: "" } }] } }] },
          { choices: [{ index: 0, delta: { tool_calls: [{ index: 0, function: { arguments: '{"expre' } }] } }] },
          { choices: [{ index: 0, delta: { tool_calls: [{ index: 0, function: { arguments: 'ssion":"12' } }] } }] },
          { choices: [{ index: 0, delta: { tool_calls: [{ index: 0, function: { arguments: '*12"}' } }] } }] },
        ];
    events.push({ choices: [], usage: { prompt_tokens: 321, completion_tokens: 45 } });
    let i = 0;
    const timer = setInterval(() => {
      if (i < events.length) return send(events[i++]);
      res.end("data: [DONE]\n\n");
      clearInterval(timer);
    }, 45);
  });
});
await new Promise((r) => mock.listen(0, r));
await page.evaluate((port) => {
  const s = JSON.parse(localStorage.getItem("loket.settings") || "{}");
  Object.assign(s, { Provider: "compatible", Endpoint: `http://localhost:${port}/v1`, ApiKey: "", Model: "mock-model", PriceIn: 1, PriceOut: 2 });
  localStorage.setItem("loket.settings", JSON.stringify(s));
}, mock.address().port);
await page.goto(base + "/");
await ready();
await ask("Berapa 12 kali 12?");
const httpLengths = await watchStream();
const mockCard = lastAnswer().locator('[data-tool="calculator"] [data-testid="tool-summary"]');
check("HTTP provider: a tool call streamed in pieces is assembled and run", (await mockCard.count()) === 1 && (await mockCard.textContent()).trim() === "12*12 = 144", (await mockCard.count()) ? (await mockCard.textContent()).trim() : "no card");
check("HTTP provider: the answer renders as the stream arrives", httpLengths.length >= 6, `${httpLengths.length} distinct lengths`);
check("HTTP provider: the provider's token counts are used", (await lastAnswer().locator(".lk-msg-usage").textContent()).includes("642"), (await lastAnswer().locator(".lk-msg-usage").textContent()).trim());
mock.close();

check("no console errors", errors.length === 0, errors.slice(0, 3).join(" | "));
if (process.env.LOKET_DEBUG) console.log(errors.join("\n---\n"));

// ── Live: a real provider ───────────────────────────────────────────────────
const live = process.env.LOKET_AZURE_KEY && process.env.LOKET_AZURE_ENDPOINT;
if (live) {
  await page.evaluate(({ endpoint, key, model }) => {
    const s = JSON.parse(localStorage.getItem("loket.settings") || "{}");
    Object.assign(s, { Provider: "azure", Endpoint: endpoint, ApiKey: key, Model: model, PriceIn: 0.25, PriceOut: 2 });
    localStorage.setItem("loket.settings", JSON.stringify(s));
  }, { endpoint: process.env.LOKET_AZURE_ENDPOINT, key: process.env.LOKET_AZURE_KEY, model: process.env.LOKET_AZURE_MODEL || "gpt-5-mini" });
  await page.goto(base + "/");
  await ready();
  await ask("Berapa 17% dari 3.250.000? Pakai kalkulator, jawab satu kalimat.");
  const liveLengths = await watchStream();
  const liveCard = lastAnswer().locator('[data-tool="calculator"] [data-testid="tool-summary"]');
  const liveSummary = (await liveCard.count()) ? (await liveCard.first().textContent()).trim() : "";
  const liveAnswer = (await lastAnswer().locator('[data-testid="answer"]').count()) ? await lastAnswer().locator('[data-testid="answer"]').textContent() : "";
  const liveError = await lastAnswer().locator(".lk-msg-error").count();
  check("live: Azure OpenAI calls the calculator", liveSummary.endsWith("= 552500"), liveSummary || (liveError ? "provider error" : "no tool call"));
  check("live: the answer states the calculator's result", /552[.,]?500/.test(liveAnswer), liveLengths.length + " distinct lengths");
  await ask("Jelaskan selengkapnya, minimal 200 kata: syarat, alur, waktu, dan biaya membuat KTP-el baru, dengan sumber.");
  const bodies = [];
  const collect = async (r) => {
    if (r.url().includes("/chat/completions")) bodies.push(await r.text().catch(() => ""));
  };
  page.on("response", collect);
  const ktpLengths = await watchStream();
  await page.waitForTimeout(500);
  page.off("response", collect);
  // How fast the text appears is the provider's business (Azure's content filter sends
  // bursts); that the reply came as a stream of server-sent events is ours to check.
  const deltas = Math.max(0, ...bodies.map((b) => (b.match(/"delta":\{[^}]*"content":"[^"]/g) ?? []).length));
  check("live: the reply arrives as a stream of server-sent events", deltas >= 5, `${deltas} content events, ${ktpLengths.length} redraws, bodies ${bodies.map((b) => b.length).join("/")}`);
  check("live: a service question searches the documents and cites them",
    (await lastAnswer().locator('[data-tool="search_documents"]').count()) >= 1 && (await lastAnswer().locator('[data-testid="answer"] .lk-cite').count()) >= 1);
  const usage = Number((await page.textContent('[data-testid="tokens-out"]')).replace(/,/g, ""));
  check("live: the provider's token counts reach the board", usage > 0, `${usage} out`);
}

await browser.close();
console.log(failed === 0 ? "loket: all checks passed" : `loket: ${failed} check(s) failed`);
process.exit(failed === 0 ? 0 : 1);
