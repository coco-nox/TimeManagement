namespace TimeManagement.Pages.Tutor;

/// <summary>Model for _TutorTabs: which tab is showing ("chat", "quiz",
/// "report", "write", "studyguide") and the course to keep selected.</summary>
public sealed record TutorTabsView(string Active, int? CourseId);
