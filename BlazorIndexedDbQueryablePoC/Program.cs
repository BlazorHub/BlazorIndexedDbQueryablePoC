using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TG.Blazor.IndexedDB;
using BlazorIndexedDbQueryablePoC.DB;

namespace BlazorIndexedDbQueryablePoC
{
	public class Program
	{
		public static async Task Main(string[] args)
		{
			var builder = WebAssemblyHostBuilder.CreateDefault(args);
			builder.RootComponents.Add<App>("app");

			builder.Services.AddTransient(sp => new HttpClient { BaseAddress=new Uri(builder.HostEnvironment.BaseAddress) });

			ConfigureServices(builder.Services);

			await builder.Build().RunAsync();
		}

		static void ConfigureServices(IServiceCollection services)
		{
			services.AddIndexedDB(dbStore =>
			{
				dbStore.DbName="TheFactory"; //example name
				dbStore.Version=1;

				DbModel.Configure(dbStore);
			})
				.AddQuerying(DbModel.Configure);
		}
	}
}
