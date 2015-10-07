using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreTweet;
using CoreTweet.Streaming;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PushBase64Tweet
{
    public class Program
    {
        public int Main(string[] args)
        {
            string configFile;
            if (args.Length == 1)
                configFile = args[0];
            else if (args.Length > 1)
            {
                Console.WriteLine("Invalid parameters");
                return 1;
            }
            else
                configFile = "config.json";
            this.config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configFile));

            Task.WhenAny(this.ReceivePushbulletEvents(), this.ReceiveTweets()).Wait();

            return 0;
        }

        private Config config;
        private long modifiedAfter;

        private readonly HashSet<long> tweetedIds = new HashSet<long>();

        private void UpdateModifiedAfter()
        {
            this.modifiedAfter = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond) - 62135596800;
        }

        private static void Log(string s)
        {
            Console.WriteLine("[{0:yyyy/MM/dd HH:mm:ss}] {1}", DateTime.Now, s);
        }

        private async Task ReceivePushbulletEvents()
        {
            while (true)
            {
                try
                {
                    using (var client = new ClientWebSocket())
                    {
                        await client.ConnectAsync(new Uri("wss://stream.pushbullet.com/websocket/" + this.config.PushbulletAccessToken), CancellationToken.None);
                        Log("WebSocket Connected");
                        this.UpdateModifiedAfter();

                        var buf = new byte[2048];
                        var bufSegm = new ArraySegment<byte>(buf);
                        while (true)
                        {
                            var result = await client.ReceiveAsync(bufSegm, CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                Log("WebSocket Closed: " + result.CloseStatusDescription);
                                goto CLOSED;
                            }

                            try
                            {
                                var s = Encoding.UTF8.GetString(buf, 0, result.Count);
                                var j = JObject.Parse(s);
                                if ((string)j["type"] == "tickle" && (string)j["subtype"] == "push")
                                    await FetchAndTweet();
                            }
                            catch (Exception ex)
                            {
                                Log("Fetch Failed: " + ex.ToString());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(ex.ToString());
                }

                CLOSED:
                await Task.Delay(3000);
            }
        }

        private HttpClient GetHttpClient()
        {
            var client = new HttpClient();
            client.BaseAddress = new Uri("https://api.pushbullet.com/v2/");
            client.DefaultRequestHeaders.Add("Access-Token", this.config.PushbulletAccessToken);
            return client;
        }

        private Tokens GetTwitterClient()
        {
            return Tokens.Create(
                this.config.TwitterConsumerKey,
                this.config.TwitterConsumerSecret,
                this.config.TwitterAccessToken,
                this.config.TwitterAccessTokenSecret);
        }

        private async Task FetchAndTweet()
        {
            using (var client = this.GetHttpClient())
            {
                this.UpdateModifiedAfter();
                var s = await client.GetStringAsync("pushes?active=true&limit=1&modified_after=" + this.modifiedAfter.ToString("D"));
                var pushes = JObject.Parse(s)["pushes"];
                foreach (var p in pushes)
                {
                    if ((string)p["receiver_email"] == "base64tweet@azyobuzi.net" && (string)p["type"] == "note")
                    {
                        var body = (string)p["body"];
                        Log("Tweeting: " + body);
                        var bytes = Encoding.UTF8.GetBytes(body);
                        const int bytesPerTweet = 105;
                        var t = this.GetTwitterClient();
                        try
                        {
                            for (var i = 0; i < bytes.Length; i += bytesPerTweet)
                            {
                                var res = await t.Statuses.UpdateAsync(
                                    Convert.ToBase64String(bytes, i, Math.Min(bytesPerTweet, bytes.Length - i)));
                                this.tweetedIds.Add(res.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log("Tweet Failed: " + ex.ToString());
                        }
                    }
                }
            }
        }

        private async Task ReceiveTweets()
        {
            var observable = Observable.Create<StreamingMessage>(x =>
            {
                Log("Connecting User Stream");
                return this.GetTwitterClient().Streaming.UserAsObservable().Subscribe(x);
            });
            await observable.Catch(observable.DelaySubscription(TimeSpan.FromSeconds(10)).Retry())
                .Repeat()
                .OfType<StatusMessage>()
                .Do(m => this.OnReceivedTweet(m.Status));
        }

        private static readonly Encoding DecodeEncoding = new UTF8Encoding(false, true);

        private async void OnReceivedTweet(Status status)
        {
            if (this.tweetedIds.Remove(status.Id)) return;

            var text = status.Text;
            while (true)
            {
                try
                {
                    var b = Convert.FromBase64String(text);
                    text = DecodeEncoding.GetString(b);
                }
                catch { return; }

                Log("Pushing: " + text);

                var j = new JObject
                {
                    ["type"] = "link",
                    ["title"] = status.User.ScreenName,
                    ["body"] = text,
                    ["url"] = $"https://twitter.com/{status.User.ScreenName}/status/{status.Id:D}"
                };

                try
                {
                    using (var client = this.GetHttpClient())
                    using (var res = await client.PostAsync("pushes", new StringContent(j.ToString(), DecodeEncoding, "application/json")))
                    {
                        if (!res.IsSuccessStatusCode)
                        {
                            Log($"Push Failed: {res.StatusCode}\n{await res.Content.ReadAsStringAsync()}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Push Failed: " + ex.ToString());
                }
            }
        }
    }
}
