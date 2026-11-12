const element = id => document.getElementById(id);
const fileInput = element("file-input");
const uploadButton = element("upload-button");
const dropZone = element("drop-zone");
const demoButton = element("demo-button");
const demoStatus = element("demo-status");
const feedback = element("file-feedback");
const documentList = element("document-list");
const sidebar = element("sidebar");
const mobileToggle = element("mobile-toggle");
const scrim = element("scrim");
const mobile = matchMedia("(max-width: 740px)");
const question = element("question");
const scope = element("scope");
const send = element("send-button");
const form = element("ask-form");
const clearChat = element("clear-chat");
const welcome = element("welcome");
const messages = element("messages");
const viewport = element("viewport");
const sourceDialog = element("source-dialog");
const deleteDialog = element("delete-dialog");
const exampleName = "Пример — доступ к VPN.md";
const exampleQuestion = "Как подать заявку на доступ к VPN?";

let documents = [];
let health = {};
let pending = false;
let uploading = false;
let refreshVersion = 0;
let sourceVersion = 0;
let documentToDelete = null;
const reindexing = new Set();

function make(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}

async function request(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    const message = problem?.detail || (response.status === 413 ? "Файл больше 20 МБ." : `Ошибка HTTP ${response.status}.`);
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}

function errorMessage(error) {
  return error instanceof TypeError ? "Не удалось связаться с сервисом." : error.message;
}

function showFeedback(message, isError = false) {
  feedback.hidden = !message;
  feedback.textContent = message;
  feedback.classList.toggle("is-error", isError);
}

function setLibraryOpen(open) {
  const active = open && mobile.matches;
  sidebar.classList.toggle("open", active);
  scrim.hidden = !active;
  mobileToggle.setAttribute("aria-expanded", String(active));
  element("workspace").inert = active;
  if (active) sidebar.querySelector(".brand").focus();
}

mobileToggle.addEventListener("click", () => setLibraryOpen(true));
scrim.addEventListener("click", () => {
  setLibraryOpen(false);
  mobileToggle.focus();
});
mobile.addEventListener("change", () => setLibraryOpen(false));
document.addEventListener("keydown", event => {
  if (event.key === "Escape" && sidebar.classList.contains("open") && !deleteDialog.open) {
    setLibraryOpen(false);
    mobileToggle.focus();
  }
});

function updateSelection() {
  for (const row of documentList.children) {
    const selected = row.dataset.id === scope.value;
    row.classList.toggle("selected", selected);
    row.querySelector(".document-select").setAttribute("aria-pressed", String(selected));
  }
}

scope.addEventListener("change", updateSelection);

function selectDocument(id) {
  scope.value = id;
  updateSelection();
  if (mobile.matches) setLibraryOpen(false);
  question.focus();
}

function documentRow(doc) {
  const row = make("li", "document-row");
  row.dataset.id = doc.id;
  const select = make("button", "document-select");
  select.type = "button";
  select.title = doc.name;
  const mark = make("span", "file-mark");
  const icon = make("span", "icon icon-file");
  icon.setAttribute("aria-hidden", "true");
  mark.append(icon);
  const label = make("span", "document-label");
  const type = doc.name.split(".").at(-1).toUpperCase();
  label.append(make("span", "document-name", doc.name),
    make("span", "document-meta", `${type} · ${doc.chunkCount} фр.`));
  select.append(mark, label);
  select.addEventListener("click", () => selectDocument(doc.id));
  row.append(select);

  const actions = make("div", "document-actions");
  if (health.embeddingModel && doc.embeddingModel !== health.embeddingModel) {
    const reindex = make("button", "document-action", "↻");
    reindex.type = "button";
    reindex.title = "Переиндексировать";
    reindex.setAttribute("aria-label", `Переиндексировать ${doc.name}`);
    reindex.disabled = reindexing.has(doc.id);
    reindex.addEventListener("click", () => reindexDocument(doc, reindex));
    actions.append(reindex);
  }
  const remove = make("button", "document-action", "×");
  remove.type = "button";
  remove.title = "Удалить";
  remove.setAttribute("aria-label", `Удалить ${doc.name}`);
  remove.addEventListener("click", () => {
    documentToDelete = doc;
    element("delete-name").textContent = doc.name;
    deleteDialog.returnValue = "cancel";
    deleteDialog.showModal();
  });
  actions.append(remove);
  row.append(actions);
  return row;
}

