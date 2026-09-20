using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Controllers;
using NoonScraper.Data.Models;
using NoonScraper.Data.Notifications;

namespace NoonScraper.Tests;

public class TelegramWebhookTests
{
    private const string Secret = "s3cret-value-123";

    private static Dictionary<string, string?> WithSecret() => new() { ["Telegram:WebhookSecret"] = Secret };

    private static string Update(string text, long chatId = 42, string chatType = "private") =>
        $$"""
        { "update_id": 1, "message": { "message_id": 1, "chat": { "id": {{chatId}}, "type": "{{chatType}}" }, "text": {{System.Text.Json.JsonSerializer.Serialize(text)}} } }
        """;

    private static async Task<HttpResponseMessage> PostAsync(ApiFactory factory, string body, string? secret = Secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (secret is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Telegram-Bot-Api-Secret-Token", secret);
        }

        return await factory.CreateClient().SendAsync(request);
    }

    private static int ProductWithPrice(ApiFactory factory, decimal? price = 199, bool active = true, string sku = "N1")
    {
        var id = 0;
        factory.Seed(db =>
        {
            var product = TestDb.AddProduct(db, $"https://www.noon.com/egypt-en/item/{sku}/p/", "Anker Charger", isActive: active);
            id = product.Id;
            if (price is not null)
            {
                TestDb.AddSnapshot(db, product, price.Value, crawledAt: DateTimeOffset.UtcNow.AddDays(-1));
            }
        });
        return id;
    }

    private static List<NotificationSubscription> Subscriptions(ApiFactory factory) =>
        factory.Query(db => db.NotificationSubscriptions.AsNoTracking().ToList());

    public class Authentication
    {
        // A public URL with nothing to check callers against is open to anyone, so the
        // endpoint is simply off until a secret is configured.
        [Fact]
        public async Task With_no_secret_configured_in_production_the_endpoint_refuses()
        {
            using var factory = new ApiFactory(environment: "Production");
            var productId = ProductWithPrice(factory);

            var response = await PostAsync(factory, Update($"/start {productId}"), secret: null);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(Subscriptions(factory));
            Assert.Empty(factory.Telegram.Sent);
        }

        [Fact]
        public async Task With_no_secret_configured_a_request_carrying_a_header_is_refused_too()
        {
            using var factory = new ApiFactory(environment: "Production");
            var productId = ProductWithPrice(factory);

            var response = await PostAsync(factory, Update($"/start {productId}"), secret: "anything");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        [Fact]
        public async Task In_local_development_no_secret_is_needed()
        {
            using var factory = new ApiFactory(environment: "Development");
            var productId = ProductWithPrice(factory);

            var response = await PostAsync(factory, Update($"/start {productId}"), secret: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(Subscriptions(factory));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("wrong")]
        [InlineData("s3cret-value-12")]
        [InlineData("s3cret-value-1234")]
        [InlineData("S3CRET-VALUE-123")]
        [InlineData(" s3cret-value-123")]
        public async Task A_missing_or_wrong_secret_is_unauthorized_and_changes_nothing(string? supplied)
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);

            var response = await PostAsync(factory, Update($"/start {productId}"), supplied);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(Subscriptions(factory));
            Assert.Empty(factory.Telegram.Sent);
            Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task The_right_secret_is_accepted()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);

            var response = await PostAsync(factory, Update($"/start {productId}"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task The_secret_is_checked_before_the_body_is_looked_at()
        {
            using var factory = new ApiFactory(WithSecret());

            // A wrong secret with a malformed-but-parseable body still gets a 401, not
            // a hint about the body's shape.
            var response = await PostAsync(factory, "{}", "wrong");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    public class Subscribing
    {
        [Fact]
        public async Task Start_subscribes_the_chat_with_the_current_price_as_baseline()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory, 199);

            var response = await PostAsync(factory, Update($"/start {productId}", chatId: 4242));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var subscription = Assert.Single(Subscriptions(factory));
            Assert.Equal((productId, 4242L, 199m), (subscription.ProductId, subscription.TelegramChatId, subscription.LastNotifiedPrice));
            var (chat, text) = Assert.Single(factory.Telegram.Sent);
            Assert.Equal(4242, chat);
            Assert.Contains("Anker Charger", text);
        }

        [Fact]
        public async Task A_product_never_crawled_has_no_baseline_yet()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory, price: null);

            await PostAsync(factory, Update($"/start {productId}"));

            Assert.Null(Assert.Single(Subscriptions(factory)).LastNotifiedPrice);
        }

        [Fact]
        public async Task Subscribing_twice_is_harmless()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);

