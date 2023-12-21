using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.EventBroker.Controllers;
using Coflnet.Sky.Core;
using Coflnet.Payments.Client.Model;
using System;
using System.Runtime.Serialization;
using User = Coflnet.Sky.EventBroker.Models.User;

namespace Coflnet.Sky.EventBroker.Services
{

    public class BaseBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<BaseBackgroundService> logger;
        private Prometheus.Counter cleanupCount = Prometheus.Metrics.CreateCounter("sky_eventbroker_cleanup", "How many events were cleaned up");

        public BaseBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<BaseBackgroundService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.config = config;
            this.logger = logger;
        }
        /// <summary>
        /// Called by asp.net on startup
        /// </summary>
        /// <param name="stoppingToken">is canceled when the applications stops</param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Migrating database");
            using (var scope = scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<EventDbContext>();
                // make sure all migrations are applied
                await context.Database.MigrateAsync();
            }
            logger.LogInformation("Starting consumer");
            var flipCons = Kafka.KafkaConsumer.Consume(config, config["TOPICS:TRANSACTIONS"], async lp =>
            {
                try
                {

                    using var scope = scopeFactory.CreateScope();
                    var service = GetService(scope);
                    await service.NewTransaction(lp);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while processing transaction");
                    throw;
                }
            }, stoppingToken, "sky-eventbroker", AutoOffsetReset.Earliest, new TransactionDeserializer());
            var verfify = Kafka.KafkaConsumer.ConsumeBatch<VerificationEvent>(config, config["TOPICS:VERIFIED"], async batch =>
            {
                try
                {
                    foreach (var lp in batch)
                    {
                        logger.LogInformation("Verification event received for {user}", lp.UserId);
                        using var scope = scopeFactory.CreateScope();
                        var service = GetService(scope);
                        await service.Verified(lp.UserId, lp.MinecraftUuid, lp.ExistingConCount);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while processing verification");
                    throw;
                }
            }, stoppingToken, "sky-eventbroker", 2);
            var notification = Kafka.KafkaConsumer.ConsumeBatch<string>(config, config["TOPICS:NOTIFICATIONS"], async batch =>
            {
                try
                {
                    foreach (var lp in batch)
                    {
                        using IServiceScope scope = await ProcessNotification(lp);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while processing notification");
                }
            }, stoppingToken, "sky-eventbroker", 2);

            var cleanUp = Task.Run(async () =>
            {
                logger.LogInformation("Starting cleanup task");
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = GetService(scope);
                        var count = await service.CleanDb();
                        cleanupCount.Inc(count);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Error while cleaning db");
                        throw;
                    }
                }
                logger.LogInformation("Stopping cleanup task");
            });

            stoppingToken.Register(() =>
            {
                // force sending notifications
                Console.WriteLine("quiting");
            });

            await Task.WhenAny(flipCons, verfify, cleanUp, notification);
            logger.LogError("One task exited");
            throw new Exception("a background task exited");
        }

        private async Task<IServiceScope> ProcessNotification(string lp)
        {
            var notification = Newtonsoft.Json.JsonConvert.DeserializeObject<FirebaseNotification>(lp);
            logger.LogInformation("Notification event received for {user}", notification.data["userId"]);
            var scope = scopeFactory.CreateScope();
            var service = GetService(scope);
            await service.AddMessage(new MessageContainer()
            {
                Message = notification.body,
                SourceSubId = notification.data["subId"],
                ImageLink = notification.icon,
                Data = notification.data,
                SourceType = "subscription",
                Link = notification.click_action,
                Summary = notification.title,
                User = new User()
                {
                    UserId = notification.data["userId"]
                }
            });
            return scope;
        }

        [DataContract]
        public class VerificationEvent
        {
            /// <summary>
            /// UserId of the user
            /// </summary>
            /// <value></value>
            [DataMember(Name = "userId")]
            public string UserId { get; set; }
            /// <summary>
            /// Minecraft uuid of the verified account
            /// </summary>
            /// <value></value>
            [DataMember(Name = "uuid")]
            public string MinecraftUuid { get; set; }
            /// <summary>
            /// How many existing verifications are on this minecraft account
            /// </summary>
            [DataMember(Name = "existing")]
            public int ExistingConCount { get; set; }
        }


        public class TransactionDeserializer : IDeserializer<TransactionEvent>
        {
            public TransactionEvent Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<TransactionEvent>(System.Text.Encoding.UTF8.GetString(data));
            }
        }

        private MessageService GetService(IServiceScope scope)
        {
            return scope.ServiceProvider.GetRequiredService<MessageService>();
        }
    }
}