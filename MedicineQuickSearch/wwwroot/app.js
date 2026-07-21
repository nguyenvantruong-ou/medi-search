const keywordInput = document.querySelector("#keyword");
const urlInput = document.querySelector("#urlInput");
const addUrlButton = document.querySelector("#addUrl");
const searchButton = document.querySelector("#search");
const clearButton = document.querySelector("#clear");
const urlList = document.querySelector("#urlList");
const providerStatus = document.querySelector("#providerStatus");
const historyList = document.querySelector("#history");
const resultsGrid = document.querySelector("#results");
const resultCount = document.querySelector("#resultCount");
const cacheState = document.querySelector("#cacheState");
const imagePreview = document.createElement("div");
imagePreview.className = "image-preview";
imagePreview.setAttribute("aria-hidden", "true");
imagePreview.innerHTML = '<img alt="">';
document.body.appendChild(imagePreview);
const imagePreviewImg = imagePreview.querySelector("img");

let urls = [
  "https://example.com/search?q={keyword}"
];
let history = JSON.parse(localStorage.getItem("medicineQuickSearchHistory") || "[]");

renderUrls();
renderHistory();

addUrlButton.addEventListener("click", addUrl);
urlInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter") addUrl();
});
searchButton.addEventListener("click", search);
clearButton.addEventListener("click", () => {
  keywordInput.value = "";
  urlInput.value = "";
  resultsGrid.className = "results-grid empty";
  resultsGrid.textContent = "Nhập từ khóa rồi chạy tìm kiếm.";
  providerStatus.className = "provider-list empty";
  providerStatus.textContent = "Chưa có lượt tìm kiếm.";
  resultCount.textContent = "";
  cacheState.textContent = "";
});

function addUrl() {
  const value = urlInput.value.trim();
  if (!value || urls.some((url) => url.toLowerCase() === value.toLowerCase())) return;
  urls.push(value);
  urlInput.value = "";
  renderUrls();
}

async function search() {
  const keyword = keywordInput.value.trim();
  if (!keyword || urls.length === 0) return;

  setLoading(true);
  providerStatus.className = "provider-list";
  providerStatus.innerHTML = urls.map((url) => `<div class="provider-item"><strong>${escapeHtml(host(url))}</strong><div class="meta">Đang chờ</div></div>`).join("");
  resultsGrid.className = "results-grid empty";
  resultsGrid.textContent = "Đang tìm trên các website nhà cung cấp...";
  resultCount.textContent = "";
  cacheState.textContent = "";

  try {
    const response = await fetch("/api/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ keyword, urls })
    });

    const payload = await response.json();
    if (!response.ok) throw new Error(payload.message || "Tìm kiếm thất bại.");

    renderProviders(payload.providers || []);
    renderResults(payload.results || []);
    resultCount.textContent = `${(payload.results || []).length} kết quả`;
    cacheState.textContent = payload.fromCache ? "Lấy từ bộ nhớ đệm" : "";
    saveHistory(keyword, urls);
  } catch (error) {
    providerStatus.className = "provider-list empty";
    providerStatus.textContent = error.message;
    resultsGrid.className = "results-grid empty";
    resultsGrid.textContent = "Không có kết quả trả về.";
  } finally {
    setLoading(false);
  }
}

function renderUrls() {
  urlList.innerHTML = urls.map((url, index) => `
    <div class="url-chip">
      <span>${escapeHtml(url)}</span>
      <button type="button" data-index="${index}">Xóa</button>
    </div>
  `).join("");

  urlList.querySelectorAll("button").forEach((button) => {
    button.addEventListener("click", () => {
      urls.splice(Number(button.dataset.index), 1);
      renderUrls();
    });
  });
}

function renderProviders(providers) {
  if (!providers.length) {
    providerStatus.className = "provider-list empty";
    providerStatus.textContent = "Không có trạng thái nhà cung cấp.";
    return;
  }

  providerStatus.className = "provider-list";
  providerStatus.innerHTML = providers.map((provider) => `
    <div class="provider-item">
      <strong>${escapeHtml(provider.provider)}</strong>
      <div class="meta">
        <span class="status-${escapeHtml(provider.status)}">${escapeHtml(provider.status)}</span>
        · ${provider.resultCount} kết quả · ${provider.elapsedMs} ms
      </div>
      ${provider.message ? `<div class="meta">${escapeHtml(provider.message)}</div>` : ""}
    </div>
  `).join("");
}

