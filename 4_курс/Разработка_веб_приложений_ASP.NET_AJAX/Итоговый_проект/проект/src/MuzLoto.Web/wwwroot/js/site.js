// Progressive enhancement: without JS every staff form keeps its regular POST handler.
(() => {
  const notice = document.querySelector('#ajax-notice');
  let connection;
  let pendingRefresh = false;
  function show(message, isError = false) {
    if (!notice) return;
    notice.textContent = message;
    notice.className = `alert ${isError ? 'error' : 'success'}`;
    notice.hidden = false;
  }

  function csrfToken() {
    return document.querySelector('meta[name="csrf-token"]')?.getAttribute('content') ?? '';
  }

  async function refreshVisibleList() {
    const current = document.querySelector('[data-live-list]');
    if (!current || pendingRefresh) return;
    pendingRefresh = true;
    try {
      const response = await fetch(location.href, {
        credentials: 'same-origin',
        headers: { 'X-Requested-With': 'XMLHttpRequest' }
      });
      if (response.status === 401) { show('Сессия истекла. Войдите повторно.', true); return; }
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
      const next = doc.querySelector('[data-live-list]');
      if (next) current.replaceChildren(...Array.from(next.childNodes).map(node => document.importNode(node, true)));
    } catch { show('Не удалось обновить список. Повторите загрузку страницы.', true); }
    finally { pendingRefresh = false; }
  }

  document.addEventListener('submit', async event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const confirmMessage = form.dataset.confirm;
    if (confirmMessage && !window.confirm(confirmMessage)) { event.preventDefault(); return; }
    if (!form.dataset.ajaxAction || !window.fetch) return;
    event.preventDefault();
    const button = form.querySelector('[type="submit"]');
    if (button) button.disabled = true;
    try {
      // The antiforgery token travels only in the X-CSRF-TOKEN header, taken from <meta name="csrf-token">.
      const body = new FormData(form);
      body.delete('__RequestVerificationToken');
      const response = await fetch(form.action, {
        method: 'POST', credentials: 'same-origin',
        headers: { 'X-Requested-With': 'XMLHttpRequest', 'X-CSRF-TOKEN': csrfToken() },
        body
      });
      if (response.status === 401 || response.status === 403) {
        show(response.status === 401 ? 'Войдите повторно.' : 'Нет прав на действие.', true);
        return;
      }
      const result = await response.json().catch(() => ({}));
      if (!response.ok) { show(result.detail || `Ошибка ${response.status}`, true); return; }
      show(result.message || 'Изменения сохранены.');
      // Edits keep the card: the refreshed list below shows the new count, total and remaining seats.
      const keepsCard = ['booking-quantity', 'booking-surcharge'].includes(form.dataset.ajaxAction);
      if (keepsCard) {
        // nothing to remove
      } else if (form.dataset.ajaxAction === 'game-complete') {
        const card = form.closest('.booking-card');
        if (card) {
          form.remove();
          const badge = document.createElement('span');
          badge.className = 'status cancelled'; badge.textContent = 'Завершено';
          card.append(badge);
        }
      } else {
        form.closest('.booking-card')?.remove();
      }
      await refreshVisibleList();
    } catch { show('Сетевая ошибка. Проверьте соединение и актуальный статус заявки.', true); }
    finally { if (button?.isConnected) button.disabled = false; }
  });

  // "Скопировать" next to a link; falls back to selecting the text when the clipboard API is unavailable.
  document.addEventListener('click', async event => {
    const button = event.target instanceof Element ? event.target.closest('[data-copy]') : null;
    if (!button) return;
    try { await navigator.clipboard.writeText(button.dataset.copy); show('Ссылка скопирована.'); }
    catch { button.previousElementSibling?.select?.(); show('Выделите ссылку и скопируйте её (Ctrl+C).', true); }
  });
  // A submit button can ask for its own confirmation (e.g. "Удалить" inside a form whose main action is "Сохранить").
  document.addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest('[data-confirm-button]') : null;
    if (button && !window.confirm(button.dataset.confirmButton)) event.preventDefault();
  });

  // Date fields "14.10.2026 19:00": digits are typed, the dots, space and colon are inserted automatically.
  document.addEventListener('input', event => {
    const field = event.target;
    if (!(field instanceof HTMLInputElement) || !field.hasAttribute('data-datetime-mask')) return;
    if (event.inputType && event.inputType.startsWith('delete')) return; // let backspace remove separators
    const d = field.value.replace(/\D/g, '').slice(0, 12);
    const parts = [d.slice(0, 2), d.slice(2, 4), d.slice(4, 8), d.slice(8, 10), d.slice(10, 12)];
    let out = parts[0];
    if (d.length > 2) out += '.' + parts[1];
    if (d.length > 4) out += '.' + parts[2];
    if (d.length > 8) out += ' ' + parts[3];
    if (d.length > 10) out += ':' + parts[4];
    field.value = out;
  });

  // Phones and tablets: the sidebar is a drawer. Without JS the #sidebar link still opens it via :target.
  const menuToggle = document.querySelector('.menu-toggle');
  if (menuToggle) {
    const setMenu = open => {
      document.body.classList.toggle('menu-open', open);
      menuToggle.setAttribute('aria-expanded', String(open));
      menuToggle.setAttribute('aria-label', open ? 'Закрыть меню' : 'Открыть меню');
      if (!open && location.hash === '#sidebar') history.replaceState(null, '', location.pathname + location.search);
    };
    menuToggle.addEventListener('click', e => { e.preventDefault(); setMenu(!document.body.classList.contains('menu-open')); });
    document.querySelectorAll('.menu-close, .menu-backdrop').forEach(el =>
      el.addEventListener('click', e => { e.preventDefault(); setMenu(false); }));
    document.addEventListener('keydown', e => { if (e.key === 'Escape') setMenu(false); });
  }

  // SignalR only runs when the self-hosted client is installed via scripts/install-client.sh.
  // Do not fall back to an external CDN: this app uses a strict same-origin CSP.
  // The layout marks staff pages with data-hub; the hub itself rejects everyone else.
  if (!window.signalR || !document.body.dataset.hub) return;
  connection = new window.signalR.HubConnectionBuilder()
    .withUrl('/hubs/bookings')
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .build();
  async function joinGroups() {
    const ids = new Set(Array.from(document.querySelectorAll('[data-session-id], [data-signalr-session]'))
      .map(x => x.dataset.sessionId || x.dataset.signalrSession).filter(Boolean));
    for (const id of ids) {
      try { await connection.invoke('JoinSession', id); }
      catch { /* permissions may have changed since page load */ }
    }
  }
  for (const eventName of ['BookingCreated', 'BookingConfirmed', 'BookingUpdated',
    'TicketsAvailabilityChanged', 'GameCompleted', 'GameReopened',
    'RefundRequested', 'RefundCancelled', 'RefundCompleted']) {
    connection.on(eventName, () => { void refreshVisibleList(); });
  }
  // The counter appears in the sidebar and in the mobile top bar.
  connection.on('OnlineCountChanged', count => {
    document.querySelectorAll('[data-online-count]').forEach(online => {
      online.textContent = `Онлайн: ${count}`;
      online.hidden = false;
    });
  });
  // The server took this connection out of a game group (assignment or role removed).
  connection.on('AccessRevoked', () => {
    show('Доступ к одной из игр отозван администратором. Список обновлён.', true);
    void refreshVisibleList();
  });
  // A reconnect is a new connection: JoinSession re-checks the rights for every group.
  connection.onreconnected(() => { void joinGroups(); void refreshVisibleList(); });
  connection.start().then(joinGroups).catch(() => { /* AJAX forms still work without SignalR */ });
})();
