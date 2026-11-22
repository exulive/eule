// Eule web: signs in with the same API the desktop app uses and shows the dictation history, and the agenda
// and to-do list the assistant files from it.
// The access token lives only in memory; the refresh token in localStorage ("Beni hatırla") or sessionStorage.
"use strict";

const KEY = "eule.refresh";
const $ = (id) => document.getElementById(id);
let access = null;      // { token, exp }
let user = null;
let notes = [];
let refreshing = null;  // one refresh at a time: refresh tokens rotate, a reused one ends every session

// ---------- tokens
function storedRefresh() { return localStorage.getItem(KEY) || sessionStorage.getItem(KEY); }
function store(res, remember) {
  access = { token: res.accessToken, exp: Date.parse(res.accessTokenExpiresAt) };
  user = res.user;
  localStorage.removeItem(KEY); sessionStorage.removeItem(KEY);
  (remember ? localStorage : sessionStorage).setItem(KEY, res.refreshToken);
}
function forget() {
  access = null; user = null; notes = [];
  localStorage.removeItem(KEY); sessionStorage.removeItem(KEY);
}

async function refresh() {
  if (refreshing) return refreshing;
  const run = async () => {
    const token = storedRefresh(); // read again: another tab may have just rotated it
    if (!token) return false;
    const remember = localStorage.getItem(KEY) === token;
    const r = await fetch("api/auth/refresh", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ refreshToken: token }),
    });
    if (!r.ok) { if (r.status === 401) forget(); return false; }
    store(await r.json(), remember);
    return true;
  };
  // Across tabs too, when the browser supports it.
  refreshing = (navigator.locks ? navigator.locks.request("eule-refresh", run) : run())
    .finally(() => { refreshing = null; });
  return refreshing;
}

async function api(path, opts = {}) {
  if (!access || access.exp - Date.now() < 30_000) {
    if (!(await refresh())) throw new SignedOut();
  }
  const send = () => fetch(path, {
    ...opts,
    headers: { "Content-Type": "application/json", Authorization: "Bearer " + access.token, ...(opts.headers || {}) },
  });
  let r = await send();
  if (r.status === 401) {
    if (!(await refresh())) throw new SignedOut();
    r = await send();
  }
  if (r.status === 401) { forget(); throw new SignedOut(); }
  if (!r.ok) {
    // Problem details carry a readable reason (e.g. the monthly dictation limit).
    const p = await r.json().catch(() => null);
    if (p && p.usage) showUsage(p.usage);
    throw new Error(p && p.detail ? p.detail : r.status === 429 ? "Çok fazla istek, biraz bekle." : "Sunucu hatası (" + r.status + ")");
  }
  return r.status === 204 ? null : r.json();
}
class SignedOut extends Error {}

// ---------- screens
function showLogin() { $("app").hidden = true; $("login").hidden = false; $("login-form").email.focus(); }
function showApp() {
  $("login").hidden = true; $("app").hidden = false;
  $("who").textContent = user ? (user.displayName || user.email) : "";
  load();
}
function handle(e) {
  if (e instanceof SignedOut) { showLogin(); return; }
  $("add-reply").textContent = e.message || "Bağlantı kurulamadı.";
}

$("login-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const f = ev.target, btn = f.querySelector("button");
  $("login-error").textContent = ""; btn.disabled = true;
  try {
    const r = await fetch("api/auth/login", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email: f.email.value, password: f.password.value, device: "Web · " + browserName() }),
    });
    if (r.status === 401) throw new Error("E-posta veya şifre hatalı.");
    if (r.status === 429) throw new Error("Çok fazla deneme. Bir dakika sonra tekrar dene.");
    if (!r.ok) throw new Error("Sunucuya ulaşılamadı.");
    store(await r.json(), f.remember.checked);
    f.password.value = "";
    showApp();
  } catch (e) {
    $("login-error").textContent = e.message;
  } finally { btn.disabled = false; }
});

$("logout").addEventListener("click", async () => {
  const token = storedRefresh();
  forget();
  if (token) fetch("api/auth/logout", { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ refreshToken: token }) }).catch(() => {});
  showLogin();
});

function browserName() {
  const ua = navigator.userAgent;
  return /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /Chrome\//.test(ua) ? "Chrome"
    : /Safari\//.test(ua) ? "Safari" : "Tarayıcı";
}

// ---------- data: the dictation history and the items the assistant filed from it
let dictations = [];
let view = "history";
const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;

async function load() {
  try {
    const [me, ds, ns] = await Promise.all([
      api("api/me?timeZone=" + encodeURIComponent(zone)), api("api/dictations"), api("api/notes"),
    ]);
    user = me; dictations = ds; notes = ns;
    $("who").textContent = user.displayName || user.email;
    showUsage(user.usage);
    render();
  } catch (e) { handle(e); }
}