function renderResults(results) {
  if (!results.length) {
    resultsGrid.className = "results-grid empty";
    resultsGrid.textContent = "Không tìm thấy thuốc phù hợp.";
    return;
  }

  resultsGrid.className = "results-grid";
  resultsGrid.innerHTML = results.map((result) => `
    <article class="result-card">
      <div class="provider">${escapeHtml(result.provider)}</div>
      <div class="result-images">
        ${result.imageUrl ? `<img class="zoomable-image" src="${escapeAttribute(result.imageUrl)}" alt="" loading="lazy" tabindex="0">` : ""}
        ${result.screenshotUrl ? `<img class="zoomable-image" src="${escapeAttribute(result.screenshotUrl)}" alt="" loading="lazy" tabindex="0">` : ""}
      </div>
      <div class="title">${escapeHtml(result.title)}</div>
      ${result.price ? `<div class="price">${escapeHtml(result.price)}</div>` : ""}
      ${result.snippet ? `<div class="snippet">${escapeHtml(result.snippet)}</div>` : ""}
      ${result.url ? `<a href="${escapeAttribute(result.url)}" target="_blank" rel="noreferrer">Mở kết quả</a>` : ""}
    </article>
  `).join("");

  resultsGrid.querySelectorAll(".zoomable-image").forEach((image) => {
    image.addEventListener("mouseenter", () => showImagePreview(image));
    image.addEventListener("mousemove", (event) => moveImagePreview(event));
    image.addEventListener("mouseleave", hideImagePreview);
    image.addEventListener("focus", () => showImagePreview(image, true));
    image.addEventListener("blur", hideImagePreview);
  });
}

function showImagePreview(image, fromKeyboard = false) {
  imagePreviewImg.src = image.currentSrc || image.src;
  imagePreview.classList.add("is-visible");

  if (fromKeyboard) {
    const rect = image.getBoundingClientRect();
    positionImagePreview(rect.right + 16, rect.top + rect.height / 2);
  }
}

function moveImagePreview(event) {
  positionImagePreview(event.clientX + 18, event.clientY + 18);
}

function positionImagePreview(x, y) {
  const margin = 14;
  const rect = imagePreview.getBoundingClientRect();
  const left = Math.min(x, window.innerWidth - rect.width - margin);
  const top = Math.min(y, window.innerHeight - rect.height - margin);
  imagePreview.style.left = `${Math.max(margin, left)}px`;
  imagePreview.style.top = `${Math.max(margin, top)}px`;
}

function hideImagePreview() {
  imagePreview.classList.remove("is-visible");
  imagePreviewImg.removeAttribute("src");
}

function renderHistory() {
  if (!history.length) {
    historyList.className = "history-list empty";
    historyList.textContent = "Các lượt tìm gần đây sẽ hiển thị tại đây.";
    return;
  }

  historyList.className = "history-list";
  historyList.innerHTML = history.map((item, index) => `
    <button type="button" class="history-item" data-index="${index}">
      <strong>${escapeHtml(item.keyword)}</strong>
      <div class="meta">${item.urls.length} nhà cung cấp · ${escapeHtml(item.when)}</div>
    </button>
  `).join("");

  historyList.querySelectorAll("button").forEach((button) => {
    button.addEventListener("click", () => {
      const item = history[Number(button.dataset.index)];
      keywordInput.value = item.keyword;
      urls = [...item.urls];
      renderUrls();
    });
  });
}

function saveHistory(keyword, urlsForSearch) {
  history = [{ keyword, urls: urlsForSearch, when: new Date().toLocaleString() }, ...history]
    .filter((item, index, all) => all.findIndex((candidate) => candidate.keyword === item.keyword) === index)
    .slice(0, 8);
  localStorage.setItem("medicineQuickSearchHistory", JSON.stringify(history));
  renderHistory();
}

function setLoading(isLoading) {
  searchButton.disabled = isLoading;
  addUrlButton.disabled = isLoading;
  searchButton.textContent = isLoading ? "Đang tìm..." : "Tìm kiếm";
}

function host(value) {
  try {
    return new URL(value).host.replace(/^www\./, "");
  } catch {
    return value;
  }
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function escapeAttribute(value) {
  return escapeHtml(value).replaceAll("`", "&#096;");
}