function renderDocuments() {
  const selected = scope.value;
  scope.replaceChildren(new Option("Все документы", ""));
  for (const doc of documents) scope.add(new Option(doc.name, doc.id));
  scope.value = documents.some(doc => doc.id === selected) ? selected : "";
  element("document-count").textContent = String(documents.length);
  documentList.replaceChildren(...documents.map(documentRow));
  updateSelection();
  demoButton.firstChild.textContent = documents.some(doc => doc.name === exampleName)
    ? "Задать вопрос "
    : "Добавить пример ";
}

async function refreshDocuments() {
  const version = ++refreshVersion;
  const next = await request("/api/documents");
  if (version === refreshVersion) {
    documents = next;
    renderDocuments();
  }
}

async function reindexDocument(doc, button) {
  reindexing.add(doc.id);
  button.disabled = true;
  showFeedback(`Индексация: ${doc.name}`);
  try {
    await request(`/api/documents/${doc.id}/reindex`, { method: "POST" });
    await refreshDocuments();
    showFeedback("Индексация завершена.");
  } catch (error) {
    showFeedback(errorMessage(error), true);
  } finally {
    reindexing.delete(doc.id);
    button.disabled = false;
  }
}

deleteDialog.addEventListener("close", async () => {
  const doc = documentToDelete;
  documentToDelete = null;
  if (!doc || deleteDialog.returnValue !== "delete") return;
  try {
    await request(`/api/documents/${doc.id}`, { method: "DELETE" });
    await refreshDocuments();
    showFeedback("Документ удалён.");
  } catch (error) {
    showFeedback(errorMessage(error), true);
  }
});

function upload(file) {
  const data = new FormData();
  data.append("file", file);
  return request("/api/documents", { method: "POST", body: data });
}

function setUploading(active) {
  uploading = active;
  uploadButton.disabled = active;
  demoButton.disabled = active;
}

async function addFiles(files) {
  if (!files.length || uploading) return;
  setUploading(true);
  const failures = [];
  let succeeded = 0;
  try {
    for (const file of files) {
      showFeedback(`Обработка: ${file.name}`);
      try {
        await upload(file);
        succeeded++;
      } catch (error) {
        failures.push(`${file.name}: ${errorMessage(error)}`);
      }
    }
    if (succeeded > 0) await refreshDocuments();
    showFeedback(failures.length ? failures.join("\n") : "Файлы готовы к поиску.", failures.length > 0);
  } catch (error) {
    showFeedback(errorMessage(error), true);
  } finally {
    setUploading(false);
  }
}

uploadButton.addEventListener("click", () => fileInput.click());
fileInput.addEventListener("change", () => {
  const files = Array.from(fileInput.files);
  fileInput.value = "";
  void addFiles(files);
});

dropZone.addEventListener("dragover", event => {
  if (!event.dataTransfer.types.includes("Files")) return;
  event.preventDefault();
  event.dataTransfer.dropEffect = uploading ? "none" : "copy";
  dropZone.classList.toggle("is-over", !uploading);
});
dropZone.addEventListener("dragleave", event => {
  if (!dropZone.contains(event.relatedTarget)) dropZone.classList.remove("is-over");
});
dropZone.addEventListener("drop", event => {
  event.preventDefault();
  dropZone.classList.remove("is-over");
  void addFiles(Array.from(event.dataTransfer.files));
});
for (const type of ["dragover", "drop"]) {
  document.addEventListener(type, event => {
    if (event.dataTransfer.types.includes("Files")) event.preventDefault();
  });
}

demoButton.addEventListener("click", async () => {
  if (uploading) return;
  demoStatus.hidden = true;
  setUploading(true);
  try {
    let doc = documents.find(item => item.name === exampleName);
    if (!doc) {
      const response = await fetch("/example.md");
      if (!response.ok) throw new Error("Не удалось загрузить пример.");
      doc = await upload(new File([await response.blob()], exampleName, { type: "text/markdown" }));
      await refreshDocuments();
    }
    question.value = exampleQuestion;
    selectDocument(doc.id);
    updateComposer();
  } catch (error) {
    demoStatus.textContent = errorMessage(error);
    demoStatus.hidden = false;
  } finally {
    setUploading(false);
  }
});

function updateComposer() {
  send.disabled = pending || question.value.trim().length === 0;
  question.style.height = "auto";
  question.style.height = `${question.scrollHeight}px`;
}

