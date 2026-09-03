using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;

namespace TimeManagement.Pages.Courses;

public class IndexModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public List<Course> Courses { get; set; } = new();

    /// <summary>Shown once after a successful save.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a course name.")]
        [StringLength(200, ErrorMessage = "Course names must be 200 characters or fewer.")]
        [Display(Name = "Course name")]
        public string Title { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        Courses = await LoadCoursesAsync(user.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound("Could not load your account.");
        }

        if (!ModelState.IsValid)
        {
            // Re-populate the list so the page still has something to show
            // alongside the validation error.
            Courses = await LoadCoursesAsync(user.Id);
            return Page();
        }

        var course = new Course
        {
            UserId = user.Id,
            Title = Input.Title.Trim(),
            CreatedUtc = DateTime.UtcNow
        };

        _db.Courses.Add(course);
        await _db.SaveChangesAsync();

        StatusMessage = $"\"{course.Title}\" was added.";

        // Redirect rather than returning Page() so refreshing the browser
        // doesn't re-submit the form.
        return RedirectToPage();
    }

    private async Task<List<Course>> LoadCoursesAsync(string userId)
    {
        return await _db.Courses
            .Include(c => c.Assessments)
                .ThenInclude(a => a.Documents)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedUtc)
            .ToListAsync();
    }
}
