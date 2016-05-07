using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jackett.Models.IndexerConfig.Bespoke
{
    public class ConfigurationDataSoap4Me : ConfigurationDataBasicLogin
    {
        [JsonProperty]
        public StringItem Url { get; private set; }

        public ConfigurationDataSoap4Me()
        {
        }

        public ConfigurationDataSoap4Me(string defaultUrl)
        {
            Url = new StringItem { Name = "Url", Value = defaultUrl };
        }
    }
}
