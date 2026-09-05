namespace HumanToolCall;

internal static class WebUi
{
    internal const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Human Tool Call</title>
<style>
:root{color-scheme:light dark;font-family:Segoe UI,system-ui,sans-serif}*{box-sizing:border-box}body{margin:0;background:Canvas;color:CanvasText}main{width:min(780px,calc(100% - 32px));margin:32px auto 80px}header{display:flex;align-items:center;justify-content:space-between;margin-bottom:24px}h1{font-size:20px;margin:0;font-weight:600}.status{font-size:13px;opacity:.7}.empty{padding:56px 20px;text-align:center;border:1px solid color-mix(in srgb,CanvasText 15%,transparent);border-radius:10px;opacity:.75}.card{border:1px solid color-mix(in srgb,CanvasText 18%,transparent);border-radius:10px;padding:18px;margin:0 0 16px}.card h2{font-size:17px;margin:0 0 8px}.intro,.context,.why,.meta{font-size:13px;opacity:.78;white-space:pre-wrap}.question{margin:18px 0}.question>label:first-child{display:block;font-weight:600;margin-bottom:6px;white-space:pre-wrap}textarea,input[type=text]{width:100%;padding:9px 10px;border-radius:6px;border:1px solid color-mix(in srgb,CanvasText 22%,transparent);background:Canvas;color:CanvasText;font:inherit}textarea{min-height:82px;resize:vertical}.unknown{display:flex;gap:7px;align-items:center;margin-top:7px;font-size:13px}.option{display:block;border:1px solid color-mix(in srgb,CanvasText 15%,transparent);border-radius:7px;padding:10px;margin:8px 0}.option strong{display:block}.option p{font-size:13px;margin:4px 0;white-space:pre-wrap}.proscons{font-size:12px;opacity:.78;margin:5px 0}.recommended{font-size:12px;font-weight:600;margin-top:7px}.actions{display:flex;gap:8px;margin-top:16px}button{padding:8px 13px;border-radius:6px;border:1px solid color-mix(in srgb,CanvasText 22%,transparent);font:inherit;cursor:pointer}button.primary{background:Highlight;color:HighlightText;border-color:Highlight}button:disabled{opacity:.5;cursor:default}section.progress{margin-top:28px}section.progress h2{font-size:15px}.report{border-left:3px solid color-mix(in srgb,CanvasText 30%,transparent);padding:4px 0 4px 12px;margin:13px 0}.report .summary{font-weight:600}.report div{white-space:pre-wrap;font-size:13px;margin:3px 0}.error{padding:14px;border:1px solid #b44;border-radius:8px;white-space:pre-wrap}
</style>
</head>
<body>
<main>
<header><h1>Human Tool Call</h1><div id="status" class="status">Connecting…</div></header>
<div id="content"></div>
<section id="progressSection" class="progress" hidden><h2>Recent progress</h2><div id="progress"></div></section>
</main>
<script>
(() => {
  const params = new URLSearchParams(location.hash.slice(1));
  const token = params.get('token') || sessionStorage.getItem('humanToolCallToken') || '';
  if (token) sessionStorage.setItem('humanToolCallToken', token);
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);
  const content = document.getElementById('content');
  const status = document.getElementById('status');
  const progressSection = document.getElementById('progressSection');
  const progressRoot = document.getElementById('progress');
  let version = 0;

  function el(tag, cls, text) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  async function api(path, options = {}) {
    const headers = new Headers(options.headers || {});
    headers.set('X-Human-Tool-Call-Token', token);
    if (options.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
    const response = await fetch(path, { ...options, headers, cache: 'no-store' });
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    return response.status === 204 ? null : await response.json();
  }

  function paragraph(parent, cls, text, prefix) {
    if (!text) return;
    const node = el('div', cls, prefix ? `${prefix}${text}` : text);
    parent.appendChild(node);
  }

  function addQuestion(card, q, interactionId) {
    const block = el('div', 'question');
    block.dataset.answerId = q.id;
    block.appendChild(el('label', '', q.question));
    paragraph(block, 'context', q.context, 'Context: ');
    paragraph(block, 'why', q.whyItMatters, 'Why it matters: ');
    const input = el('textarea');
    input.setAttribute('aria-label', q.question);
    block.appendChild(input);
    const unknown = el('label', 'unknown');
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.addEventListener('change', () => { input.disabled = check.checked; if (check.checked) input.value = ''; });
    unknown.append(check, document.createTextNode("I don't know / I'm not sure"));
    block.appendChild(unknown);
    card.appendChild(block);
  }

  function addDecision(card, d) {
    const block = el('div', 'question');
    block.dataset.answerId = d.id;
    block.appendChild(el('label', '', d.question));
    paragraph(block, 'context', d.context, 'Context: ');
    const group = `decision_${d.id}_${Math.random().toString(36).slice(2)}`;
    for (const o of d.options || []) {
      const label = el('label', 'option');
      const radio = document.createElement('input');
      radio.type = 'radio'; radio.name = group; radio.value = o.id;
      label.appendChild(radio);
      label.appendChild(el('strong', '', ` ${o.label}`));
      label.appendChild(el('p', '', o.description));
      if (o.pros?.length) label.appendChild(el('div', 'proscons', `Pros: ${o.pros.join(' • ')}`));
      if (o.cons?.length) label.appendChild(el('div', 'proscons', `Cons: ${o.cons.join(' • ')}`));
      if (d.recommendedOptionId === o.id) label.appendChild(el('div', 'recommended', 'Recommended'));
      block.appendChild(label);
    }
    if (d.recommendationReason) paragraph(block, 'why', d.recommendationReason, 'Recommendation: ');
    const other = el('input'); other.type = 'text'; other.placeholder = "Custom answer, 'I don't know', or a question for ChatGPT";
    other.className = 'customAnswer';
    other.addEventListener('input', () => { if (other.value.trim()) block.querySelectorAll(`input[name="${group}"]`).forEach(x => x.checked = false); });
    block.querySelectorAll(`input[name="${group}"]`).forEach(x => x.addEventListener('change', () => { if (x.checked) other.value = ''; }));
    block.appendChild(other);
    card.appendChild(block);
  }

  function collectAnswers(card) {
    const answers = {};
    for (const block of card.querySelectorAll('[data-answer-id]')) {
      const id = block.dataset.answerId;
      const textarea = block.querySelector('textarea');
      if (textarea) {
        const unknown = block.querySelector('.unknown input')?.checked;
        answers[id] = unknown ? "I don't know / I'm not sure." : textarea.value.trim();
        continue;
      }
      const custom = block.querySelector('.customAnswer')?.value.trim();
      const selected = block.querySelector('input[type=radio]:checked');
      answers[id] = custom || selected?.value || "I don't know / no selection.";
    }
    return answers;
  }

  function renderInteraction(item) {
    const card = el('div', 'card');
    card.dataset.interactionId = item.id;
    card.appendChild(el('h2', '', item.kind === 'choose_path' ? 'ChatGPT needs a decision' : 'ChatGPT has a question'));
    paragraph(card, 'intro', item.intro);
    const expires = new Date(item.expiresAt);
    paragraph(card, 'meta', `Expires ${expires.toLocaleTimeString()}`);
    for (const q of item.questions || []) addQuestion(card, q, item.id);
    for (const d of item.decisions || []) addDecision(card, d);
    const actions = el('div', 'actions');
    const submit = el('button', 'primary', 'Submit');
    const dismiss = el('button', '', 'Dismiss');
    submit.addEventListener('click', async () => {
      submit.disabled = dismiss.disabled = true;
      try { await api(`/api/interactions/${encodeURIComponent(item.id)}/answer`, { method: 'POST', body: JSON.stringify({ answers: collectAnswers(card) }) }); await refresh(); }
      catch (e) { alert(`Could not submit: ${e.message}`); submit.disabled = dismiss.disabled = false; }
    });
    dismiss.addEventListener('click', async () => {
      submit.disabled = dismiss.disabled = true;
      try { await api(`/api/interactions/${encodeURIComponent(item.id)}/cancel`, { method: 'POST' }); await refresh(); }
      catch (e) { alert(`Could not dismiss: ${e.message}`); submit.disabled = dismiss.disabled = false; }
    });
    actions.append(submit, dismiss); card.appendChild(actions);
    return card;
  }

  function render(snapshot) {
    version = snapshot.version || 0;
    status.textContent = `${snapshot.pending.length} waiting`;
    content.replaceChildren();
    if (!snapshot.pending.length) content.appendChild(el('div', 'empty', 'Waiting for ChatGPT…'));
    else for (const item of snapshot.pending) content.appendChild(renderInteraction(item));

    progressRoot.replaceChildren();
    const reports = (snapshot.progress || []).slice().reverse();
    progressSection.hidden = !reports.length;
    for (const r of reports) {
      const node = el('div', 'report');
      node.appendChild(el('div', 'summary', r.summary));
      paragraph(node, '', r.completed, 'Completed: ');
      paragraph(node, '', r.nextStep, 'Next: ');
      paragraph(node, '', r.notableDiscovery, 'Discovery: ');
      paragraph(node, 'meta', new Date(r.createdAt).toLocaleTimeString());
      progressRoot.appendChild(node);
    }
  }

  async function refresh() { render(await api('/api/state')); }

  async function poll() {
    while (true) {
      try { render(await api(`/api/poll?version=${encodeURIComponent(version)}`)); }
      catch (e) { status.textContent = 'Disconnected'; await new Promise(r => setTimeout(r, 1500)); }
    }
  }

  if (!token) {
    status.textContent = 'Not authorized';
    content.appendChild(el('div', 'error', 'Open this page from the Human Tool Call tray menu. The tray supplies a temporary browser-session token.'));
    return;
  }

  refresh().then(poll).catch(e => {
    status.textContent = 'Disconnected';
    content.appendChild(el('div', 'error', `Could not connect to the local backend.\n${e.message}`));
  });
})();
</script>
</body>
</html>
""";
}
