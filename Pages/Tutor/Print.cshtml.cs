using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

namespace TimeManagement.Pages.Tutor;

/// <summary>
/// A clean, print-ready page for a paper (Write tab) or a study guide -
/// no sidebar or buttons in the printout - that opens the browser's print
/// dialog straight away, where "Save as PDF" makes the PDF download.
/// </summary>
public class PrintModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager) : PageModel
{
    private readonly ApplicationDbContext _db = db;
    private readonly UserManager<ApplicationUser> _userManager = userManager;

    public string Heading { get; set; } = string.Empty;

    public string? Eyebrow { get; set; }

    public string? Subtitle { get; set; }

    /// <summary>Already sanitised (papers) or encoded (study guides) HTML.</summary>
    public string BodyHtml { get; set; } = string.Empty;

    public bool IsStudyGuide { get; set; }

    public async Task<IActionResult> OnGetAsync(int? documentId, int? guideId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        if (documentId.HasValue)
        {
            var document = await _db.StudyDocuments
                .Include(d => d.Course)
                .Include(d => d.Assessment)
                .FirstOrDefaultAsync(d => d.Id == documentId.Value && d.UserId == user.Id);
            if (document == null)
            {
                return NotFound();
            }

            Heading = document.Title;
            Subtitle = document.Assessment != null ? $"{document.Course?.Title} · {document.Assessment.Title}" : document.Course?.Title;
            // Already sanitised on save; again here, since it's written out raw.
            BodyHtml = DocumentHtml.Sanitize(document.ContentHtml);
            return Page();
        }

        if (guideId.HasValue)
        {
            var guide = await _db.StudyGuides
                .Include(g => g.Assessment).ThenInclude(a => a!.Course)
                .FirstOrDefaultAsync(g => g.Id == guideId.Value && g.UserId == user.Id);
            var content = guide == null ? null : StudyGuideService.Parse(guide.ContentJson);
            if (guide?.Assessment == null || content == null)
            {
                return NotFound();
            }

            IsStudyGuide = true;
            Eyebrow = $"{guide.Assessment.Course?.Title} · Study guide";
            Heading = guide.Assessment.Title;
            Subtitle = (guide.Assessment.DueDate is DateTime due ? $"{due:dddd d MMMM} · " : "") + $"built from: {guide.SourceNames}";
            BodyHtml = StudyGuideHtml.Render(content);
            return Page();
        }

        return NotFound();
    }
}
