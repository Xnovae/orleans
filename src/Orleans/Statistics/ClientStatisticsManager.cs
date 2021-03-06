using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class ClientStatisticsManager : IDisposable
    {
        private readonly ClientConfiguration config;
        private readonly IServiceProvider serviceProvider;
        private ClientTableStatistics tableStatistics;
        private LogStatistics logStatistics;
        private RuntimeStatisticsGroup runtimeStats;
        private readonly Logger logger;
        private readonly ILoggerFactory loggerFactory;
        public ClientStatisticsManager(ClientConfiguration config, SerializationManager serializationManager, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        {
            this.config = config;
            this.serviceProvider = serviceProvider;
            runtimeStats = new RuntimeStatisticsGroup(loggerFactory);
            logStatistics = new LogStatistics(config.StatisticsLogWriteInterval, false, serializationManager, loggerFactory);
            logger = new LoggerWrapper<ClientStatisticsManager>(loggerFactory);
            this.loggerFactory = loggerFactory;
            MessagingStatisticsGroup.Init(false);
            NetworkingStatisticsGroup.Init(false);
            ApplicationRequestsStatisticsGroup.Init(config.ResponseTimeout);
        }

        internal async Task Start(StatisticsProviderManager statsManager, IMessageCenter transport, GrainId clientId)
        {
            runtimeStats.Start();

            // Configure Metrics
            IProvider statsProvider = null;
            if (!string.IsNullOrEmpty(config.StatisticsProviderName))
            {
                var extType = config.StatisticsProviderName;
                statsProvider = statsManager.GetProvider(extType);
                var metricsDataPublisher = statsProvider as IClientMetricsDataPublisher;
                if (metricsDataPublisher == null)
                {
                    var msg = String.Format("Trying to create {0} as a metrics publisher, but the provider is not configured."
                        , extType);
                    throw new ArgumentException(msg, "ProviderType (configuration)");
                }
                var configurableMetricsDataPublisher = metricsDataPublisher as IConfigurableClientMetricsDataPublisher;
                if (configurableMetricsDataPublisher != null)
                {
                    configurableMetricsDataPublisher.AddConfiguration(
                        config.DeploymentId, config.DNSHostName, clientId.ToString(), transport.MyAddress.Endpoint.Address);
                }
                tableStatistics = new ClientTableStatistics(transport, metricsDataPublisher, runtimeStats, this.loggerFactory)
                {
                    MetricsTableWriteInterval = config.StatisticsMetricsTableWriteInterval
                };
            }
            else if (config.UseAzureSystemStore)
            {
                // Hook up to publish client metrics to Azure storage table
                var publisher = AssemblyLoader.LoadAndCreateInstance<IClientMetricsDataPublisher>(Constants.ORLEANS_AZURE_UTILS_DLL, logger, this.serviceProvider);
                await publisher.Init(config, transport.MyAddress.Endpoint.Address, clientId.ToParsableString());
                tableStatistics = new ClientTableStatistics(transport, publisher, runtimeStats, this.loggerFactory)
                {
                    MetricsTableWriteInterval = config.StatisticsMetricsTableWriteInterval
                };
            }

            // Configure Statistics
            if (config.StatisticsWriteLogStatisticsToTable)
            {
                if (statsProvider != null)
                {
                    logStatistics.StatsTablePublisher = statsProvider as IStatisticsPublisher;
                    // Note: Provider has already been Init-ialized above.
                }
                else if (config.UseAzureSystemStore)
                {
                    var statsDataPublisher = AssemblyLoader.LoadAndCreateInstance<IStatisticsPublisher>(Constants.ORLEANS_AZURE_UTILS_DLL, logger, this.serviceProvider);
                    await statsDataPublisher.Init(false, config.DataConnectionString, config.DeploymentId,
                        transport.MyAddress.Endpoint.ToString(), clientId.ToParsableString(), config.DNSHostName);
                    logStatistics.StatsTablePublisher = statsDataPublisher;
                }
            }
            logStatistics.Start();
        }

        internal void Stop()
        {
            runtimeStats?.Stop();
            runtimeStats = null;

            if (logStatistics != null)
            {
                logStatistics.Stop();
                logStatistics.DumpCounters().WaitWithThrow(TimeSpan.FromSeconds(10));
            }

            logStatistics = null;

            tableStatistics?.Dispose();
            tableStatistics = null;
        }

        public void Dispose()
        {
            if (runtimeStats != null)
                runtimeStats.Dispose();
            runtimeStats = null;
            if (logStatistics != null)
                logStatistics.Dispose();
            logStatistics = null;
        }
    }
}
