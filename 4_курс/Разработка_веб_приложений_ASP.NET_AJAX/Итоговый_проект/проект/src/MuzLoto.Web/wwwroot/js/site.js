// Static external file keeps the Content Security Policy free of unsafe-inline.
for (const form of document.querySelectorAll('form[data-confirm]')) {
  form.addEventListener('submit', event => {
    if (!window.confirm(form.dataset.confirm || 'Выполнить действие?')) event.preventDefault();
  });
}
