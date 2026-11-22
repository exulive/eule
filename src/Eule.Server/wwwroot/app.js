// Eule web: signs in with the same API the desktop app uses and shows the user's notes.
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
  if (!r.ok) throw new Error(r.status === 429 ? "Çok fazla istek, biraz bekle." : "Sunucu hatası (" + r.status + ")");
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

// ---------- notes
async function load() {
  try {
    if (!user) user = await api("api/me");
    $("who").textContent = user.displayName || user.email;
    notes = await api("api/notes");
    render();
  } catch (e) { handle(e); }
}

$("add-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const input = ev.target.text, text = input.value.trim();
  if (!text) return;
  input.disabled = true;
  try {
    const res = await api("api/dictations", { method: "POST", body: JSON.stringify({
      clientId: crypto.randomUUID(), text, source: "text",
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    }) });
    $("add-reply").textContent = res.reply;
    if (res.note) { notes.push(res.note); render(); }
    input.value = "";
  } catch (e) { handle(e); }
  finally { input.disabled = false; input.focus(); }
});

async function patch(note, body) {
  try {
    const updated = await api("api/notes/" + note.id, { method: "PATCH", body: JSON.stringify(body) });
    Object.assign(note, updated);
  } catch (e) { handle(e); }
  render();
}

async function remove(note) {
  if (!confirm("Bu not silinsin mi?\n\n" + note.title)) return;
  try {
    await api("api/notes/" + note.id, { method: "DELETE" });
    notes = notes.filter((n) => n.id !== note.id);
    render();
  } catch (e) { handle(e); }
}

// ---------- grouping and display
const dayKey = (d) => d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + d.getDate();
function groupOf(n, now) {
  if (n.done) return "done";
  if (!n.dueAt) return "none";
  const due = new Date(n.dueAt);
  const today = dayKey(now), tomorrow = dayKey(new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1));
  const k = dayKey(due);
  if (n.hasTime ? due < now : k < today) return "overdue";
  if (k === today) return "today";
  if (k === tomorrow) return "tomorrow";
  return "later";
}
const GROUPS = [
  ["overdue", "Gecikmiş"], ["today", "Bugün"], ["tomorrow", "Yarın"], ["later", "Sonra"],
  ["none", "Tarihsiz"], ["done", "Tamamlanan"],
];

function describe(n) {
  const parts = [];
  if (n.dueAt) {
    const o = { timeZone: n.timeZone || undefined, weekday: "short", day: "numeric", month: "long" };
    if (n.hasTime) { o.hour = "2-digit"; o.minute = "2-digit"; }
    parts.push(new Intl.DateTimeFormat("tr-TR", o).format(new Date(n.dueAt)));
  }
  parts.push((n.source === "voice" ? "Sesle · " : "Yazıyla · ") +
    new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "short" }).format(new Date(n.createdAt)));
  if (n.text && n.text.trim().toLocaleLowerCase("tr") !== n.title.trim().toLocaleLowerCase("tr")) parts.push("“" + n.text + "”");
  return parts.join("  ·  ");
}

// ---------- views: notes grouped by due date, or every dictation by the time it was said
let view = "notes";
function setView(v) {
  view = v;
  $("tab-notes").setAttribute("aria-selected", v === "notes");
  $("tab-history").setAttribute("aria-selected", v === "history");
  $("show-done-label").hidden = v === "history";
  render();
}
$("tab-notes").addEventListener("click", () => setView("notes"));
$("tab-history").addEventListener("click", () => setView("history"));

const matches = (n, q) => !q || (n.title + " " + n.text).toLocaleLowerCase("tr").includes(q);

function dayLabel(d, now) {
  const k = dayKey(d);
  if (k === dayKey(now)) return "Bugün";
  if (k === dayKey(new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1))) return "Dün";
  const o = { weekday: "long", day: "numeric", month: "long" };
  if (d.getFullYear() !== now.getFullYear()) o.year = "numeric";
  return new Intl.DateTimeFormat("tr-TR", o).format(d);
}