            await PostAsync(factory, Update($"/start {productId}"));
            var again = await PostAsync(factory, Update($"/start {productId}"));

            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Single(Subscriptions(factory));
            Assert.Equal(2, factory.Telegram.Sent.Count);
        }

        // Telegram redelivers webhooks, and a user can double-tap Start.
        [Fact]
        public async Task Simultaneous_duplicate_starts_create_one_subscription_and_no_errors()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);

            var responses = await Task.WhenAll(Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => PostAsync(factory, Update($"/start {productId}")))));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Single(Subscriptions(factory));
        }

        [Theory]
        [InlineData("/start {id}")]
        [InlineData("/start   {id}")]
        [InlineData("/start {id}  ")]
        [InlineData("  /start {id}")]
        [InlineData("/START {id}")]
        [InlineData("/start@NoonScraperTrackerBot {id}")]
        public async Task The_command_is_recognized_however_it_is_spelled(string template)
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);

            await PostAsync(factory, Update(template.Replace("{id}", productId.ToString())));

            Assert.Single(Subscriptions(factory));
        }

        [Theory]
        [InlineData("/start")]
        [InlineData("/start abc")]
        [InlineData("/start 0")]
        [InlineData("/start -3")]
        [InlineData("/start 99999999999999999999")]
        [InlineData("/start 1 2")]
        [InlineData("/start 1.5")]
        [InlineData("/start '; DROP TABLE Products;--")]
        public async Task A_missing_or_malformed_product_id_gets_help_and_creates_nothing(string text)
        {
            using var factory = new ApiFactory(WithSecret());
            ProductWithPrice(factory);

            var response = await PostAsync(factory, Update(text));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Subscriptions(factory));
            Assert.Contains("Notify me", Assert.Single(factory.Telegram.Sent).Text);
            Assert.Equal(1, factory.Query(db => db.Products.Count()));
        }

        [Fact]
        public async Task An_unknown_product_gets_a_polite_refusal()
        {
            using var factory = new ApiFactory(WithSecret());

            await PostAsync(factory, Update("/start 9999"));

            Assert.Empty(Subscriptions(factory));
            Assert.Contains("doesn't exist", Assert.Single(factory.Telegram.Sent).Text);
        }

        [Fact]
        public async Task An_untracked_product_cannot_be_subscribed_to()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory, active: false);

            await PostAsync(factory, Update($"/start {productId}"));

            Assert.Empty(Subscriptions(factory));
        }

        [Fact]
        public async Task A_failure_to_send_the_reply_does_not_undo_the_subscription_or_fail_the_webhook()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);
            factory.Telegram.Outcome = _ => SendOutcome.Failed;

            var response = await PostAsync(factory, Update($"/start {productId}"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(Subscriptions(factory));
        }

        [Fact]
        public async Task Very_large_chat_ids_work()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);

            await PostAsync(factory, Update($"/start {productId}", chatId: long.MaxValue));

            Assert.Equal(long.MaxValue, Assert.Single(Subscriptions(factory)).TelegramChatId);
        }

        [Fact]
        public async Task A_chat_can_follow_only_so_many_products()
        {
            using var factory = new ApiFactory(WithSecret());
            var ids = Enumerable.Range(1, TelegramController.MaxSubscriptionsPerChat + 1)
                .Select(i => ProductWithPrice(factory, sku: $"N{i}")).ToList();

            foreach (var id in ids.Take(TelegramController.MaxSubscriptionsPerChat))
            {
                await PostAsync(factory, Update($"/start {id}"));
            }

            await PostAsync(factory, Update($"/start {ids.Last()}"));

            Assert.Equal(TelegramController.MaxSubscriptionsPerChat, Subscriptions(factory).Count);
            Assert.Contains("maximum", factory.Telegram.Sent.Last().Text);
        }

        [Fact]
        public async Task Re_subscribing_at_the_cap_to_something_already_followed_still_confirms()
        {
            using var factory = new ApiFactory(WithSecret());
            var ids = Enumerable.Range(1, TelegramController.MaxSubscriptionsPerChat)
                .Select(i => ProductWithPrice(factory, sku: $"N{i}")).ToList();
            foreach (var id in ids)
            {
                await PostAsync(factory, Update($"/start {id}"));
            }

            await PostAsync(factory, Update($"/start {ids[0]}"));

            Assert.Contains("now tracking", factory.Telegram.Sent.Last().Text);
        }

        [Fact]
        public async Task The_cap_is_per_chat()
        {
            using var factory = new ApiFactory(WithSecret());
            var ids = Enumerable.Range(1, TelegramController.MaxSubscriptionsPerChat)
                .Select(i => ProductWithPrice(factory, sku: $"N{i}")).ToList();
            foreach (var id in ids)
            {
                await PostAsync(factory, Update($"/start {id}", chatId: 1));
            }

            await PostAsync(factory, Update($"/start {ids[0]}", chatId: 2));

            Assert.Equal(TelegramController.MaxSubscriptionsPerChat + 1, Subscriptions(factory).Count);
        }
    }

    public class Unsubscribing
    {
        [Fact]
        public async Task Stop_removes_every_subscription_for_that_chat_only()
        {
            using var factory = new ApiFactory(WithSecret());
            var a = ProductWithPrice(factory, sku: "N1");
            var b = ProductWithPrice(factory, sku: "N2");
            await PostAsync(factory, Update($"/start {a}", chatId: 1));
            await PostAsync(factory, Update($"/start {b}", chatId: 1));
            await PostAsync(factory, Update($"/start {a}", chatId: 2));

            await PostAsync(factory, Update("/stop", chatId: 1));

            var remaining = Assert.Single(Subscriptions(factory));
            Assert.Equal(2, remaining.TelegramChatId);
            Assert.Contains("Unsubscribed from all 2", factory.Telegram.Sent.Last().Text);
        }

        [Theory]
        [InlineData("/stop")]
        [InlineData("/STOP")]
        [InlineData("/stop@NoonScraperTrackerBot")]
        [InlineData("  /stop  ")]
        public async Task Stop_is_recognized_however_it_is_spelled(string text)
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);
            await PostAsync(factory, Update($"/start {productId}"));

            await PostAsync(factory, Update(text));

            Assert.Empty(Subscriptions(factory));
        }

        [Fact]
        public async Task Stop_with_nothing_subscribed_is_fine()
        {
            using var factory = new ApiFactory(WithSecret());

            var response = await PostAsync(factory, Update("/stop"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("0 product", factory.Telegram.Sent.Single().Text);
        }

        [Fact]
        public async Task Deleting_a_product_takes_its_subscriptions_with_it()
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);
            await PostAsync(factory, Update($"/start {productId}"));

            factory.Seed(db => db.Products.Where(p => p.Id == productId).ExecuteDelete());

            Assert.Empty(Subscriptions(factory));
            await PostAsync(factory, Update($"/start {productId}"));
            Assert.Empty(Subscriptions(factory));
        }
    }

    public class UntrustedPayloads
    {
        [Theory]
        [InlineData("group")]
        [InlineData("supergroup")]
        [InlineData("channel")]
        [InlineData("")]
        public async Task Only_private_chats_are_served(string chatType)
        {
            using var factory = new ApiFactory(WithSecret());
            var productId = ProductWithPrice(factory);

            var response = await PostAsync(factory, Update($"/start {productId}", chatType: chatType));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Subscriptions(factory));
            Assert.Empty(factory.Telegram.Sent);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"message\": null}")]
        [InlineData("{\"message\": {}}")]
        [InlineData("{\"message\": {\"text\": \"/start 1\"}}")]
        [InlineData("{\"message\": {\"chat\": {\"id\": 1, \"type\": \"private\"}}}")]
        [InlineData("{\"message\": {\"chat\": null, \"text\": \"/start 1\"}}")]
        [InlineData("{\"edited_message\": {\"chat\": {\"id\": 1, \"type\": \"private\"}, \"text\": \"/start 1\"}}")]
        [InlineData("{\"callback_query\": {\"id\": \"1\"}}")]
        [InlineData("{\"message\": {\"chat\": {\"id\": 1, \"type\": \"private\"}, \"text\": \"\"}}")]
        public async Task Updates_that_are_not_a_text_message_in_a_chat_are_acknowledged_and_ignored(string body)
        {
            using var factory = new ApiFactory(WithSecret());
            ProductWithPrice(factory);

            var response = await PostAsync(factory, body);

            // 200 so Telegram doesn't keep redelivering something we'll never act on.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Subscriptions(factory));
            Assert.Empty(factory.Telegram.Sent);
        }

        [Theory]
        [InlineData("/help")]
        [InlineData("hello")]
        [InlineData("/starting 1")]
        [InlineData("/stopnow")]
        [InlineData("start 1")]
        public async Task Other_text_is_ignored(string text)
        {
            using var factory = new ApiFactory(WithSecret());
            ProductWithPrice(factory);

            var response = await PostAsync(factory, Update(text));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Subscriptions(factory));
            Assert.Empty(factory.Telegram.Sent);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[")]
        public async Task A_body_that_is_not_json_is_a_400(string body)
        {
            using var factory = new ApiFactory(WithSecret());

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(factory, body)).StatusCode);
        }

        [Fact]
        public async Task A_very_long_message_is_handled()
        {
            using var factory = new ApiFactory(WithSecret());

            var response = await PostAsync(factory, Update("/start " + new string('9', 20000)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Subscriptions(factory));
        }
    }
}
