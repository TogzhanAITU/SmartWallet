using BudgetSummary.Blazor;
using BudgetSummary.Blazor.Services;
using Budget_Summary;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<ITaxFeeAdapter, ManagedTaxFeeAdapter>();
builder.Services.AddSingleton<BudgetState>();

await builder.Build().RunAsync();