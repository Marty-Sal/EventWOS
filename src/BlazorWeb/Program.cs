using Blazored.LocalStorage;
using EventWOS.BlazorWeb;
using EventWOS.BlazorWeb.Auth;
using EventWOS.BlazorWeb.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ─── HTTP Client ──────────────────────────────────────────────────────────────
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5000";
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBase) });

// ─── Auth ─────────────────────────────────────────────────────────────────────
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AppAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<AppAuthStateProvider>());

// ─── App Services ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<IAuthApiService, AuthApiService>();
builder.Services.AddScoped<IUserApiService, UserApiService>();
builder.Services.AddScoped<ISessionApiService, SessionApiService>();
builder.Services.AddScoped<IVendorApiService, VendorApiService>();
builder.Services.AddScoped<IEventApiService, EventApiService>();
builder.Services.AddScoped<IManagerApiService, ManagerApiService>();
builder.Services.AddScoped<PermissionGuard>();
builder.Services.AddScoped<IAnalyticsApiService, AnalyticsApiService>();
builder.Services.AddScoped<IPaymentApiService, PaymentApiService>();
builder.Services.AddScoped<TokenRefreshService>();
builder.Services.AddScoped<NotificationHubService>();

await builder.Build().RunAsync();
