using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Newtonsoft.Json;
using Coflnet.Payments.Client.Model;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Commands.Shared;
using Microsoft.Extensions.Configuration;
using System.Text;
using RestSharp;

namespace Coflnet.Sky.EventBroker.Services
{
    public class MessageService
    {
        private EventDbContext db;
        private ConnectionMultiplexer connection;
        private Payments.Client.Api.ProductsApi productsApi;
        private ILogger<MessageService> Logger;
        private AsyncUserLockService lockService;
        private SettingsService settingsService;
        private IConfiguration config;
        private PremiumService premiumService;

        public MessageService(EventDbContext db, ConnectionMultiplexer connection, Payments.Client.Api.ProductsApi productsApi,
                        ILogger<MessageService> logger, AsyncUserLockService lockService, SettingsService settingsService, IConfiguration config, PremiumService premiumService)
        {
            this.db = db;
            this.connection = connection;
            this.productsApi = productsApi;
            Logger = logger;
            this.lockService = lockService;
            this.settingsService = settingsService;
            this.config = config;
            this.premiumService = premiumService;
        }

        public async Task<MessageContainer> AddMessage(MessageContainer message)
        {
            if (message.Timestamp == default || message.Timestamp < DateTime.Now - TimeSpan.FromDays(1))
            {
                message.Timestamp = DateTime.Now;
            }
            if (string.IsNullOrEmpty(message.Reference))
                message.Reference = Guid.NewGuid().ToString().Replace("-", "");
            var subs = await db.Subscriptions.Where(s => (s.SourceType == message.SourceType || s.SourceType == "*") && s.UserId == message.User.UserId).Include(s => s.Targets).ThenInclude(t => t.Target).ToListAsync();
            var pubsub = connection.GetSubscriber();
            var serialized = JsonConvert.SerializeObject(message);
            var receivedCount = 1L;
            if (!IsInGameDeactivated(subs))
                receivedCount = await pubsub.PublishAsync(RedisChannel.Literal("uev" + message.User.UserId), serialized);
            Logger.LogInformation("published for {user} source {source} count {count}", message.User.UserId, message.SourceType, receivedCount);
            foreach (var sub in subs)
            {
                foreach (var target in sub.Targets)
                {
                    await SendToTarget(message, target.Target);
                }
            }
            // message has been received by someone and can be dropped
            if (receivedCount > 0 || (!message.Setings?.StoreIfOffline ?? true) || message.Id != 0)
                return message;

            // not sure if someone received the message, store it
            try
            {
                db.Messages.Add(message);
                await db.SaveChangesAsync();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error while saving message {message}", JsonConvert.SerializeObject(message));
            }

            return message;
        }

        private static bool IsInGameDeactivated(List<Subscription> subs)
        {
            return subs.Any(s => s.Targets.Any(t => t.Target.Type == NotificationTarget.TargetType.InGame && t.Target.When.HasFlag(NotificationTarget.NotifyWhen.NEVER)));
        }

        /// <summary>
        /// Sends the message to the target
        /// </summary>
        /// <param name="message"></param>
        /// <param name="target"></param>
        /// <returns>If the message was sent successfully</returns>
        public async Task<bool> SendToTarget(MessageContainer message, NotificationTarget target)
        {
            if (target.Type == NotificationTarget.TargetType.DiscordWebhook)
            {
                return await SendWebhook(message, target);
            }
            if (target.Type == NotificationTarget.TargetType.FIREBASE)
            {
                return await SendFirebase(message, target);
            }
            return false;
        }

