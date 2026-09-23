using Microsoft.EntityFrameworkCore;
using TaskFlowApi.Entities;

namespace TaskFlowApi.Data;

/// <summary>Контекст базы данных TaskFlow.</summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasMany(p => p.Tasks)
                .WithOne(t => t.Project)
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasIndex(t => t.Status);

            entity.HasOne(t => t.AssignedTo)
                .WithMany()
                .HasForeignKey(t => t.AssignedToId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(t => t.Comments)
                .WithOne(c => c.TaskItem)
                .HasForeignKey(c => c.TaskItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasOne(c => c.Author)
                .WithMany()
                .HasForeignKey(c => c.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(r => r.Key);
        });

        var seedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<AppUser>().HasData(new AppUser
        {
            Id = 1,
            Username = "demo",
            Email = "demo@taskflow.local",
            PasswordHash = "seed-hash"
        });

        modelBuilder.Entity<Project>().HasData(new Project
        {
            Id = 1,
            Name = "Демонстрационный проект",
            Description = "Проект, созданный при первом запуске",
            CreatedAt = seedDate
        });

        modelBuilder.Entity<TaskItem>().HasData(new TaskItem
        {
            Id = 1,
            ProjectId = 1,
            Title = "Первая демонстрационная задача",
            Description = "Пример задачи из seed-данных",
            Status = TaskItemStatus.ToDo,
            Priority = TaskPriority.Medium,
            CreatedAt = seedDate
        });
    }
}
