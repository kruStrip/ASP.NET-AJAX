(() => {
    document.addEventListener('click', event => {
        const button = event.target.closest('.add-to-cart');
        if (!button || button.disabled) return;
        addToCart(button.dataset.productId, button);
    });

    async function addToCart(productId, button) {
        const originalText = button.textContent;
        button.disabled = true;
        button.innerHTML = '<span class="spinner-border spinner-border-sm" role="status"></span> Добавление...';

        try {
            const response = await fetch('/Catalog/AddToCart', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: 'id=' + encodeURIComponent(productId)
            });
            if (!response.ok) throw new Error('HTTP ' + response.status);
            const data = await response.json();

            if (!data.success) throw new Error(data.message || 'Товар недоступен');

            const badge = document.getElementById('cartBadge');
            if (badge) badge.textContent = data.cartCount;
            const toast = document.getElementById('cartToast');
            const toastText = document.getElementById('cartToastText');
            if (toast && toastText && window.bootstrap) {
                toastText.textContent = 'Товар «' + data.productName + '» добавлен в корзину';
                bootstrap.Toast.getOrCreateInstance(toast, { delay: 3000 }).show();
            }

            button.textContent = 'Добавлено ✓';
            button.classList.replace('btn-primary', 'btn-success');
            setTimeout(() => {
                button.textContent = originalText;
                button.classList.replace('btn-success', 'btn-primary');
                button.disabled = false;
            }, 2000);
        } catch (error) {
            console.error(error);
            button.textContent = 'Ошибка';
            setTimeout(() => {
                button.textContent = originalText;
                button.disabled = false;
            }, 2000);
        }
    }
})();
