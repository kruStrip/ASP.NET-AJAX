// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

document.querySelectorAll('.filter-tag').forEach(tag => {
    tag.addEventListener('click', () => {
        document.querySelectorAll('.filter-tag').forEach(item => item.classList.remove('active'));
        tag.classList.add('active');
    });
});
