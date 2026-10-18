(() => {
    const input = document.getElementById('searchInput');
    const grid = document.getElementById('catalogGrid');
    if (!input || !grid) return;

    const initialGridHtml = grid.innerHTML;
    let timeoutId;

    input.addEventListener('input', () => {
        clearTimeout(timeoutId);
        const query = input.value.trim();

        if (query.length < 2) {
            grid.innerHTML = initialGridHtml;
            return;
        }

        timeoutId = setTimeout(() => searchProducts(query), 300);
    });

    async function searchProducts(query) {
        grid.innerHTML = '<div class="col-12 text-center py-5"><div class="spinner-border text-primary" role="status"><span class="visually-hidden">Загрузка...</span></div></div>';
        try {
            const response = await fetch('/Catalog/Search?query=' + encodeURIComponent(query));
            if (!response.ok) throw new Error('HTTP ' + response.status);
            const html = await response.text();
            grid.innerHTML = html.trim()
                ? html
                : '<div class="col-12"><div class="alert alert-info">Ничего не найдено</div></div>';
        } catch (error) {
            grid.innerHTML = '<div class="col-12"><div class="alert alert-danger">Ошибка поиска</div></div>';
            console.error(error);
        }
    }
})();
