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

    // Column constraints and relationships for every entity in the app.
    // EF Core would infer reasonable defaults without this, but being
    // explicit here (max lengths, required-ness, cascade deletes) is what
    // actually shows up in the generated migrations.
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Adds one extra required column to Identity's built-in user table.
        builder.Entity<ApplicationUser>(user => user.Property(u => u.FullName).HasMaxLength(100).IsRequired());

        // A course belongs to one user; indexed since every course query
        // filters by owner.
        builder.Entity<Course>(course =>
        {
            course.Property(c => c.Title).HasMaxLength(200).IsRequired();
            course.HasIndex(c => c.UserId);
        });

        // An assessment belongs to one course. Deleting a course deletes
        // its assessments too (cascade), rather than leaving orphans.
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

        // A document belongs to one assessment; same cascade-delete reasoning.
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

        // A Tutor chat message belongs to one course; indexed on both the
        // owning user and the course since the Tutor page queries by both.
        builder.Entity<ChatMessage>(chatMessage =>
        {
            chatMessage.Property(m => m.Role).HasMaxLength(20).IsRequired();
            chatMessage.Property(m => m.SourceDocument).HasMaxLength(260);
            chatMessage.HasIndex(m => m.UserId);
            chatMessage.HasIndex(m => m.CourseId);
            // Speeds up "find this course+user's active (unarchived) conversation".
            chatMessage.HasIndex(m => new { m.CourseId, m.UserId, m.ArchivedUtc });

            chatMessage.HasOne(m => m.Course)
                .WithMany()
                .HasForeignKey(m => m.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
