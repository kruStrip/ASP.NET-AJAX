namespace DashboardAdmin.Models;

public enum Trend
{
    Up,
    Down,
    Stable
}

public sealed class DashboardCard
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public decimal Value { get; init; }
    public Trend Trend { get; init; }
    public required string Unit { get; init; }
    public required string Description { get; init; }
}