        /// <summary>
        /// Attempts to send a notification
        /// </summary>
        /// <param name="message"></param>
        /// <param name="target"></param>
        /// <returns><c>true</c> when the notification was sent successfully</returns>
        public async Task<bool> SendFirebase(MessageContainer message, NotificationTarget target)
        {
            string firebaseKey = config["FIREBASE_KEY"];
            string firebaseSenderId = config["FIREBASE_SENDER_ID"];
            try
            {
                var notification = new FirebaseNotification(message.Summary, message.Message, message.Link, message.ImageLink, null, message.Data as Dictionary<string, string>);
                // Get the server key from FCM console
                var serverKey = string.Format("key={0}", firebaseKey);

                // Get the sender id from FCM console
                var senderId = string.Format("id={0}", firebaseSenderId);

                //var icon = "https://sky.coflnet.com/logo192.png";
                var data = notification.data;
                var payload = new
                {
                    target.Target, // Recipient device token
                    notification,
                    data
                };

                // Using Newtonsoft.Json
                var jsonBody = JsonConvert.SerializeObject(payload);
                var client = new RestClient("https://fcm.googleapis.com");
                var request = new RestRequest("fcm/send", Method.Post);

                request.AddHeader("Authorization", serverKey);
                request.AddHeader("Sender", senderId); request.AddHeader("Content-Type", "application/json");
                request.AddParameter("application/json", jsonBody, ParameterType.RequestBody);
                var response = await client.ExecuteAsync(request);


                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    Console.WriteLine(JsonConvert.SerializeObject(response.Content));
                }

                dynamic res = JsonConvert.DeserializeObject(response.Content);
                var success = res.success == 1;
                if (!success)
                    dev.Logger.Instance.Error(response.Content);

                return success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Couldn't send notification");
            }

            return false;
        }