function showUsage(u) {
  if (!u) return;
  const el = $("usage");
  el.hidden = false;
  el.textContent = `Bu ay ${u.used}/${u.limit} dikte`;
  el.classList.toggle("full", u.used >= u.limit);
  el.title = "Yenilenme: " + new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "long" }).format(new Date(u.resetsAt));
}

$("add-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const input = ev.target.text, text = input.value.trim();
  if (!text) return;
  input.disabled = true;
  $("add-reply").textContent = "Anlıyorum…";
  try {
    const res = await api("api/dictations", { method: "POST", body: JSON.stringify({
      clientId: crypto.randomUUID(), text, source: "text", timeZone: zone,
    }) });
    $("add-reply").textContent = res.reply;
    input.value = "";
    await load();
  } catch (e) { handle(e); }
  finally { input.disabled = false; input.focus(); }
});

async function patch(note, body) {
  try {
    const updated = await api("api/notes/" + note.id, { method: "PATCH", body: JSON.stringify(body) });
    Object.assign(note, updated);
    for (const d of dictations) for (const n of d.notes) if (n.id === note.id) Object.assign(n, updated);
  } catch (e) { handle(e); }
  render();
}

async function removeNote(note) {
  if (!confirm("Silinsin mi?\n\n" + note.title)) return;
  try {
    await api("api/notes/" + note.id, { method: "DELETE" });
    notes = notes.filter((n) => n.id !== note.id);
    for (const d of dictations) d.notes = d.notes.filter((n) => n.id !== note.id);
    render();
  } catch (e) { handle(e); }
}

async function removeDictation(d) {
  const what = d.notes.length ? "\n\nBu dikteden eklenenler de silinir: " + d.notes.map((n) => n.title).join(", ") : "";
  if (!confirm("Dikte silinsin mi?\n\n“" + d.text + "”" + what)) return;
  try {
    await api("api/dictations/" + d.id, { method: "DELETE" });
    const gone = new Set(d.notes.map((n) => n.id));
    dictations = dictations.filter((x) => x.id !== d.id);
    notes = notes.filter((n) => !gone.has(n.id));
    render();
  } catch (e) { handle(e); }
}

// ---------- display helpers
const dayKey = (d) => d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + d.getDate();
const KIND = { event: "Ajanda", todo: "Yapılacak", note: "Not" };

function when(n, withDay = true) {
  if (!n.dueAt) return "";
  const o = { timeZone: n.timeZone || undefined };
  if (withDay) Object.assign(o, { weekday: "short", day: "numeric", month: "long" });
  if (n.hasTime) Object.assign(o, { hour: "2-digit", minute: "2-digit" });
  return new Intl.DateTimeFormat("tr-TR", o).format(new Date(n.dueAt));
}

function dayLabel(d, now) {
  const k = dayKey(d), day = (offset) => dayKey(new Date(now.getFullYear(), now.getMonth(), now.getDate() + offset));
  if (k === day(0)) return "Bugün";
  if (k === day(-1)) return "Dün";
  if (k === day(1)) return "Yarın";
  const o = { weekday: "long", day: "numeric", month: "long" };
  if (d.getFullYear() !== now.getFullYear()) o.year = "numeric";
  return new Intl.DateTimeFormat("tr-TR", o).format(d);
}

const query = () => $("search").value.trim().toLocaleLowerCase("tr");
const has = (q, ...texts) => !q || texts.join(" ").toLocaleLowerCase("tr").includes(q);

function section(box, label, cls) {
  const h = document.createElement("h2");
  h.textContent = label;
  if (cls) h.className = cls;
  const ul = document.createElement("ul");
  box.append(h, ul);
  return ul;
}

function empty(shown, text) {
  $("empty").hidden = shown;
  $("empty").textContent = text;
}

// ---------- views
document.querySelectorAll(".tab").forEach((t) => t.addEventListener("click", () => {
  view = t.dataset.view;
  document.querySelectorAll(".tab").forEach((x) => x.setAttribute("aria-selected", x === t));
  $("show-done-label").hidden = view === "history";
  render();
}));

function render() {
  $("groups").replaceChildren();
  if (view === "agenda") renderAgenda();
  else if (view === "todo") renderTodos();
  else renderHistory();
}

