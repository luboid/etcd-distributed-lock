using dotnet_etcd;
using EtcdLock;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace EtcdTest
{
    internal class Program
    {
        private static readonly CancellationTokenSource cancellationTokenSource = new();

        // update connection settion if it needs
        private static EtcdClient etcdClient = new EtcdClient("http://localhost:57001,http://localhost:57002,http://localhost:57003", configureChannelOptions: (options) =>
        {
            options.Credentials = ChannelCredentials.Insecure;
        });

        static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationTokenSource.Cancel();
            };


            ServiceCollection services = new();
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddConsole();
            });
            services.AddSingleton(etcdClient);
            services.AddSingleton<IEtcdLockFactory, EtcdLockFactory>();

            ServiceProvider serviceProvider = services.BuildServiceProvider();

            await using AsyncServiceScope serviceScope = serviceProvider.CreateAsyncScope();
            try
            {
                // ILoggerFactory loggerFactory = serviceScope.ServiceProvider.GetRequiredService<ILoggerFactory>();
                IEtcdLockFactory etcdLockFactory = serviceScope.ServiceProvider.GetRequiredService<IEtcdLockFactory>();

                do
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    Console.WriteLine("Waiting to acquire lock ...");
                    await using IEtcdLock? etcdLock = await etcdLockFactory.AcquireAsync("test_lock_name", 10/*lock timeout in seconds*/, cancellationTokenSource.Token)
                        .ConfigureAwait(false);
                    if (etcdLock == null)
                    {
                        await Task.Delay(1000, cancellationTokenSource.Token)
                            .ConfigureAwait(false);

                        Console.WriteLine("Failed to acquire lock.");
                        continue;
                    }

                    Console.WriteLine($"Lock was acquired {stopwatch.ElapsedMilliseconds}ms...");

                    try
                    {
                        int i = 0;
                        while (!etcdLock.CancellationToken.IsCancellationRequested && i < 30)
                        {
                            Console.WriteLine($"Working on some issue part {i} ...");
                            await Task.Delay(1000, etcdLock.CancellationToken)
                                .ConfigureAwait(false);
                            i++;
                        }

                        if (etcdLock.CancellationToken.IsCancellationRequested)
                        {
                            Console.WriteLine("Lock was released (IsCancellationRequested) ...");
                        }
                        else
                        {
                            Console.WriteLine("Work on issue completed.");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Lock was released (OperationCanceledException) ...");
                    }
                }
                while (!cancellationTokenSource.Token.IsCancellationRequested);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }

            Console.WriteLine("Press enter to exit ...");
            Console.ReadLine();
        }
    }
}
