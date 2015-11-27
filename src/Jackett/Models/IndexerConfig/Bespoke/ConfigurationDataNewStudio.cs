using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jackett.Models.IndexerConfig.Bespoke
{
    public class ConfigurationDataNewStudio : ConfigurationDataBasicLogin
    {
        [JsonProperty]
        public StringItem Url { get; private set; }
        [JsonProperty]
        public BoolItem StripRussian { get; private set; }

        public ConfigurationDataNewStudio()
        {
        }

        public ConfigurationDataNewStudio(string defaultUrl)
        {
            Url = new StringItem { Name = "Url", Value = defaultUrl };
            StripRussian = new BoolItem() { Name = "StripRusNamePrefix", Value = true };
        }
    }
}
