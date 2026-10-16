using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------
// Database: SQLite via EF Core, one file for the whole app.
// ---------------------------------------------------------------------
// Add services to the container.
// Use an absolute path rooted at the project's ContentRootPath so the same
// TimeManage.db file is used regardless of how the app is launched (Visual
// Studio debug vs `dotnet run` resolve relative paths differently, which
// otherwise creates a new empty database each time).
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "TimeManage.db");
var connectionString = $"Data Source={dbPath}";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));

// ---------------------------------------------------------------------
// Identity: user accounts, password rules, and lockout policy.
// ---------------------------------------------------------------------
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        // Password rules. Enforced server-side by Identity and surfaced
        // to the user as validation errors on the registration form.
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = false;

        // One account per email address.
        options.User.RequireUniqueEmail = true;

        // Lock an account for 5 minutes after 5 failed attempts, to blunt
        // brute-force guessing.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

        // Email confirmation is out of scope for Sprint 1 (no mail sender yet).
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

// ---------------------------------------------------------------------
// Auth cookie: where Identity sends signed-out users, and how long a
// session lasts.
// ---------------------------------------------------------------------
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;

    // Always HTTPS-only in production. In development the "http" launch
    // profile has no HTTPS port, and a Secure cookie would never be sent
    // back — making login look broken.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

// ---------------------------------------------------------------------
// AI providers: Gemini and Groq config (key/endpoint/model), the
// "AiRouting" task -> provider mapping, and AiClient - the one typed
// HttpClient every AI feature goes through (see AiClient for the
// fallback-to-the-other-provider behaviour). Keys come from User Secrets
// (Gemini:ApiKey / Groq:ApiKey), never appsettings.json.
// ---------------------------------------------------------------------
builder.Services.Configure<AiProviderOptions>(nameof(AiProvider.Gemini), builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<AiProviderOptions>(nameof(AiProvider.Groq), builder.Configuration.GetSection("Groq"));
builder.Services.Configure<AiRoutingOptions>(builder.Configuration.GetSection("AiRouting"));
builder.Services.AddHttpClient<AiClient>();

// One service per AI feature (document categorisation, Tutor chat, quiz
// generation, calendar scheduling, report checklists, the Analytics weekly
// summary) - each builds its
// prompt and parses the reply, and leaves the HTTP side to AiClient.
builder.Services.AddScoped<DocumentCategorizationService>();
builder.Services.AddScoped<TutorChatService>();
builder.Services.AddScoped<QuizGenerationService>();
builder.Services.AddScoped<CalendarSchedulingService>();
builder.Services.AddScoped<ReportChecklistGenerationService>();
builder.Services.AddScoped<WeeklySummaryService>();
builder.Services.AddScoped<StudyGuideService>();

// Timed study sessions (the timer bar, "Still studying?", Analytics' time
// actually studied) - see StudySessionService for how time is counted.
builder.Services.AddScoped<StudySessionService>();

// ---------------------------------------------------------------------
// Email: Gmail SMTP via MailKit, for password reset links. Address/AppPassword
// live in user-secrets locally (dotnet user-secrets set Email:Address ... /
// Email:AppPassword ...) - never appsettings.json, since an app password is
// a real credential.
// ---------------------------------------------------------------------
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddTransient<EmailSender>();

// Study-session reminders: the rules (shared by the in-app reminder card,
// email and Telegram), the once-a-minute job that sends them by email
// (same Gmail account) and Telegram while the app is running, and the
// Telegram bot that long-polls for button taps (+15 min, Cancel, ...).
// The bot token lives in User Secrets (Telegram:BotToken). Its URLs carry
// the token, so the client's HTTP request logging is removed - otherwise
// the default logging prints the token to the console.
builder.Services.AddScoped<SessionReminderService>();
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.AddHttpClient<ITelegramClient, TelegramClient>().RemoveAllLoggers();
builder.Services.AddScoped<TelegramUpdateHandler>();
builder.Services.AddHostedService<SessionReminderNotificationService>();
builder.Services.AddHostedService<TelegramBotService>();

// ---------------------------------------------------------------------
// Razor Pages: page routing plus the folder-level sign-in requirement.
// ---------------------------------------------------------------------
var razorPagesBuilder = builder.Services.AddRazorPages(options =>
{
    // Everything requires a signed-in user except the pages opted out below.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Privacy");
    options.Conventions.AllowAnonymousToPage("/Error");
});

#if DEBUG
// Recompiles .cshtml files on request so edits show up on a browser refresh
// instead of needing a full rebuild + restart. Debug-only (see the
// conditional PackageReference in the .csproj) and further gated on the
// environment here, so a Debug build never picks this up outside dev.
if (builder.Environment.IsDevelopment())
{
    razorPagesBuilder.AddRazorRuntimeCompilation();
}
#endif

var app = builder.Build();

// ---------------------------------------------------------------------
// HTTP request pipeline. Order matters here: each app.Use... call wraps
// the ones after it, so e.g. auth has to run before routing decides
// which page handles the request.
// ---------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    // Only outside Development: shows a generic error page instead of a
    // stack trace to real users, and tells browsers to always use HTTPS.
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

// Who is this request from, and are they allowed to see this page - in
// that order, since you can't authorize a request before you know who
// sent it.
app.UseAuthentication();
app.UseAuthorization();

// Wires up the actual page/static-file endpoints the routing above dispatches to.
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// ---------------------------------------------------------------------
// Startup task: bring the database schema up to date before serving
// any requests.
// ---------------------------------------------------------------------
// Apply any pending migrations at startup so a fresh clone just runs.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

app.Run();
