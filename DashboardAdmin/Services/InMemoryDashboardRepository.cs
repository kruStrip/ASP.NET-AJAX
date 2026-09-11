using DashboardAdmin.Models;

namespace DashboardAdmin.Services;

public sealed class InMemoryDashboardRepository : IDashboardRepository
{
    private static readonly IReadOnlyList<DashboardCard> Cards =
    [
        new() { Id = 1, Title = "Выручка", Value = 125400m, Trend = Trend.Up, Unit = "руб.", Description = "Выручка за текущий месяц" },
        new() { Id = 2, Title = "Новые заказы", Value = 846m, Trend = Trend.Up, Unit = "шт.", Description = "Количество новых заказов" },
        new() { Id = 3, Title = "Конверсия", Value = 7.8m, Trend = Trend.Stable, Unit = "%", Description = "Конверсия посетителей в покупателей" },
        new() { Id = 4, Title = "Возвраты", Value = -12500m, Trend = Trend.Down, Unit = "руб.", Description = "Стоимость возвращённых заказов" },
        new() { Id = 5, Title = "Активные клиенты", Value = 3240m, Trend = Trend.Up, Unit = "шт.", Description = "Клиенты, активные за период" },
        new() { Id = 6, Title = "Отказы", Value = 2.4m, Trend = Trend.Down, Unit = "%", Description = "Доля отказов при оформлении" }
    ];

    public IEnumerable<DashboardCard> GetAllCards() => Cards;

    public DashboardCard? GetById(int id) => Cards.FirstOrDefault(card => card.Id == id);

    public IEnumerable<DashboardCard> GetLatest(int count) =>
        count <= 0 ? [] : Cards.OrderByDescending(card => card.Id).Take(count);
}
