// Local smoke test of the relay logic in plain Node: the Cloudflare-specific parts of worker.js are just
// fetch/Request/Response/FormData, so Node runs the same code. api.telegram.org is replaced by a local stub
// (Telegram is blocked for this machine too), which also lets us check exactly what the relay forwards.
import http from "node:http";
import worker from "./worker.js";

const env = { RELAY_SECRET: "test-secret" };
const base = "http://relay.local";
let failures = 0;

function check(name, ok, extra = "") {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${extra ? " — " + extra : ""}`);
  if (!ok) failures++;
}

const seen = [];
const stub = http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    seen.push({ method: req.method, url: req.url, headers: req.headers, body: Buffer.concat(chunks) });
    res.writeHead(401, { "content-type": "application/json" });
    res.end(JSON.stringify({ ok: false, error_code: 401, description: "Unauthorized" }));
  });
});
await new Promise((resolve) => stub.listen(0, "127.0.0.1", resolve));
const port = stub.address().port;
const realFetch = globalThis.fetch;
globalThis.fetch = (input, init) =>
  realFetch(String(input).replace("https://api.telegram.org", `http://127.0.0.1:${port}`), init);

// 1. No secret -> 403, Telegram is not called.
let res = await worker.fetch(new Request(base + "/bot123:ABC/sendMessage", { method: "POST" }), env);
check("no secret is refused", res.status === 403, String(res.status));

// 2. Wrong secret -> 403.
res = await worker.fetch(
  new Request(base + "/bot123:ABC/sendMessage", { method: "POST", headers: { "x-relay-secret": "nope" } }),
  env,
);
check("wrong secret is refused", res.status === 403, String(res.status));

// 3. A bot method the site does not use -> 404.
res = await worker.fetch(
  new Request(base + "/bot123:ABC/getUpdates", { method: "POST", headers: { "x-relay-secret": "test-secret" } }),
  env,
);
check("other bot methods are not relayed", res.status === 404, String(res.status));
check("nothing reached Telegram yet", seen.length === 0, `${seen.length} request(s)`);

// 4. A JSON call is forwarded with the path and the body unchanged.
res = await worker.fetch(
  new Request(base + "/bot123:ABC/sendMessage", {
    method: "POST",
    headers: { "x-relay-secret": "test-secret", "content-type": "application/json" },
    body: JSON.stringify({ chat_id: "-100123", text: "ping" }),
  }),
  env,
);
let text = await res.text();
check("sendMessage reaches Telegram", res.status === 401 && text.includes("Unauthorized"), `${res.status} ${text.slice(0, 60)}`);
const jsonCall = seen.at(-1);
check("path and route are kept", jsonCall.url === "/bot123:ABC/sendMessage", jsonCall.url);
check("JSON body is kept", jsonCall.body.toString() === '{"chat_id":"-100123","text":"ping"}', jsonCall.body.toString());

// 5. A GET with query arguments (getChat) keeps the query string.
res = await worker.fetch(
  new Request(base + "/bot123:ABC/getChat?chat_id=-100123", { method: "GET", headers: { "x-relay-secret": "test-secret" } }),
  env,
);
text = await res.text();
check("getChat reaches Telegram", res.status === 401 && text.includes("Unauthorized"), `${res.status} ${text.slice(0, 60)}`);
check("query string is kept", seen.at(-1).url === "/bot123:ABC/getChat?chat_id=-100123", seen.at(-1).url);

// 6. A multipart upload (sendPhoto/sendDocument) is forwarded as multipart with the caption and the file.
const form = new FormData();
form.set("chat_id", "-100123");
form.set("message_thread_id", "2");
form.set("caption", "чек");
form.set("photo", new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: "image/png" }), "receipt.png");
res = await worker.fetch(
  new Request(base + "/bot123:ABC/sendPhoto", { method: "POST", headers: { "x-relay-secret": "test-secret" }, body: form }),
  env,
);
text = await res.text();
check("sendPhoto reaches Telegram", res.status === 401 && text.includes("Unauthorized"), `${res.status} ${text.slice(0, 60)}`);
const upload = seen.at(-1);
const uploadBody = upload.body.toString("latin1");
check("upload path is kept", upload.url === "/bot123:ABC/sendPhoto", upload.url);
check("multipart content-type is kept", String(upload.headers["content-type"]).startsWith("multipart/form-data; boundary="),
  String(upload.headers["content-type"]));
check("upload carries content-length", Number(upload.headers["content-length"]) === upload.body.length,
  `${upload.headers["content-length"]} / ${upload.body.length}`);
check("caption and file are inside the upload",
  upload.body.includes(Buffer.from("чек")) && upload.body.includes(Buffer.from("receipt.png")),
  `${upload.body.length} bytes`);
check("the relay secret is not required by Telegram", upload.headers["x-relay-secret"] === undefined,
  String(upload.headers["x-relay-secret"]));

stub.close();
console.log(failures === 0 ? "\nall relay checks passed" : `\n${failures} relay check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
