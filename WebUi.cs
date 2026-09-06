namespace HumanToolCall;

internal static class WebUi
{
    internal const string Html = """
                                 <!doctype html>
                                 <html lang="en">
                                 <head>
                                 <meta charset="utf-8">
                                 <meta name="viewport" content="width=device-width,initial-scale=1">
                                 <title>HumanToolCall</title>
                                 <style>
                                 :root{color-scheme:light dark;font-family:Segoe UI,system-ui,sans-serif}*{box-sizing:border-box}body{margin:0;background:Canvas;color:CanvasText}main{width:min(780px,calc(100% - 32px));margin:32px auto 80px}header{display:flex;align-items:center;justify-content:space-between;margin-bottom:24px}h1{font-size:20px;margin:0;font-weight:600}.status{font-size:13px;opacity:.7}.empty{padding:56px 20px;text-align:center;border:1px solid color-mix(in srgb,CanvasText 15%,transparent);border-radius:10px;opacity:.75}.card{border:1px solid color-mix(in srgb,CanvasText 18%,transparent);border-radius:10px;padding:18px;margin:0 0 16px}.card h2{font-size:17px;margin:0 0 8px}.intro,.context,.why,.meta{font-size:13px;opacity:.78;white-space:pre-wrap}.question{margin:18px 0}.question>label:first-child{display:block;font-weight:600;margin-bottom:6px;white-space:pre-wrap}textarea,input[type=text]{width:100%;padding:9px 10px;border-radius:6px;border:1px solid color-mix(in srgb,CanvasText 22%,transparent);background:Canvas;color:CanvasText;font:inherit}textarea{min-height:82px;resize:vertical}.unknown{display:flex;gap:7px;align-items:center;margin-top:7px;font-size:13px}.option{display:block;border:1px solid color-mix(in srgb,CanvasText 15%,transparent);border-radius:7px;padding:10px;margin:8px 0}.option strong{display:block}.option p{font-size:13px;margin:4px 0;white-space:pre-wrap}.proscons{font-size:12px;opacity:.78;margin:5px 0}.recommended{font-size:12px;font-weight:600;margin-top:7px}.actions{display:flex;gap:8px;margin-top:16px}button{padding:8px 13px;border-radius:6px;border:1px solid color-mix(in srgb,CanvasText 22%,transparent);font:inherit;cursor:pointer}button.primary{background:Highlight;color:HighlightText;border-color:Highlight}button:disabled{opacity:.5;cursor:default}section.progress{margin-top:28px}section.progress h2{font-size:15px}.report{border-left:3px solid color-mix(in srgb,CanvasText 30%,transparent);padding:4px 0 4px 12px;margin:13px 0}.report .summary{font-weight:600}.report div{white-space:pre-wrap;font-size:13px;margin:3px 0}.error{padding:14px;border:1px solid #b44;border-radius:8px;white-space:pre-wrap}
                                 </style>
                                 </head>
                                 <body>
                                 <main>
                                 <header><h1>HumanToolCall</h1><div id="status" class="status">Connecting…</div></header>
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
                                   const interactions = new Map();
                                   const progressReports = new Map();
                                   let connected = false;

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

                                    function addQuestion(card, question) {
                                      const block = el('div', 'question');
                                      block.appendChild(el('label', '', question));
                                      const input = el('textarea');
                                      input.setAttribute('aria-label', question);
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
                                      block.appendChild(el('label', '', d.decision));
                                      const group = `decision_${Math.random().toString(36).slice(2)}`;
                                      for (const o of d.options || []) {
                                        const label = el('label', 'option');
                                        const radio = document.createElement('input');
                                         radio.type = 'radio'; radio.name = group; radio.value = o.choiceId;
                                         label.appendChild(radio);
                                         label.appendChild(el('strong', '', ` ${o.choiceId}`));
                                         label.appendChild(el('p', '', o.choice));
                                         block.appendChild(label);
                                       }
                                       if (d.recommendation) paragraph(block, 'why', d.recommendation, 'Recommendation: ');
                                      card.appendChild(block);
                                    }

                                    function collectAnswer(card) {
                                      const textarea = card.querySelector('textarea');
                                      if (textarea) {
                                        return card.querySelector('.unknown input')?.checked
                                          ? "I don't know / I'm not sure."
                                          : textarea.value.trim();
                                      }

                                      return card.querySelector('input[type=radio]:checked')?.value ?? null;
                                    }

                                   function renderInteraction(item) {
                                     const card = el('div', 'card');
                                     card.dataset.interactionId = item.id;
                                     card.dataset.createdAt = String(Date.parse(item.createdAt) || 0);
                                     card.appendChild(el('h2', '', item.kind === 'choosePath' ? 'ChatGPT needs a decision' : 'ChatGPT has a question'));
                                      const expires = new Date(item.expiresAt);
                                      paragraph(card, 'meta', `Expires ${expires.toLocaleTimeString()}`);
                                      if (item.question) addQuestion(card, item.question);
                                      if (item.decision) addDecision(card, item.decision);
                                     const actions = el('div', 'actions');
                                     const submit = el('button', 'primary', 'Submit');
                                     const dismiss = el('button', '', 'Dismiss');
                                      submit.addEventListener('click', async () => {
                                         const answer = collectAnswer(card);
                                         if (answer === null) {
                                           alert('Choose an option before submitting.');
                                           return;
                                         }
                                         submit.disabled = dismiss.disabled = true;
                                         try { await api(`/api/interactions/${encodeURIComponent(item.id)}/answer`, { method: 'POST', body: JSON.stringify({ answer }) }); }
                                       catch (e) { alert(`Could not submit: ${e.message}`); submit.disabled = dismiss.disabled = false; }
                                     });
                                     dismiss.addEventListener('click', async () => {
                                       submit.disabled = dismiss.disabled = true;
                                       try { await api(`/api/interactions/${encodeURIComponent(item.id)}/cancel`, { method: 'POST' }); }
                                       catch (e) { alert(`Could not dismiss: ${e.message}`); submit.disabled = dismiss.disabled = false; }
                                     });
                                     actions.append(submit, dismiss); card.appendChild(actions);
                                     return card;
                                   }

                                   function updateStatus() {
                                     if (connected) status.textContent = `${interactions.size} waiting`;
                                   }

                                   function updateEmptyState() {
                                     let empty = document.getElementById('emptyState');
                                     if (interactions.size) {
                                       empty?.remove();
                                       return;
                                     }

                                     if (!empty) {
                                       empty = el('div', 'empty', 'Waiting for ChatGPT…');
                                       empty.id = 'emptyState';
                                       content.appendChild(empty);
                                     }
                                   }

                                   function addInteraction(item) {
                                     if (!item?.id || interactions.has(item.id)) return;
                                     const card = renderInteraction(item);
                                     const createdAt = Number(card.dataset.createdAt);
                                     const next = [...content.querySelectorAll('[data-interaction-id]')]
                                       .find(node => Number(node.dataset.createdAt) > createdAt);
                                     if (next) content.insertBefore(card, next);
                                     else content.appendChild(card);
                                     interactions.set(item.id, card);
                                     updateEmptyState();
                                     updateStatus();
                                   }

                                   function removeInteraction(id) {
                                     const card = interactions.get(id);
                                     if (!card) return;
                                     card.remove();
                                     interactions.delete(id);
                                     updateEmptyState();
                                     updateStatus();
                                   }

                                   function addProgress(report) {
                                     if (!report?.id || progressReports.has(report.id)) return;
                                     const node = el('div', 'report');
                                     node.dataset.reportId = report.id;
                                     node.appendChild(el('div', 'summary', report.summary));
                                     paragraph(node, '', report.completed, 'Completed: ');
                                     paragraph(node, '', report.nextStep, 'Next: ');
                                     paragraph(node, '', report.notableDiscovery, 'Discovery: ');
                                     paragraph(node, 'meta', new Date(report.createdAt).toLocaleTimeString());
                                     progressRoot.prepend(node);
                                     progressReports.set(report.id, node);
                                     progressSection.hidden = false;
                                   }

                                   function removeProgress(id) {
                                     const node = progressReports.get(id);
                                     if (!node) return;
                                     node.remove();
                                     progressReports.delete(id);
                                     progressSection.hidden = !progressReports.size;
                                   }

                                   function sync(snapshot) {
                                     const pending = snapshot?.pending || [];
                                     const pendingIds = new Set(pending.map(item => item.id));
                                     for (const id of [...interactions.keys()]) {
                                       if (!pendingIds.has(id)) removeInteraction(id);
                                     }
                                     for (const item of pending) addInteraction(item);

                                     const reports = snapshot?.progress || [];
                                     const reportIds = new Set(reports.map(report => report.id));
                                     for (const id of [...progressReports.keys()]) {
                                       if (!reportIds.has(id)) removeProgress(id);
                                     }
                                     for (const report of reports) addProgress(report);

                                     updateEmptyState();
                                     updateStatus();
                                   }

                                   function handleEvent(message) {
                                     switch (message?.type) {
                                       case 'sync': sync(message.snapshot); break;
                                       case 'interactionAdded': addInteraction(message.interaction); break;
                                       case 'interactionRemoved': removeInteraction(message.interactionId); break;
                                       case 'progressAdded': addProgress(message.report); break;
                                       case 'progressRemoved': removeProgress(message.reportId); break;
                                     }
                                   }

                                   async function connect() {
                                     while (true) {
                                       try {
                                         status.textContent = 'Connecting…';
                                         const response = await fetch('/api/events', {
                                           headers: { 'X-Human-Tool-Call-Token': token },
                                           cache: 'no-store'
                                         });
                                         if (response.status === 401) {
                                           connected = false;
                                           status.textContent = 'Not authorized';
                                           return;
                                         }
                                         if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
                                         if (!response.body) throw new Error('The browser did not provide a readable event stream.');

                                         connected = true;
                                         updateStatus();
                                         const reader = response.body.getReader();
                                         const decoder = new TextDecoder();
                                         let buffer = '';

                                         while (true) {
                                           const { value, done } = await reader.read();
                                           if (done) throw new Error('Event stream closed.');
                                           buffer += decoder.decode(value, { stream: true });

                                           let newline;
                                           while ((newline = buffer.indexOf('\n')) >= 0) {
                                             const line = buffer.slice(0, newline).trim();
                                             buffer = buffer.slice(newline + 1);
                                             if (line) handleEvent(JSON.parse(line));
                                           }
                                         }
                                       }
                                       catch (e) {
                                         connected = false;
                                         status.textContent = 'Disconnected';
                                         await new Promise(resolve => setTimeout(resolve, 1500));
                                       }
                                     }
                                   }

                                   if (!token) {
                                     status.textContent = 'Not authorized';
                                     content.appendChild(el('div', 'error', 'Open this page from the HumanToolCall tray menu. The tray supplies a temporary browser-session token.'));
                                     return;
                                   }

                                   updateEmptyState();
                                   connect();
                                 })();
                                 </script>
                                 </body>
                                 </html>
                                 """;
}