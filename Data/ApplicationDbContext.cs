using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Models;

namespace TimeManagement.Data;

/// <summary>
/// EF Core context backing the app. Inherits the Identity schema
/// (users, roles, claims, logins, tokens). The course/assessment/document
/// hierarchy is tracked here.
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Course> Courses => Set<Course>();

    public DbSet<Assessment> Assessments => Set<Assessment>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user => user.Property(u => u.FullName).HasMaxLength(100).IsRequired());

        builder.Entity<Course>(course =>
        {
            course.Property(c => c.Title).HasMaxLength(200).IsRequired();
            course.HasIndex(c => c.UserId);
        });

        builder.Entity<Assessment>(assessment =>
        {
            assessment.Property(a => a.Title).HasMaxLength(200).IsRequired();
            assessment.Property(a => a.DueDateConfirmed).HasDefaultValue(false);
            assessment.HasIndex(a => a.CourseId);

            assessment.HasOne(a => a.Course)
                .WithMany(c => c.Assessments)
                .HasForeignKey(a => a.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Document>(document =>
        {
            document.Property(d => d.OriginalFileName).HasMaxLength(260).IsRequired();
            document.Property(d => d.StoredFileName).HasMaxLength(260).IsRequired();
            document.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
            document.HasIndex(d => d.AssessmentId);

            document.HasOne(d => d.Assessment)
                .WithMany(a => a.Documents)
                .HasForeignKey(d => d.AssessmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChatMessage>(chatMessage =>
        {
            chatMessage.Property(m => m.Role).HasMaxLength(20).IsRequired();
            chatMessage.Property(m => m.SourceDocument).HasMaxLength(260);
            chatMessage.HasIndex(m => m.UserId);
            chatMessage.HasIndex(m => m.CourseId);

            chatMessage.HasOne(m => m.Course)
                .WithMany()
                .HasForeignKey(m => m.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