question.addEventListener("input", updateComposer);
question.addEventListener("keydown", event => {
  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    if (!send.disabled) form.requestSubmit();
  }
});

function addTurn(text) {
  const turn = make("article", "turn");
  const prompt = make("div", "question-message");
  prompt.append(make("p", "", text));
  const answer = make("div", "answer-message pending");
  answer.append(make("h2", "answer-label", "Rnx RAG"), make("div", "answer-text", "Ищу по документам…"));
  turn.append(prompt, answer);
  messages.append(turn);
  return { turn, answer };
}

function renderAnswer(container, result) {
  container.classList.remove("pending");
  const title = result.mode === "extractive" ? "Цитаты из документов" : "Rnx RAG";
  const body = make("div", "answer-text");
  const sources = new Map(result.sources.map(source => [source.number, source]));
  for (const paragraph of result.answer.split(/\n{2,}/)) {
    const line = make("p");
    for (const part of paragraph.split(/(\[\d+\])/)) {
      const source = /^\[\d+\]$/.test(part) && sources.get(Number(part.slice(1, -1)));
      if (!source) {
        line.append(part);
        continue;
      }
      const link = make("button", "citation", part);
      link.type = "button";
      link.title = source.documentName;
      link.setAttribute("aria-label", `Источник ${source.number}: ${source.documentName}`);
      link.addEventListener("click", () => openSource(source));
      line.append(link);
    }
    body.append(line);
  }
  container.replaceChildren(make("h2", "answer-label", title), body);
  if (result.warning) container.append(make("p", "answer-warning", result.warning));
  if (sources.size === 0) return;
  const cards = make("div", "source-list");
  for (const source of sources.values()) {
    const card = make("button", "source-card");
    card.type = "button";
    card.title = source.excerpt;
    card.append(make("strong", "", `[${source.number}] ${source.documentName}`), make("small", "", sourcePosition(source)));
    card.addEventListener("click", () => openSource(source));
    cards.append(card);
  }
  container.append(cards);
}

function sourcePosition(source) {
  const fragment = `Фрагмент ${source.chunk}`;
  return source.page ? `Страница ${source.page} · ${fragment}` : fragment;
}

async function openSource(source) {
  const version = ++sourceVersion;
  element("source-title").textContent = source.documentName;
  element("source-meta").textContent = sourcePosition(source);
  element("source-text").textContent = "Загрузка…";
  sourceDialog.showModal();
  try {
    const result = await request(source.url);
    if (version === sourceVersion) element("source-text").textContent = result.text;
  } catch (error) {
    if (version === sourceVersion) element("source-text").textContent = errorMessage(error);
  }
}

form.addEventListener("submit", async event => {
  event.preventDefault();
  const text = question.value.trim();
  if (!text || pending) return;
  const documentId = scope.value || null;
  pending = true;
  question.value = "";
  updateComposer();
  welcome.hidden = true;
  messages.hidden = false;
  clearChat.hidden = false;
  clearChat.disabled = true;
  const { turn, answer } = addTurn(text);
  turn.scrollIntoView({ block: "start" });
  try {
    const result = await request("/api/ask", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question: text, documentId })
    });
    renderAnswer(answer, result);
  } catch (error) {
    renderAnswer(answer, { answer: errorMessage(error), mode: "error", sources: [] });
    if (!question.value) question.value = text;
  } finally {
    pending = false;
    clearChat.disabled = false;
    updateComposer();
  }
});

clearChat.addEventListener("click", () => {
  if (pending) return;
  messages.replaceChildren();
  messages.hidden = true;
  clearChat.hidden = true;
  welcome.hidden = false;
  viewport.scrollTop = 0;
  question.focus();
});

async function start() {
  const mode = element("mode");
  const label = element("mode-label");
  try {
    health = await request("/api/health");
    const modes = [];
    if (health.chatModel) modes.push(`Генерация: ${health.chatModel}`);
    if (health.embeddingModel) modes.push(`Эмбеддинги: ${health.embeddingModel}`);
    label.textContent = modes.length ? "Ollama настроена" : "Поиск по словам";
    mode.title = modes.join("\n");
    mode.classList.add("ready");
  } catch {
    label.textContent = "Сервис недоступен";
    mode.classList.add("error");
  }
  try {
    await refreshDocuments();
  } catch (error) {
    showFeedback(errorMessage(error), true);
  }
}

void start();