        private async Task<bool> SendWebhook(MessageContainer message, NotificationTarget target)
        {
            var url = target.Target;
            var client = new System.Net.Http.HttpClient();
            if (!(Uri.TryCreate(message.Link, UriKind.Absolute, out var uriResult) && uriResult.Scheme == Uri.UriSchemeHttp))
            {
                message.Link = "https://sky.coflnet.com";
            }
            var body = JsonConvert.SerializeObject(new { embeds = new[] { new { description = message.Message, url = message.Link, title = message.Summary } } });
            var response = await client.PostAsync(url, new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"));
            Logger.LogInformation("sent to {webhook}\n{body}\n {response} {content}", url, body, response.StatusCode, response.Content.ReadAsStringAsync().Result);
            return response.StatusCode <= System.Net.HttpStatusCode.NoContent;
        }

        internal Task Received(string refence)
        {
            db.Confirms.Add(new ReceiveConfirm() { Reference = refence, Timestamp = DateTime.UtcNow });
            return db.SaveChangesAsync();
        }

        internal async Task NewTransaction(TransactionEvent lp)
        {
            if (lp.UserId == null)
                return;
            var message = $"Your topup of {FormatCoins(lp.Amount)} CoflCoins was received";
            if (lp.Amount < 1800)
                message = $"You received {FormatCoins(lp.Amount)} CoflCoins";
            if (lp.ProductSlug == "transfer")
                message = $"You received {FormatCoins(lp.Amount)} CoflCoins from someone";
            if (lp.ProductSlug == "compensation")
                if (lp.Amount < 0)
                    message = $"{FormatCoins(lp.Amount)} CoflCoins were deducted from your account for {lp.Reference}";
                else
                    message = $"You received {FormatCoins(lp.Amount)} CoflCoins as compensation for {lp.Reference}";
            if (lp.ProductSlug == config["PRODUCTS:VERIFY_MC"])
                return;
            if (lp.ProductSlug == config["PRODUCTS:REFERRAL_BONUS"])
                message = $"You received {FormatCoins(lp.Amount)} CoflCoins from the referral system";
            var sourceType = "topup";
            if (lp.ProductSlug == config["PRODUCTS:TEST_PREMIUM"])
            {
                var product = await productsApi.ProductsPProductSlugGetAsync(lp.ProductSlug);
                var timeInDays = TimeSpan.FromSeconds(product.OwnershipSeconds).TotalDays;
                message = $"You received {timeInDays} days of test premium for verifying your minecraft account";
            }
            else if (lp.Amount < 0)
            {
                var product = await productsApi.ProductsPProductSlugGetAsync(lp.ProductSlug);
                message = $"You purchased {product?.Title ?? lp.ProductSlug}";
                sourceType = "purchase";
            }

            if (lp.Timestamp > DateTime.UtcNow - TimeSpan.FromHours(1))
                await AddMessage(new MessageContainer()
                {
                    Data = lp,
                    Message = message,
                    Reference = "transaction" + lp.Id,
                    SourceType = sourceType,
                    Setings = new Models.Settings() { ConfirmDelivery = true, PlaySound = true },
                    User = new Models.User()
                    {
                        UserId = lp.UserId
                    }
                });

            await lockService.GetLock(lp.UserId, async (u) =>
            {
                Logger.LogInformation("handling transaction for {user}", lp.UserId);
                var current = await settingsService.GetCurrentValue<AccountInfo>(u, "accountInfo", default);

                if (current == null)
                {
                    Logger.LogInformation($"No account info found for {u}");
                    return;
                }
                if (config["PRODUCTS:PREMIUM"] == lp.ProductSlug || config["PRODUCTS:TEST_PREMIUM"] == lp.ProductSlug)
                {
                    Logger.LogInformation("changing premium time for {user}", lp.UserId);

                    var when = await premiumService.ExpiresWhen(lp.UserId);
                    if (when > DateTime.Now)
                    {
                        current.ExpiresAt = when;
                        current.Tier = AccountTier.PREMIUM;
                        await settingsService.UpdateSetting(u, "accountInfo", current);
                    }
                }
            });
        }

        private static string FormatCoins(double amount)
        {
            return string.Format("{0:n0}", Convert.ToInt32(amount));
        }

        internal async Task<IEnumerable<MessageContainer>> GetMessages(string userId)
        {
            return await db.Messages.Where(m => m.User.UserId == userId).Include(m => m.Setings).ToListAsync();
        }

        internal async Task<int> CleanDb()
        {
            var minTime = DateTime.UtcNow - TimeSpan.FromMinutes(1);
            var oldestTime = DateTime.UtcNow - TimeSpan.FromDays(30);
            var old = await db.Messages.Where(m => m.Timestamp < minTime && !m.Setings.StoreIfOffline || m.Timestamp < oldestTime).Include(m => m.Setings).Include(m => m.User).ToListAsync();
            db.RemoveRange(old.Select(o => o.Setings).Where(s => s != null));
            db.RemoveRange(old.Select(o => o.User).Where(u => u != null));
            db.Messages.RemoveRange(old);
            var remCount = await db.SaveChangesAsync();
            Logger.LogInformation("Removed {remCount} message from db", remCount);
            return remCount;
        }

        internal async Task Verified(string userId, string minecraftUuid, int verifiedCount)
        {
            await AddMessage(new MessageContainer()
            {
                Data = minecraftUuid,
                Message = "You successfully verified your minecraft account",
                Reference = minecraftUuid,
                SourceType = "mcVerify",
                Setings = new Models.Settings() { ConfirmDelivery = true, PlaySound = true },
                User = new Models.User()
                {
                    UserId = userId
                }
            });

            if (verifiedCount != 0)
            {
                await VerifiedAlready(userId, minecraftUuid);
                return;
            }

            await lockService.GetLock(userId, async (u) =>
            {
                var current = await settingsService.GetCurrentValue<AccountInfo>(u, "accountInfo", () => null);
                if (current == null)
                {
                    Logger.LogInformation($"No account info found for {userId}");
                    return;
                }
                current.McIds.Add(minecraftUuid);
                await settingsService.UpdateSetting(u, "accountInfo", current);
            });
        }

        internal async Task VerifiedAlready(string userId, string minecraftUuid)
        {
            await AddMessage(new MessageContainer()
            {
                Data = minecraftUuid,
                Message = "Since the verification bonus was already claimed for your minecraft account it won't be awarded again.",
                Reference = minecraftUuid,
                SourceType = "mcVerify",
                Setings = new Models.Settings() { ConfirmDelivery = true, PlaySound = true },
                User = new Models.User()
                {
                    UserId = userId
                }
            });
        }
    }
}
