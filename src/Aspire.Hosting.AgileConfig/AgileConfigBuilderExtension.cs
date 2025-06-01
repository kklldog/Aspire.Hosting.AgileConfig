using Aspire.Hosting.ApplicationModel;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Aspire.Hosting.AgileConfig
{
    public static class AgileConfigBuilderExtension
    {
        public static IResourceBuilder<AgileConfigServerResource> AddAgileConfig(this IDistributedApplicationBuilder builder,
            [ResourceName] string name = "AgileConfig",
            string tag = ImageTags.Tag,
            int? port = null,
            int? targetPort = 5000,
            AgileConfigOption option = null,
            IDictionary<string, string>? environmentVariables = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(name);

            option ??= AgileConfigOption.Default;

            var agileConfigResource = new AgileConfigServerResource(name, option);

            return builder.AddResource(agileConfigResource)
                .WithEndpoint(port: port, targetPort: targetPort, name: AgileConfigServerResource.InternalEndpointName)
                .WithHttpEndpoint(port: port, targetPort: targetPort, name: AgileConfigServerResource.PrimaryEndpointName)
                .WithImage(ImageTags.Image, tag)
                .WithImageRegistry(ImageTags.Registry)
                .WithLifetime(ContainerLifetime.Persistent)
                .WithContainerName($"Aspire_AgileConfig_{tag}")
                .WithEnvironment(context =>
                {
                    // as default
                    context.EnvironmentVariables[nameof(option.adminConsole)] = option.adminConsole;
                    context.EnvironmentVariables["db:provider"] = option.dbProvider;
                    context.EnvironmentVariables["db:conn"] = option.dbConn;
                    context.EnvironmentVariables[nameof(option.saPassword)] = option.saPassword;
                    context.EnvironmentVariables[nameof(option.defaultApp)] = option.defaultApp;

                    if (environmentVariables != null)
                    {
                        foreach (var environmentVariable in environmentVariables)
                        {
                            context.EnvironmentVariables[environmentVariable.Key] = environmentVariable.Value;
                        }
                    }
                })
                .WithHttpHealthCheck("/home/echo")
                .WithOtlpExporter()
                ;
        }

        public static IResourceBuilder<AgileConfigAppResource> AddApp(this IResourceBuilder<AgileConfigServerResource> builder, [ResourceName] string appId, string appSecret = "")
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(appId);

            var agileConfigApp = new AgileConfigAppResource(appId, appSecret, builder.Resource);

            builder.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(async (e, c) =>
            {
                if (e.Resource is AgileConfigServerResource resource)
                {
                    var logger = e.Services.GetService<ILoggerFactory>().CreateLogger<AgileConfigServerResource>();
                    var http = e.Services.GetService<IHttpClientFactory>();
                    var httpclient = http.CreateClient();
                    httpclient.BaseAddress = new Uri(resource.PrimaryEndpoint.Url);
                    //httpclient.SetBasicAuthentication("admin", resource.Option.saPassword);
                    //aspire 升9.3.0 没有引用 IdentityModel.Client 包，所以不能使用 SetBasicAuthentication 方法
                    var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{resource.Option.saPassword}"));
                    httpclient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
                    using StringContent jsonContent = new(
                        JsonSerializer.Serialize(new
                        {
                            id = appId,
                            name = appId,
                            secret = appSecret,
                            enabled = true,
                            appAdmin = "super_admin"
                        }),
                        Encoding.UTF8,
                        "application/json");
                    var response = await httpclient.PostAsync("/api/app", jsonContent, c);
                    response.EnsureSuccessStatusCode();

                    logger.LogInformation("App {appId} be created on agile config server {server}", appId, resource.PrimaryEndpoint.Url);
                }

            });

            return builder.ApplicationBuilder.AddResource(agileConfigApp);
        }

    }
}
