using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TimeManagement.Data;
using TimeManagement.Models;
using TimeManagement.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Use an absolute path rooted at the project's ContentRootPath so the same
// TimeManage.db file is used regardless of how the app is launched (Visual
// Studio debug vs `dotnet run` resolve relative paths differently, which
// otherwise creates a new empty database each time).
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "TimeManage.db");
var connectionString = $"Data Source={dbPath}";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));

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

builder.Services.Configure<DocumentCategorizationOptions>(builder.Configuration.GetSection("DocumentCategorization"));
builder.Services.AddHttpClient<DocumentCategorizationService>();

// TutorChatService reuses the same DocumentCategorizationOptions/endpoint
// above - it's a second typed client for the same AI provider, not a
// separate integration.
builder.Services.AddHttpClient<TutorChatService>();

builder.Services.AddRazorPages(options =>
{
    // Everything requires a signed-in user except the pages opted out below.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Privacy");
    options.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// Apply any pending migrations at startup so a fresh clone just runs.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

app.Run();
