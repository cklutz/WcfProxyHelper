using System;
using System.Runtime.InteropServices;
using TestContracts;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Net;
using System.Diagnostics.Contracts;
#if NET6_0_OR_GREATER
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore;
using CoreWCF.Configuration;
#endif

namespace TestServer
{
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
#if NET48
                var host = new ServiceHost(typeof(Calculator));
                host.AddServiceEndpoint(typeof(ICalculator), new NetTcpBinding(),
                    "net.tcp://localhost:12345/Calculator/ICalculator");
                host.Open();
                Console.WriteLine("Hit any key to exit...");
                Console.ReadKey();
                return 0;
#elif NET6_0_OR_GREATER
                var host = CreateWebHostBuilder(args).Build();
                host.Run();
                return 0;
#else
                throw new NotSupportedException();
#endif
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return Marshal.GetHRForException(ex);
            }
        }

#if NET6_0_OR_GREATER
        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
            .UseKestrel(options =>
            {
                options.ListenLocalhost(8088);
                options.Listen(address: IPAddress.Loopback, 8443, listenOptions =>
                {
                    listenOptions.UseHttps();
                    if (System.Diagnostics.Debugger.IsAttached)
                    {
                        listenOptions.UseConnectionLogging();
                    }
                });
            })
            .UseNetTcp(12345)
            .UseStartup<Startup>();
#endif
    }

    internal class Calculator : ICalculator
    {
        public int Add(int x, int y) => x + y;
    }

#if NET6_0_OR_GREATER
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddServiceModelServices();
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            app.UseServiceModel(builder =>
            {
                builder.AddService<Calculator>();

                Console.WriteLine("=SERVICES=");
                foreach (var b in builder.Services)
                {
                    Console.WriteLine(b);
                }

                builder.AddServiceEndpoint<Calculator, ICalculator>(new CoreWCF.NetTcpBinding(), "Calculator/ICalculator");
            });
        }
    }
#endif

}