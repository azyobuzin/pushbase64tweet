using Newtonsoft.Json;

namespace PushBase64Tweet
{
    [JsonObject]
    public class Config
    {
        [JsonProperty]
        public string TwitterConsumerKey { get; set; }
        [JsonProperty]
        public string TwitterConsumerSecret { get; set; }
        [JsonProperty]
        public string TwitterAccessToken { get; set; }
        [JsonProperty]
        public string TwitterAccessTokenSecret { get; set; }
        [JsonProperty]
        public string PushbulletAccessToken { get; set; }
    }
}
