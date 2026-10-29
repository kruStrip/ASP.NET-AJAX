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
      if (form.dataset.ajaxAction === 'game-complete') {
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
  const online = document.querySelector('[data-online-count]');
  connection.on('OnlineCountChanged', count => {
    if (!online) return;
    online.textContent = `Онлайн: ${count}`;
    online.hidden = false;
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
