// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

document.querySelectorAll('.filter-tag').forEach(tag => {
    tag.addEventListener('click', () => {
        document.querySelectorAll('.filter-tag').forEach(item => item.classList.remove('active'));
        tag.classList.add('active');
    });
});

document.addEventListener('DOMContentLoaded', () => {
    const badge = document.getElementById('cartBadge');
    if (!badge) return;

    fetch('/Catalog/GetCartCount')
        .then(response => {
            if (!response.ok) throw new Error('HTTP ' + response.status);
            return response.json();
        })
        .then(data => { badge.textContent = data.count; })
        .catch(error => console.error('Не удалось получить количество товаров:', error));
});