function renderHistory() {
  const now = new Date();
  const q = $("search").value.trim().toLocaleLowerCase("tr");
  const list = notes.filter((n) => matches(n, q)).sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
  const box = $("groups");
  box.replaceChildren();
  const time = new Intl.DateTimeFormat("tr-TR", { hour: "2-digit", minute: "2-digit" });
  let ul = null, lastDay = null;
  for (const n of list) {
    const at = new Date(n.createdAt), day = dayKey(at);
    if (day !== lastDay) {
      const h = document.createElement("h2");
      h.textContent = dayLabel(at, now);
      ul = document.createElement("ul");
      box.append(h, ul);
      lastDay = day;
    }
    const li = $("history-tpl").content.firstElementChild.cloneNode(true);
    li.querySelector(".clock").textContent = time.format(at);
    li.querySelector(".clock").dateTime = n.createdAt;
    li.querySelector(".said").textContent = n.text || n.title;
    const meta = [n.source === "voice" ? "Sesle" : "Yazıyla"];
    if (n.dueAt) {
      const o = { timeZone: n.timeZone || undefined, weekday: "short", day: "numeric", month: "long" };
      if (n.hasTime) { o.hour = "2-digit"; o.minute = "2-digit"; }
      meta.push("Hatırlatma: " + new Intl.DateTimeFormat("tr-TR", o).format(new Date(n.dueAt)));
    }
    if (n.done) meta.push("Tamamlandı");
    li.querySelector(".meta").textContent = meta.join("  ·  ");
    li.querySelector(".del").addEventListener("click", () => remove(n));
    ul.append(li);
  }
  $("empty").hidden = list.length > 0;
  $("empty").textContent = notes.length ? "Eşleşen dikte yok." : "Henüz dikte yok. Baykuşa söylediğin her şey burada görünür.";
}

function render() {
  if (view === "history") return renderHistory();
  const now = new Date();
  const q = $("search").value.trim().toLocaleLowerCase("tr");
  const showDone = $("show-done").checked;
  const buckets = Object.fromEntries(GROUPS.map(([k]) => [k, []]));
  for (const n of notes) {
    if (q && !(n.title + " " + n.text).toLocaleLowerCase("tr").includes(q)) continue;
    const g = groupOf(n, now);
    if (g === "done" && !showDone) continue;
    buckets[g].push(n);
  }
  const byDue = (a, b) => Date.parse(a.dueAt) - Date.parse(b.dueAt);
  const byNew = (a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt);
  const box = $("groups");
  box.replaceChildren();
  let any = false;
  for (const [key, label] of GROUPS) {
    const list = buckets[key];
    if (!list.length) continue;
    any = true;
    list.sort(key === "none" || key === "done" ? byNew : byDue);
    const h = document.createElement("h2");
    h.textContent = label + " · " + list.length;
    if (key === "overdue") h.className = "overdue";
    const ul = document.createElement("ul");
    for (const n of list) ul.append(row(n));
    box.append(h, ul);
  }
  $("empty").hidden = any;
  $("empty").textContent = notes.length && !any ? "Eşleşen not yok." : "Henüz not yok. Baykuşa söylediğin her şey burada görünür.";
}

function row(n) {
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
  li.querySelector(".meta").textContent = describe(n);
  li.querySelector(".del").addEventListener("click", () => remove(n));
  return li;
}

$("search").addEventListener("input", render);
$("show-done").addEventListener("change", render);
// Notes said to the owl on the desktop show up here without a reload.
document.addEventListener("visibilitychange", () => { if (!document.hidden && !$("app").hidden) load(); });
setInterval(() => { if (!document.hidden && !$("app").hidden && !document.activeElement?.isContentEditable) load(); }, 60_000);

// ---------- start
(async () => {
  if (storedRefresh() && (await refresh().catch(() => false))) showApp();
  else showLogin();
})();
