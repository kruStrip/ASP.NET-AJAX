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

  // Booking form: choosing a date shows that game's card; the total follows the ticket count.
  document.querySelectorAll('select[data-autosubmit]').forEach(select =>
    select.addEventListener('change', () => select.form?.submit()));
  document.querySelectorAll('select[data-price]').forEach(select => {
    const target = document.querySelector(select.dataset.totalTarget);
    const update = () => {
      if (target) target.value = `${((Number(select.value) || 0) * Number(select.dataset.price)).toLocaleString('ru-RU')} ₽`;
    };
    select.addEventListener('change', update);
    update();
  });

  // Drop-down lists in the site palette. Browsers draw the open list of a native <select> themselves (white, system
  // blue) and ignore page styles, so with a mouse the list is our own; the <select> stays and keeps the value, so
  // forms, validation and "change" listeners work as before. Touch screens keep the phone's native picker.
  if (window.matchMedia('(pointer: fine)').matches) {
    let openMenu = null;
    const close = () => { if (!openMenu) return; openMenu.list.remove(); openMenu.select.setAttribute('aria-expanded', 'false'); openMenu = null; };
    const choose = (select, index) => {
      if (index < 0 || select.options[index].disabled) return;
      const changed = select.selectedIndex !== index;
      select.selectedIndex = index;
      close();
      select.focus({ preventScroll: true });
      if (changed) select.dispatchEvent(new Event('change', { bubbles: true }));
    };
    const open = select => {
      close();
      const list = document.createElement('ul');
      list.className = 'select-menu';
      list.setAttribute('role', 'listbox');
      Array.from(select.options).forEach((option, index) => {
        const item = document.createElement('li');
        item.setAttribute('role', 'option');
        item.textContent = option.text;
        item.dataset.index = String(index);
        if (option.disabled) item.setAttribute('aria-disabled', 'true');
        if (index === select.selectedIndex) item.setAttribute('aria-selected', 'true');
        list.append(item);
      });
      list.addEventListener('mousedown', e => e.preventDefault()); // keep focus on the select
      list.addEventListener('click', e => {
        const item = e.target instanceof Element ? e.target.closest('li') : null;
        if (item) choose(select, Number(item.dataset.index));
      });
      list.addEventListener('mousemove', e => {
        const item = e.target instanceof Element ? e.target.closest('li') : null;
        if (item) highlight(item);
      });
      document.body.append(list);
      const box = select.getBoundingClientRect();
      const below = window.innerHeight - box.bottom;
      list.style.left = `${box.left + window.scrollX}px`;
      list.style.width = `${box.width}px`;
      list.style.maxHeight = `${Math.max(160, Math.min(320, Math.max(below, box.top) - 16))}px`;
      if (below < 200 && box.top > below) list.style.top = `${box.top + window.scrollY - list.offsetHeight - 6}px`;
      else list.style.top = `${box.bottom + window.scrollY + 6}px`;
      select.setAttribute('aria-expanded', 'true');
      openMenu = { select, list };
      highlight(list.children[select.selectedIndex] || list.firstElementChild);
    };
    const highlight = item => {
      if (!openMenu || !item) return;
      openMenu.list.querySelector('.active')?.classList.remove('active');
      item.classList.add('active');
      // Scroll only inside the list: scrollIntoView would also move the page and close the menu.
      const list = openMenu.list;
      if (item.offsetTop < list.scrollTop) list.scrollTop = item.offsetTop - 6;
      else if (item.offsetTop + item.offsetHeight > list.scrollTop + list.clientHeight)
        list.scrollTop = item.offsetTop + item.offsetHeight - list.clientHeight + 6;
    };
    const enhance = select => {
      if (select.multiple || select.size > 1 || select.dataset.nativeSelect !== undefined) return;
      select.setAttribute('aria-haspopup', 'listbox');
      select.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        e.preventDefault();                        // no native popup
        select.focus({ preventScroll: true });
        openMenu?.select === select ? close() : open(select);
      });
      select.addEventListener('keydown', e => {
        const menu = openMenu?.select === select ? openMenu : null;
        if (!menu) {
          if (e.key === ' ' || e.key === 'Enter' || (e.altKey && e.key === 'ArrowDown')) { e.preventDefault(); open(select); }
          return;                                  // arrows on a closed list change the value natively
        }
        const items = Array.from(menu.list.children);
        let i = items.indexOf(menu.list.querySelector('.active'));
        if (e.key === 'ArrowDown') { e.preventDefault(); highlight(items[Math.min(items.length - 1, i + 1)]); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); highlight(items[Math.max(0, i - 1)]); }
        else if (e.key === 'Home') { e.preventDefault(); highlight(items[0]); }
        else if (e.key === 'End') { e.preventDefault(); highlight(items[items.length - 1]); }
        else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); choose(select, i); }
        else if (e.key === 'Escape' || e.key === 'Tab') { close(); }
      });
      select.addEventListener('blur', () => { if (openMenu?.select === select) close(); });
    };
    document.querySelectorAll('select').forEach(enhance);
    document.addEventListener('mousedown', e => {
      if (openMenu && !(e.target instanceof Node && (openMenu.list.contains(e.target) || openMenu.select.contains(e.target)))) close();
    });
    window.addEventListener('resize', close);
    // The list is positioned on the page, so it moves with the page; only a scrolling box around the select detaches it.
    document.addEventListener('scroll', e => {
      if (openMenu && e.target instanceof Element && e.target !== openMenu.list && e.target.contains(openMenu.select)) close();
    }, true);
  }

  // Phone fields "+7 (900) 000-00-00": digits are typed, the brackets, spaces and dashes appear by themselves.
  // A pasted 8 900 … or +7 900 … is understood too; the server normalises the number again in any case.
  const formatPhone = digits => {
    const d = digits.slice(0, 10);
    if (!d) return '';
    let out = '+7 (' + d.slice(0, 3);
    if (d.length > 3) out += ') ' + d.slice(3, 6);
    if (d.length > 6) out += '-' + d.slice(6, 8);
    if (d.length > 8) out += '-' + d.slice(8, 10);
    return out;
  };
  document.querySelectorAll('input[data-phone-mask]').forEach(field => {
    // "+7" is already part of the mask, so typing starts straight from the operator code.
    const digits = () => field.value.replace(/\D/g, '');
    field.addEventListener('focus', () => { if (!field.value) field.value = '+7 ('; });
    field.addEventListener('blur', () => { if (digits().length <= 1) field.value = ''; });
    field.addEventListener('input', event => {
      if (event.inputType && event.inputType.startsWith('delete')) return; // let backspace remove separators
      let d = digits();
      if (field.value.startsWith('+7')) d = d.slice(1);                    // the 7 of the prefix is not a typed digit
      if (d.length > 10 && (d[0] === '7' || d[0] === '8')) d = d.slice(1);  // pasted 8 900 … or 7 900 …
      field.value = d ? formatPhone(d) : '+7 (';
    });
    if (field.value) field.value = formatPhone(field.value.startsWith('+7') ? digits().slice(1) : digits());
  });

  // File fields: the browser writes «Browse… / No file selected» in the language of the system, so the native control
  // is hidden and a Russian button with the chosen file name is shown instead. The input itself still sends the file.
  document.querySelectorAll('input[type="file"]').forEach(input => {
    const box = document.createElement('span');
    box.className = 'file-field';
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'small-button';
    button.textContent = input.multiple ? 'Выбрать файлы' : 'Выбрать файл';
    const name = document.createElement('span');
    name.className = 'file-name';
    const update = () => {
      const files = Array.from(input.files || []);
      name.textContent = files.length === 0 ? 'Файл не выбран' : files.length === 1 ? files[0].name : `Выбрано файлов: ${files.length}`;
      box.classList.toggle('has-file', files.length > 0);
      // The browser's own «Please select a file» would also be in the system language.
      input.setCustomValidity(input.required && files.length === 0 ? 'Выберите файл.' : '');
    };
    input.replaceWith(box);
    box.append(input, button, name);
    input.classList.add('file-native');
    button.addEventListener('click', () => input.click());
    input.addEventListener('change', update);
    input.form?.addEventListener('reset', () => setTimeout(update));
    update();
  });

  // The WhatsApp QR code changes every ~20 s and the page should notice when the number is linked: reload it.
  const refresh = document.querySelector('[data-refresh-seconds]');
  if (refresh) setTimeout(() => location.reload(), Number(refresh.dataset.refreshSeconds) * 1000);

  // Password fields get an eye button that shows or hides what was typed.
  const eye = open => `<svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>${open ? '' : '<path d="M3 3l18 18"/>'}</svg>`;
  document.querySelectorAll('input[type="password"]').forEach(field => {
    const wrap = document.createElement('span');
    wrap.className = 'password-field';
    field.replaceWith(wrap);
    wrap.append(field);
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'password-toggle';
    const update = () => {
      const shown = field.type === 'text';
      button.innerHTML = eye(!shown);
      button.setAttribute('aria-label', shown ? 'Скрыть пароль' : 'Показать пароль');
      button.setAttribute('aria-pressed', String(shown));
    };
    button.addEventListener('click', () => { field.type = field.type === 'password' ? 'text' : 'password'; update(); field.focus(); });
    update();
    wrap.append(button);
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
