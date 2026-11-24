using ElmahCore.Mvc;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Dashboard.BasicAuthorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Serialization;
using PRMS.Data;
using PRMS.Hubs;
using PRMS.Services.Monitor;
using PRMS.Services.Shared;
using System;
using System.IO;

namespace PRMS
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddAntiforgery(s => s.SuppressXFrameOptionsHeader = true);

            services.Configure<CookiePolicyOptions>(options =>
            {
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            services.AddDbContext<DofmPrmsContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection")));

            // Hangfire
            services.AddHangfire(config =>
                config.UseSqlServerStorage(
                    Configuration.GetConnectionString("DefaultConnection")
                ));

            services.AddHangfireServer();

            // SignalR
            services.AddSignalR();

            // Job Monitor Services
            services.AddSingleton<JobMonitorService>();

            // Scoped: broadcaster — one per request/job, gets logger scoped to the job type
            services.AddScoped<JobBroadcaster>();

            // Hangfire jobs
            services.AddScoped<TriggerWatchdogJob>();
            services.AddScoped<UpdateDashboardJob>();

            // Watchdog
            services.AddSingleton<WatchdogRuntimeState>();
            services.AddSingleton<WatchdogHelper>(sp =>
            {
                var hub = sp.GetRequiredService<IHubContext<JobMonitorHub>>();
                var state = sp.GetRequiredService<WatchdogRuntimeState>();
                var logger = sp.GetRequiredService<ILogger<WatchdogHelper>>();
                return new WatchdogHelper(Configuration["Watchdog:ProcessName"], state, hub, logger);
            });

            // MVC
            services.AddMvc().AddJsonOptions(options =>
            {
                var resolver = options.SerializerSettings.ContractResolver;
                if (resolver != null)
                    (resolver as DefaultContractResolver).NamingStrategy = null;
            });

            services.AddDistributedMemoryCache();

            services.AddSession(options =>
            {
                options.Cookie.Name = ".YourApp.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.IdleTimeout = TimeSpan.FromMinutes(30);
            });

            services.AddSingleton<IFileProvider>(
                new PhysicalFileProvider(
                    Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")));

            services.AddElmah();
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            var guard = app.ApplicationServices.GetRequiredService<WatchdogHelper>();
            guard.KillOrphan();
            guard.RecoverStateOnStartup();

            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
                app.UseExceptionHandler("/Home/Error");

            app.UseHttpsRedirection();
            app.UsePathBase("/prms");
            app.UseStaticFiles();
            app.UseCookiePolicy();
            app.UseAuthentication();
            app.UseSession();
            app.UseElmah();

            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = new[]
                {
                    new BasicAuthAuthorizationFilter(
                        new BasicAuthAuthorizationFilterOptions
                        {
                            RequireSsl         = false,
                            SslRedirect        = false,
                            LoginCaseSensitive = true,
                            Users = new[]
                            {
                                new BasicAuthAuthorizationUser
                                {
                                    Login         = "admin",
                                    PasswordClear = "12345"
                                }
                            }
                        })
                }
            });

            // Register as recurring jobs (cron set to never — triggered manually)
            RecurringJob.AddOrUpdate<TriggerWatchdogJob>(
                "Watchdog: Start Job",
                x => x.StartWatchdog(),
                "0 0 31 2 *"
            );

            RecurringJob.AddOrUpdate<TriggerWatchdogJob>(
                "Watchdog: Stop Job",
                x => x.StopWatchdog(),
                "0 0 31 2 *"
            );

            RecurringJob.AddOrUpdate<UpdateDashboardJob>(
                "Dashboard: Update Job",
                x => x.UpdateDashboard(),
                "0 0 31 2 *"
            );

            app.UseSignalR(routes =>
            {
                routes.MapHub<JobMonitorHub>("/jobHub");
            });

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Login}/{action=Login}/{id?}");
            });
        }
    }
}