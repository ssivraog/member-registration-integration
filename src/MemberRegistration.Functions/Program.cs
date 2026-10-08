using MemberRegistration.Core;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RegistrationTranslator>();

builder.Build().Run();
