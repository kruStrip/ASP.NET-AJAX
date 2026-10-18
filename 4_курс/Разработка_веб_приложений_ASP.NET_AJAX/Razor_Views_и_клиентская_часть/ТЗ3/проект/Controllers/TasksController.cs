using Microsoft.AspNetCore.Mvc;
using TaskBoard.Services;

namespace TaskBoard.Controllers;

public class TasksController : Controller
{
    private readonly ITaskService _service;

    public TasksController(ITaskService service)
    {
        _service = service;
    }

    // GET /tasks
    // GET /tasks?status=all|done|active   (Уровень 3 — фильтрация)
    public IActionResult Index(string? status)
    {
        var all = _service.GetAll();

        // Уровень 3. Нормализуем фильтр и применяем его к списку.
        status = (status ?? "all").ToLowerInvariant();
        if (status != "done" && status != "active") status = "all";

        var tasks = status switch
        {
            "done" => all.Where(t => t.IsDone).ToList(),
            "active" => all.Where(t => !t.IsDone).ToList(),
            _ => all
        };

        // Уровень 1. Счётчик задач через ViewBag.
        ViewBag.Total = all.Count;
        ViewBag.DoneCount = all.Count(t => t.IsDone);
        ViewBag.Status = status;

        return View(tasks);
    }

    // GET /tasks/details/1
    public IActionResult Details(int id)
    {
        var task = _service.GetById(id);
        if (task == null) return NotFound();
        return View(task);
    }

    // GET /tasks/create
    public IActionResult Create()
    {
        return View();
    }

    // POST /tasks/create
    [HttpPost]
    public IActionResult Create(string title, string? description)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            // Убираем ошибку, которую model binding уже добавил для пустого
            // non-nullable string ("The title field is required."),
            // чтобы во View показалось наше сообщение.
            ModelState.Remove("title");
            ModelState.AddModelError("title", "Заголовок обязателен");
            return View();
        }

        _service.Add(title, description);
        return RedirectToAction(nameof(Index));
    }

    // POST /tasks/done/1
    // Уровень 3: выбранный фильтр возвращаем обратно в ссылку, чтобы он не сбрасывался.
    [HttpPost]
    public IActionResult Done(int id, string? status)
    {
        _service.MarkDone(id);
        return RedirectToAction(nameof(Index), new { status });
    }

    // POST /tasks/delete/1
    [HttpPost]
    public IActionResult Delete(int id, string? status)
    {
        _service.Delete(id);
        return RedirectToAction(nameof(Index), new { status });
    }

    // ------------------------------------------------------------------
    // Шаг 6. JSON API. Доступ только с заголовком X-Api-Key: secret123
    // (проверяется в middleware, см. Program.cs — Уровень 2).
    // ------------------------------------------------------------------

    // GET /tasks/api/list
    [HttpGet("tasks/api/list")]
    public IActionResult ApiList()
    {
        var tasks = _service.GetAll();
        return Json(tasks);
    }

    // GET /tasks/api/1
    [HttpGet("tasks/api/{id:int}")]
    public IActionResult ApiGet(int id)
    {
        var task = _service.GetById(id);
        if (task == null) return NotFound();
        return Json(task);
    }

    // POST /tasks/api/create
    [HttpPost("tasks/api/create")]
    public IActionResult ApiCreate([FromBody] CreateTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "Заголовок обязателен" });

        var task = _service.Add(request.Title, request.Description);
        return CreatedAtAction(nameof(ApiGet), new { id = task.Id }, task);
    }
}
