// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

document.querySelectorAll(".mark-completed").forEach(button => {
    button.addEventListener("click", async () => {
        const token = document.querySelector('meta[name="csrftoken"]')?.content;
        const id = button.dataset.taskId;
        const response = await fetch(`/Tasks/Index?handler=MarkCompleted&id=${id}`, {
            method: "POST",
            headers: { "X-CSRF-TOKEN": token }
        });
        const message = document.querySelector("#ajax-message");
        if (!response.ok) {
            message.textContent = `Ошибка: ${response.status}`;
            message.className = "alert alert-danger";
            return;
        }
        const card = document.querySelector(`[data-task-card="${id}"]`);
        const status = card?.querySelector(".task-status");
        if (status) {
            status.textContent = "Выполнена";
            status.className = "badge bg-success task-status";
        }
        button.remove();
        message.textContent = "Задача отмечена выполненной.";
        message.className = "alert alert-success";
    });
});