/** Every dictation by the time it was said, with what the assistant made of it. */
function renderHistory() {
  const now = new Date(), q = query(), box = $("groups");
  const time = new Intl.DateTimeFormat("tr-TR", { hour: "2-digit", minute: "2-digit" });
  const list = dictations.filter((d) => has(q, d.text, ...d.notes.map((n) => n.title + " " + n.body)));
  let ul = null, lastDay = null;
  for (const d of list) {
    const at = new Date(d.receivedAt);
    if (dayKey(at) !== lastDay) { ul = section(box, dayLabel(at, now)); lastDay = dayKey(at); }
    const li = $("history-tpl").content.firstElementChild.cloneNode(true);
    li.querySelector(".clock").textContent = time.format(at);
    li.querySelector(".clock").dateTime = d.receivedAt;
    li.querySelector(".said").textContent = "“" + d.text + "”";
    const items = li.querySelector(".items");
    for (const n of d.notes) {
      const it = document.createElement("li");
      it.className = "item kind-" + n.kind + (n.done ? " is-done" : "");
      const tag = document.createElement("span");
      tag.className = "tag";
      tag.textContent = KIND[n.kind] || "Not";
      const title = document.createElement("span");
      title.className = "item-title";
      title.textContent = n.title + (n.dueAt ? "  ·  " + when(n) : "");
      it.append(tag, title);
      if (n.body) { const b = document.createElement("div"); b.className = "muted small"; b.textContent = n.body; it.append(b); }
      items.append(it);
    }
    if (!d.notes.length) {
      const it = document.createElement("li");
      it.className = "item muted";
      it.textContent = "Bu dikteden kayıtlı bir öğe kalmadı.";
      items.append(it);
    }
    li.querySelector(".meta").textContent = d.source === "voice" ? "Sesle" : "Yazıyla";
    li.querySelector(".del").title = "Dikteyi sil";
    li.querySelector(".del").addEventListener("click", () => removeDictation(d));
    ul.append(li);
  }
  empty(list.length > 0, dictations.length ? "Eşleşen dikte yok."
    : "Henüz dikte yok. Baykuşa söylediğin ya da buraya yazdığın her şey burada görünür.");
}

/** Dated items by day: overdue first, then today, tomorrow and later. */
function renderAgenda() {
  const now = new Date(), q = query(), showDone = $("show-done").checked, box = $("groups");
  const list = notes.filter((n) => n.kind === "event" && n.dueAt && (showDone || !n.done) && has(q, n.title, n.body, n.text))
    .sort((a, b) => Date.parse(a.dueAt) - Date.parse(b.dueAt));
  const late = (n) => !n.done && (n.hasTime ? new Date(n.dueAt) < now : dayKey(new Date(n.dueAt)) < dayKey(now));
  const overdue = list.filter(late);
  if (overdue.length) { const ul = section(box, "Gecikmiş · " + overdue.length, "overdue"); overdue.forEach((n) => ul.append(row(n, false))); }
  let ul = null, lastDay = null;
  for (const n of list) {
    if (late(n)) continue;
    const at = new Date(n.dueAt);
    if (dayKey(at) !== lastDay) { ul = section(box, dayLabel(at, now)); lastDay = dayKey(at); }
    ul.append(row(n, true));
  }
  empty(list.length > 0, "Ajandada bir şey yok. Tarih ya da saat içeren dikteler buraya eklenir.");
}

/** The to-do list: open items first, newest on top. */
function renderTodos() {
  const q = query(), showDone = $("show-done").checked, box = $("groups");
  const all = notes.filter((n) => n.kind === "todo" && has(q, n.title, n.body, n.text));
  const byNew = (a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt);
  const open = all.filter((n) => !n.done).sort(byNew), done = all.filter((n) => n.done).sort(byNew);
  if (open.length) { const ul = section(box, "Yapılacak · " + open.length); open.forEach((n) => ul.append(row(n, false))); }
  if (showDone && done.length) { const ul = section(box, "Tamamlanan · " + done.length); done.forEach((n) => ul.append(row(n, false))); }
  empty(open.length > 0 || (showDone && done.length > 0),
    all.length ? "Hepsi tamam." : "Yapılacak bir şey yok. Tarihsiz işler buraya eklenir.");
}

function row(n, timeOnly) {
  const li = $("note-tpl").content.firstElementChild.cloneNode(true);
  li.classList.toggle("is-done", n.done);
  const cb = li.querySelector(".done");
  cb.checked = n.done;
  cb.addEventListener("change", () => patch(n, { done: cb.checked }));
  const title = li.querySelector(".title");
  title.textContent = n.title;
  title.addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); title.blur(); }
    if (e.key === "Escape") { title.textContent = n.title; title.blur(); }
  });
  title.addEventListener("blur", () => {
    const t = title.textContent.trim();
    if (!t) { title.textContent = n.title; return; }
    if (t !== n.title) patch(n, { title: t.slice(0, 500) });
  });
  const meta = [];
  if (n.dueAt) meta.push(timeOnly ? (n.hasTime ? when(n, false) : "Gün boyu") : when(n));
  if (n.body) meta.push(n.body);
  meta.push("“" + n.text + "”");
  li.querySelector(".meta").textContent = meta.join("  ·  ");
  li.querySelector(".del").addEventListener("click", () => removeNote(n));
  return li;
}

$("search").addEventListener("input", render);
$("show-done").addEventListener("change", render);
// Dictations said to the owl on the desktop show up here without a reload.
document.addEventListener("visibilitychange", () => { if (!document.hidden && !$("app").hidden) load(); });
setInterval(() => { if (!document.hidden && !$("app").hidden && !document.activeElement?.isContentEditable) load(); }, 60_000);

// ---------- start
(async () => {
  if (storedRefresh() && (await refresh().catch(() => false))) showApp();
  else showLogin();
})();
